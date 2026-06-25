using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using SharpDX;
using SharpDX.Mathematics.Interop;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using SharpDX.XAudio2.Fx;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2ReverbDiagnosticPing
    {
        private static readonly BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int WetBusSampleRate = 48000;
        private const int WetBusChannels = 2;
        private const float MinLateFieldSeedDb = -40f;
        private static readonly List<PingState> ActivePings = new List<PingState>();
        private static readonly List<CueState> ActiveCues = new List<CueState>();
        private static readonly Dictionary<string, SharedWetBusState> SharedCueBuses = new Dictionary<string, SharedWetBusState>(StringComparer.Ordinal);
        private static readonly Dictionary<IMySourceVoice, DateTime> AutoWetSourceVoices = new Dictionary<IMySourceVoice, DateTime>();
        private static readonly Dictionary<string, DateTime> AutoWetCueCooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan AutoWetCueCooldown = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan AutoWetSourceLifetime = TimeSpan.FromSeconds(8);
        private static DateTime _lastAutoWetLogUtc = DateTime.MinValue;
        private static string _lastStatus = "reverbPing=not-run";
        private static string _lastAppliedParameterStatus = "xParams=not-applied";

        public static string LastStatus => _lastStatus;

        public static string FormatWetBusStatus()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            string parameterStatus = DescribeParameters(CreateParameters(settings));
            return string.Format(
                CultureInfo.InvariantCulture,
                "wetBus active={0} pings={1} cues={2} shared={3} sends={4} send={5:0.00} mode={6} | {7} | last={8}",
                ActivePings.Count + ActiveCues.Count + SharedCueBuses.Count,
                ActivePings.Count,
                ActiveCues.Count,
                SharedCueBuses.Count,
                AutoWetSourceVoices.Count,
                settings?.GlobalReverbWetSend ?? 1f,
                DescribeExpectedMode(settings),
                parameterStatus,
                _lastAppliedParameterStatus);
        }

        public static string Play()
        {
            return PlayInternal(null, false);
        }

        public static string PlayPreset(string presetName)
        {
            return PlayInternal(presetName, false);
        }

        public static string PlayXapo()
        {
            const string status = "reverbXapo=unsupported: XAPO.Fx.Reverb does not support XAudio2.9";
            V2DebugLog.WriteEvent("reverb-xapo", status);
            return SetStatus(status);
        }

        private static string PlayInternal(string presetName, bool useSimpleXapo)
        {
            object audio = MyAudio.Static;
            if (audio == null)
                return SetStatus("reverbPing=audio-missing");

            object engineObject = audio.GetType().GetField("m_audioEngine", InstanceMembers)?.GetValue(audio);
            XAudio2 engine = engineObject as XAudio2;
            if (engine == null)
                return SetStatus("reverbPing=xaudio-missing");

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float diffusion = Clamp01(settings?.GlobalReverbDiffusion ?? 0.5f);
            float roomSize = Clamp01(settings?.GlobalReverbRoomSize ?? 0.8f);
            bool usePreset = !string.IsNullOrWhiteSpace(presetName);
            string signature = useSimpleXapo ? "xapo-simple" : usePreset ? "preset:" + presetName.Trim().ToLowerInvariant() : BuildParameterSignature(settings);

            SourceVoice source = null;
            SubmixVoice bus = null;
            DataStream stream = null;
            SharpDX.XAPO.AudioProcessor reverb = null;
            try
            {
                const int sampleRate = 48000;
                const int channels = WetBusChannels;
                const float burstSeconds = 0.025f;
                string canonicalPreset = string.Empty;
                string effectLabel = useSimpleXapo ? "XAPO" : usePreset ? "Preset" : "XAudio2FX";
                ReverbParameters parameters = default(ReverbParameters);
                SharpDX.XAPO.Fx.ReverbParameters simpleParameters = default(SharpDX.XAPO.Fx.ReverbParameters);
                TimeSpan tailLifetime;
                if (useSimpleXapo)
                {
                    simpleParameters = CreateSimpleXapoParameters(settings);
                    tailLifetime = TimeSpan.FromSeconds(12);
                }
                else
                {
                    parameters = usePreset
                        ? CreatePresetParameters(presetName, out canonicalPreset)
                        : CreateParameters(settings);
                    tailLifetime = usePreset
                        ? CalculateParameterTailLifetime(parameters)
                        : CalculateSharedBusIdleLifetime(settings);
                }
                float durationSeconds = (float)tailLifetime.TotalSeconds;

                WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                float[] samples = CreateBurstSamples(sampleRate, channels, durationSeconds, burstSeconds);
                stream = new DataStream(samples.Length * sizeof(float), true, true);
                stream.WriteRange(samples);
                stream.Position = 0;

                AudioBuffer buffer = new AudioBuffer(stream)
                {
                    Flags = BufferFlags.EndOfStream
                };

                reverb = useSimpleXapo ? CreateSimpleXapoReverbEffect(engine, out string reverbCtor) : CreateReverbEffect(engine, out reverbCtor);
                EffectDescriptor descriptor = new EffectDescriptor(reverb, channels);
                bus = new SubmixVoice(engine, channels, sampleRate);
                bus.SetEffectChain(new[] { descriptor });
                if (useSimpleXapo)
                    bus.SetEffectParameters(0, simpleParameters);
                else
                    bus.SetEffectParameters(0, parameters);
                bus.EnableEffect(0);
                bus.IsEffectEnabled(0, out RawBool enabledRaw);
                bool enabled = enabledRaw;
                _lastAppliedParameterStatus = useSimpleXapo
                    ? "xapo " + DescribeSimpleXapoParameters(simpleParameters)
                    : (usePreset ? "preset " + canonicalPreset + " " : "ping ") + DescribeParameters(parameters);

                source = new SourceVoice(engine, format);
                source.SetOutputVoices(new[] { new VoiceSendDescriptor(bus) });
                TrySetOutputMatrix(source, bus, channels, channels, out string matrixStatus);
                source.SetVolume(CalculateWetVolume(0.9f, settings), 0);
                source.SubmitSourceBuffer(buffer, null);
                source.Start();

                ActivePings.Add(new PingState
                {
                    Source = source,
                    Bus = bus,
                    Stream = stream,
                    Reverb = reverb,
                    Signature = signature,
                    SourceBaseVolume = 0.9f,
                    ExpiresUtc = DateTime.UtcNow + tailLifetime,
                    CreatedUtc = DateTime.UtcNow,
                    FixedParameters = usePreset || useSimpleXapo
                });

                string status = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}=played bus=48k/{1}ch effect={2} ctor={3} kind={4} diff={5:0.00} room={6:0.00} source=48k/{7}ch burst=25ms keepAlive={8:0.0}s wet=100 matrix={9}",
                    useSimpleXapo ? "reverbXapo" : usePreset ? "reverbPreset:" + canonicalPreset : "reverbPing",
                    channels,
                    enabled ? "enabled" : "disabled",
                    reverbCtor,
                    effectLabel,
                    diffusion,
                    roomSize,
                    channels,
                    tailLifetime.TotalSeconds,
                    matrixStatus ?? "?");
                V2DebugLog.WriteEvent(useSimpleXapo ? "reverb-xapo" : usePreset ? "reverb-preset" : "reverb-ping", status);
                V2DebugLog.WriteEvent(useSimpleXapo ? "reverb-xapo-params" : usePreset ? "reverb-preset-params" : "reverb-ping-params", _lastAppliedParameterStatus + " expected=" + (useSimpleXapo ? "simple-xapo" : usePreset ? "native-preset" : DescribeExpectedMode(settings)));
                return SetStatus(status);
            }
            catch (Exception ex)
            {
                Dispose(source);
                Dispose(bus);
                Dispose(stream);
                Dispose(reverb);
                string status = (useSimpleXapo ? "reverbXapo=failed:" : usePreset ? "reverbPreset=failed:" : "reverbPing=failed:") + ex.GetType().Name + " " + ex.Message;
                V2DebugLog.WriteEvent(useSimpleXapo ? "reverb-xapo" : usePreset ? "reverb-preset" : "reverb-ping", status);
                return SetStatus(status);
            }
        }

        public static string PlayCue(string cueName)
        {
            return PlayCueInternal(cueName, 1f, "manual", true);
        }

        public static bool TryPlayAutomaticWetSend(IMySourceVoice sourceVoice, string cueName, string category, float wetVolume, out string status)
        {
            status = "autoWet=skip";
            if (sourceVoice == null || !sourceVoice.IsValid || !sourceVoice.IsPlaying || IsOwnedWetVoice(sourceVoice))
                return false;

            cueName = string.IsNullOrWhiteSpace(cueName) ? sourceVoice.CueEnum.ToString() : cueName.Trim();
            if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty")
                return false;

            DateTime now = DateTime.UtcNow;
            PurgeAutoWetSources(now);
            if (AutoWetSourceVoices.ContainsKey(sourceVoice))
            {
                status = "autoWet=source-seen";
                return false;
            }

            if (AutoWetCueCooldowns.TryGetValue(cueName, out DateTime lastCue) && now - lastCue < AutoWetCueCooldown)
            {
                status = "autoWet=cue-cooldown";
                return false;
            }

            AutoWetSourceVoices[sourceVoice] = now;
            AutoWetCueCooldowns[cueName] = now;

            float volume = Clamp(wetVolume, 0.05f, 1.25f);
            status = PlayCueInternal(cueName, volume, "auto-" + (category ?? "?"), false);
            bool played = status.StartsWith("reverbCue=played", StringComparison.OrdinalIgnoreCase);
            if (played && now - _lastAutoWetLogUtc > TimeSpan.FromSeconds(1))
            {
                _lastAutoWetLogUtc = now;
                V2DebugLog.WriteEvent("reverb-wet-auto", status);
            }

            return played;
        }

        public static bool IsOwnedWetVoice(IMySourceVoice voice)
        {
            if (voice == null)
                return false;

            for (int i = 0; i < ActiveCues.Count; i++)
            {
                if (ReferenceEquals(ActiveCues[i].Voice, voice))
                    return true;
            }

            return false;
        }

        private static string PlayCueInternal(string cueName, float wetVolume, string reason, bool updateLastStatus)
        {
            cueName = string.IsNullOrWhiteSpace(cueName) ? "ArcPlayStepsMetal" : cueName.Trim();

            IMyAudio audio = MyAudio.Static;
            if (audio == null)
                return SetCueStatus("reverbCue=audio-missing", updateLastStatus);

            object engineObject = audio.GetType().GetField("m_audioEngine", InstanceMembers)?.GetValue(audio);
            XAudio2 engine = engineObject as XAudio2;
            if (engine == null)
                return SetCueStatus("reverbCue=xaudio-missing", updateLastStatus);

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float diffusion = Clamp01(settings?.GlobalReverbDiffusion ?? 0.5f);
            float roomSize = Clamp01(settings?.GlobalReverbRoomSize ?? 0.8f);
            string signature = BuildParameterSignature(settings);

            IMySourceVoice voice = null;
            SharedWetBusState bus = null;
            VoiceSendDescriptor[] originalOutputVoices = null;
            bool routed = false;

            try
            {
                MyCueId cueId = new MyCueId(MyStringHash.GetOrCompute(cueName));
                try
                {
                    if (audio.GetCue(cueId) == null)
                        return SetCueStatus("reverbCue=cue-missing:" + cueName, updateLastStatus);
                }
                catch
                {
                    return SetCueStatus("reverbCue=cue-lookup-failed:" + cueName, updateLastStatus);
                }

                voice = audio.GetSound(cueId, null, MySoundDimensions.D2);
                if (voice == null)
                    return SetCueStatus("reverbCue=voice-missing:" + cueName, updateLastStatus);

                if (!RspDynamicAudioFilters.TryResolveNativeSourceVoice(voice, out object nativeObject) || !(nativeObject is SourceVoice nativeSource))
                {
                    StopGameVoice(voice, "native-missing");
                    return SetCueStatus("reverbCue=native-missing:" + cueName, updateLastStatus);
                }

                int sourceRate = ResolveVoiceInputSampleRate(nativeSource);
                int sourceChannels = ResolveSourceInputChannelCount(nativeSource);
                int busChannels = WetBusChannels;
                if (!TryEnsureSharedCueBus(engine, busChannels, settings, out bus, out string busStatus))
                {
                    StopGameVoice(voice, "bus-failed");
                    return SetCueStatus("reverbCue=bus-failed:" + busStatus + " cue=" + cueName, updateLastStatus);
                }

                originalOutputVoices = ResolveCurrentOutputVoices(voice);
                if (!TrySetGameVoiceOutputVoices(voice, new[] { new VoiceSendDescriptor(bus.Bus) }, out string routeStatus))
                {
                    StopGameVoice(voice, "route-failed");
                    return SetCueStatus("reverbCue=route-failed:" + routeStatus + " cue=" + cueName, updateLastStatus);
                }

                routed = true;
                TrySetOutputMatrix(nativeSource, bus.Bus, sourceChannels, busChannels, out string matrixStatus);
                voice.VolumeMultiplier = 1f;
                voice.SetVolume(CalculateWetVolume(wetVolume, settings));
                voice.Start(false, false);

                ActiveCues.Add(new CueState
                {
                    Voice = voice,
                    OriginalOutputVoices = originalOutputVoices,
                    CueName = cueName,
                    Reason = reason,
                    BusKey = bus.Key,
                    Signature = signature,
                    WetVolumeBase = wetVolume,
                    CreatedUtc = DateTime.UtcNow
                });

                string status = string.Format(
                    CultureInfo.InvariantCulture,
                    "reverbCue=played cue={0} src={1}Hz/{2}ch bus={3}Hz/{4}ch effect={5} ctor={6} diff={7:0.00} room={8:0.00}",
                    cueName,
                    sourceRate,
                    sourceChannels,
                    WetBusSampleRate,
                    busChannels,
                    bus.EffectEnabled ? "enabled" : "disabled",
                    bus.Constructor,
                    diffusion,
                    roomSize);
                status += string.Format(
                    CultureInfo.InvariantCulture,
                    " wet={0:0.00} send={1:0.00} reason={2} route={3} matrix={4} origOut={5} cues={6} buses={7}",
                    wetVolume,
                    CalculateWetVolume(wetVolume, settings),
                    reason ?? "?",
                    routeStatus,
                    matrixStatus ?? "?",
                    originalOutputVoices?.Length ?? 0,
                    ActiveCues.Count,
                    SharedCueBuses.Count);
                return SetCueStatus(status, updateLastStatus);
            }
            catch (Exception ex)
            {
                if (routed)
                    TryRestoreGameVoiceOutputVoices(voice, originalOutputVoices, out _);
                StopGameVoice(voice, "cue-failed");
                string status = "reverbCue=failed:" + ex.GetType().Name + " " + ex.Message;
                V2DebugLog.WriteEvent("reverb-cue", status + " " + DescribeException(ex));
                return SetCueStatus(status, updateLastStatus);
            }
        }

        private static Reverb CreateReverbEffect(XAudio2 engine, out string constructor)
        {
            Exception first = null;
            try
            {
                constructor = "ctor=false";
                return new Reverb(engine, false);
            }
            catch (Exception ex)
            {
                first = ex;
                V2DebugLog.WriteEvent("reverb-ping", "ctor=false failed " + DescribeException(ex));
            }

            try
            {
                constructor = "ctor=true";
                return new Reverb(engine, true);
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("reverb-ping", "ctor=true failed " + DescribeException(ex));
            }

            try
            {
                constructor = "ctor=default";
                return new Reverb(engine);
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("reverb-ping", "ctor=default failed " + DescribeException(ex));
                throw first ?? ex;
            }
        }

        private static SharpDX.XAPO.AudioProcessor CreateSimpleXapoReverbEffect(XAudio2 engine, out string constructor)
        {
            constructor = "xapo-simple";
            return new SharpDX.XAPO.Fx.Reverb(engine);
        }

        public static void Update()
        {
            DateTime now = DateTime.UtcNow;
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.GlobalReverbEnabled)
            {
                DisposeActiveWetVoices("disabled");
                DisposeSharedCueBuses("disabled");
                AutoWetSourceVoices.Clear();
                AutoWetCueCooldowns.Clear();
                return;
            }
            else
                RefreshSharedCueBuses(settings, now);

            for (int i = ActivePings.Count - 1; i >= 0; i--)
            {
                PingState ping = ActivePings[i];
                if (now < ping.ExpiresUtc)
                {
                    RefreshPingSettings(ping);
                    continue;
                }

                DisposePing(ping, "expired");
                ActivePings.RemoveAt(i);
            }

            for (int i = ActiveCues.Count - 1; i >= 0; i--)
            {
                CueState cue = ActiveCues[i];
                bool expired = now - cue.CreatedUtc > TimeSpan.FromSeconds(6.5);
                bool finished = now - cue.CreatedUtc > TimeSpan.FromSeconds(0.5) && (cue.Voice == null || !cue.Voice.IsPlaying);
                if (!expired && !finished)
                {
                    RefreshCueSettings(cue);
                    continue;
                }

                DisposeCue(cue, expired ? "expired" : "finished");
                ActiveCues.RemoveAt(i);
            }

            PurgeAutoWetSources(now);
        }

        public static void Reset()
        {
            DisposeActiveWetVoices("reset");
            DisposeSharedCueBuses("reset");
            AutoWetSourceVoices.Clear();
            AutoWetCueCooldowns.Clear();
            _lastAutoWetLogUtc = DateTime.MinValue;
            _lastStatus = "reverbPing=reset";
            _lastAppliedParameterStatus = "xParams=reset";
        }

        private static float[] CreateBurstSamples(int sampleRate, int channels, float durationSeconds, float burstSeconds)
        {
            channels = Math.Max(1, Math.Min(2, channels));
            int frameCount = Math.Max(1, (int)(sampleRate * durationSeconds));
            int burstSamples = Math.Max(1, (int)(sampleRate * burstSeconds));
            float[] samples = new float[frameCount * channels];
            uint seed = 0xA5143F2Du;

            for (int i = 0; i < burstSamples && i < frameCount; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                float noise = ((seed >> 8) / 16777215f) * 2f - 1f;
                float t = i / (float)burstSamples;
                float envelope = (float)Math.Pow(1f - t, 1.8f);
                float sample = noise * envelope * 0.85f;
                for (int ch = 0; ch < channels; ch++)
                    samples[i * channels + ch] = sample;
            }

            if (samples.Length > 0)
            {
                for (int ch = 0; ch < channels; ch++)
                    samples[ch] = 1f;
            }

            int secondClickFrame = sampleRate / 20;
            if (secondClickFrame < frameCount)
            {
                for (int ch = 0; ch < channels; ch++)
                    samples[secondClickFrame * channels + ch] += 0.65f;
            }

            return samples;
        }

        private static ReverbParameters CreateParameters(RealisticSoundPlusSettings settings)
        {
            float diffusion = Clamp01(settings?.GlobalReverbDiffusion ?? 0.5f);
            float roomSize = Clamp01(settings?.GlobalReverbRoomSize ?? 0.8f);
            byte diffusionByte = ToByte(diffusion * 15f, 0, 15);
            float tailGain = ClampReverbGain(settings?.GlobalReverbTailGainDb ?? (2f + roomSize * 8f));
            float earlyGain = ClampEarlyReverbGain(settings?.GlobalReverbEarlyGainDb ?? (-1f + roomSize * 6f), tailGain);
            byte sourcePosition = ToByte(18f + roomSize * 12f, 0, 30);
            return new ReverbParameters
            {
                WetDryMix = 100f,
                ReflectionsDelay = (int)Math.Round(Clamp(settings?.GlobalReverbPredelayMs ?? (8f + roomSize * 70f), 0f, 300f)),
                ReverbDelay = ToByte(Clamp(settings?.GlobalReverbLateDelayMs ?? (8f + roomSize * 70f), 0f, 85f), 0, 85),
                RearDelay = 5,
                SideDelay = 5,
                PositionLeft = sourcePosition,
                PositionRight = sourcePosition,
                PositionMatrixLeft = 30,
                PositionMatrixRight = 30,
                EarlyDiffusion = diffusionByte,
                LateDiffusion = diffusionByte,
                LowEQGain = 8,
                LowEQCutoff = 4,
                HighEQGain = 0,
                HighEQCutoff = 14,
                RoomFilterFreq = Clamp(settings?.GlobalReverbToneHz ?? 5000f, 20f, 20000f),
                RoomFilterMain = 0f,
                RoomFilterHF = Clamp(settings?.GlobalReverbHighFrequencyDb ?? 0f, -60f, 0f),
                ReflectionsGain = earlyGain,
                ReverbGain = tailGain,
                DecayTime = Clamp(settings?.GlobalReverbDecaySeconds ?? (0.7f + roomSize * 6.3f), 0.1f, 30f),
                Density = Clamp(settings?.GlobalReverbDensity ?? Math.Max(15f, diffusion * 100f), 0f, 100f),
                RoomSize = ToNativeRoomSize(roomSize),
                DisableLateField = new RawBool(false)
            };
        }

        private static ReverbParameters CreatePresetParameters(string presetName, out string canonicalName)
        {
            string key = string.IsNullOrWhiteSpace(presetName) ? "hangar" : presetName.Trim().ToLowerInvariant();
            ReverbI3DL2Parameters preset;
            switch (key)
            {
                case "arena":
                    canonicalName = "Arena";
                    preset = ReverbI3DL2Parameters.Presets.Arena;
                    break;
                case "auditorium":
                    canonicalName = "Auditorium";
                    preset = ReverbI3DL2Parameters.Presets.Auditorium;
                    break;
                case "cave":
                    canonicalName = "Cave";
                    preset = ReverbI3DL2Parameters.Presets.Cave;
                    break;
                case "concert":
                case "concerthall":
                case "church":
                    canonicalName = "ConcertHall";
                    preset = ReverbI3DL2Parameters.Presets.ConcertHall;
                    break;
                case "hall":
                case "largehall":
                    canonicalName = "LargeHall";
                    preset = ReverbI3DL2Parameters.Presets.LargeHall;
                    break;
                case "pipe":
                case "sewer":
                case "sewerpipe":
                    canonicalName = "SewerPipe";
                    preset = ReverbI3DL2Parameters.Presets.SewerPipe;
                    break;
                case "stone":
                case "stoneroom":
                    canonicalName = "StoneRoom";
                    preset = ReverbI3DL2Parameters.Presets.StoneRoom;
                    break;
                case "hangar":
                default:
                    canonicalName = "Hangar";
                    preset = ReverbI3DL2Parameters.Presets.Hangar;
                    break;
            }

            ReverbParameters parameters = (ReverbParameters)preset;
            parameters.WetDryMix = 100f;
            parameters.DisableLateField = new RawBool(false);
            return parameters;
        }

        private static SharpDX.XAPO.Fx.ReverbParameters CreateSimpleXapoParameters(RealisticSoundPlusSettings settings)
        {
            return new SharpDX.XAPO.Fx.ReverbParameters
            {
                Diffusion = Clamp01(settings?.GlobalReverbDiffusion ?? 1f),
                RoomSize = Math.Max(0.0001f, Clamp01(settings?.GlobalReverbRoomSize ?? 1f))
            };
        }

        private static string DescribeSimpleXapoParameters(SharpDX.XAPO.Fx.ReverbParameters parameters)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "simple diffusion={0:0.00} room={1:0.00}",
                parameters.Diffusion,
                parameters.RoomSize);
        }

        private static string DescribeParameters(ReverbParameters parameters)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "x wet={0:0}% room={1:0.0}ft decay={2:0.0}s dens={3:0}% diff={4}/{5} delay={6}/{7}ms gain={8:0.0}/{9:0.0}dB filt={10:0}Hz/{11:0.0}dB pos={12}/{13} hiEq={14}/{15}",
                parameters.WetDryMix,
                parameters.RoomSize,
                parameters.DecayTime,
                parameters.Density,
                parameters.EarlyDiffusion,
                parameters.LateDiffusion,
                parameters.ReflectionsDelay,
                parameters.ReverbDelay,
                parameters.ReflectionsGain,
                parameters.ReverbGain,
                parameters.RoomFilterFreq,
                parameters.RoomFilterHF,
                parameters.PositionLeft,
                parameters.PositionMatrixLeft,
                parameters.HighEQGain,
                parameters.HighEQCutoff);
        }

        private static string DescribeExpectedMode(RealisticSoundPlusSettings settings)
        {
            if (settings == null)
                return "defaults";
            if (!settings.GlobalReverbEnabled)
                return "off";
            if (settings.GlobalReverbWetSend <= 0.001f)
                return "send-muted";

            bool earlyMuted = settings.GlobalReverbEarlyGainDb <= -59f;
            bool tailMuted = settings.GlobalReverbTailGainDb <= -59f;
            if (earlyMuted && tailMuted)
                return "early+tail-muted";
            if (earlyMuted)
                return "early-seeded-tail";
            if (tailMuted)
                return "tail-muted";
            return "early+tail";
        }

        private static float ToNativeRoomSize(float roomSize)
        {
            return 1f + Clamp01(roomSize) * 99f;
        }

        private static float ClampReverbGain(float value)
        {
            float clamped = Clamp(value, -60f, 20f);
            return clamped <= -60f ? -59f : clamped;
        }

        private static float ClampEarlyReverbGain(float value, float tailGain)
        {
            float clamped = ClampReverbGain(value);
            return tailGain > -59f && clamped < MinLateFieldSeedDb ? MinLateFieldSeedDb : clamped;
        }

        private static string BuildParameterSignature(RealisticSoundPlusSettings settings)
        {
            if (settings == null)
                return "default";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.000}|{1:0.000}|{2:0.000}|{3:0.000}|{4:0.000}|{5:0.000}|{6:0.000}|{7:0.000}|{8:0.000}|{9:0.0}",
                settings.GlobalReverbDiffusion,
                settings.GlobalReverbRoomSize,
                settings.GlobalReverbDecaySeconds,
                settings.GlobalReverbEarlyGainDb,
                settings.GlobalReverbTailGainDb,
                settings.GlobalReverbPredelayMs,
                settings.GlobalReverbLateDelayMs,
                settings.GlobalReverbDensity,
                settings.GlobalReverbToneHz,
                settings.GlobalReverbHighFrequencyDb);
        }

        private static void RefreshPingSettings(PingState ping)
        {
            if (ping == null)
                return;

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (!ping.FixedParameters)
                RefreshBusParameters(ping.Bus, ref ping.Signature, settings);
            try
            {
                ping.Source?.SetVolume(CalculateWetVolume(ping.SourceBaseVolume, settings), 0);
            }
            catch
            {
            }
        }

        private static void RefreshCueSettings(CueState cue)
        {
            if (cue == null)
                return;

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            try
            {
                if (cue.Voice != null && cue.Voice.IsValid)
                    cue.Voice.SetVolume(CalculateWetVolume(cue.WetVolumeBase, settings));
            }
            catch
            {
            }
        }

        private static bool TryEnsureSharedCueBus(XAudio2 engine, int channels, RealisticSoundPlusSettings settings, out SharedWetBusState state, out string status)
        {
            state = null;
            status = "shared-bus=missing";
            if (engine == null)
                return false;

            channels = Math.Max(1, Math.Min(2, channels));
            DateTime now = DateTime.UtcNow;
            string key = BuildSharedBusKey(channels, settings);
            if (SharedCueBuses.TryGetValue(key, out state) && state != null && state.Bus != null)
            {
                state.LastUsedUtc = now;
                EnsureSharedBusTailPump(engine, state, settings, now, "reuse");
                status = "shared-bus=existing";
                return true;
            }

            SubmixVoice bus = null;
            Reverb reverb = null;
            try
            {
                reverb = CreateReverbEffect(engine, out string reverbCtor);
                EffectDescriptor descriptor = new EffectDescriptor(reverb, channels);
                bus = new SubmixVoice(engine, channels, WetBusSampleRate);
                bus.SetEffectChain(new[] { descriptor });
                ReverbParameters parameters = CreateParameters(settings);
                bus.SetEffectParameters(0, parameters);
                bus.EnableEffect(0);
                bus.IsEffectEnabled(0, out RawBool enabledRaw);
                _lastAppliedParameterStatus = "shared " + DescribeParameters(parameters);

                state = new SharedWetBusState
                {
                    Bus = bus,
                    Reverb = reverb,
                    Key = key,
                    Channels = channels,
                    Constructor = reverbCtor,
                    EffectEnabled = enabledRaw,
                    Signature = BuildParameterSignature(settings),
                    LastUsedUtc = now
                };
                SharedCueBuses[key] = state;
                EnsureSharedBusTailPump(engine, state, settings, now, "created");
                status = string.Format(
                    CultureInfo.InvariantCulture,
                    "shared-bus=created {0}Hz/{1}ch effect={2} ctor={3}",
                    WetBusSampleRate,
                    channels,
                    state.EffectEnabled ? "enabled" : "disabled",
                    reverbCtor);
                V2DebugLog.WriteEvent("reverb-shared-bus", status);
                V2DebugLog.WriteEvent("reverb-bus-params", "created " + _lastAppliedParameterStatus + " expected=" + DescribeExpectedMode(settings));
                return true;
            }
            catch (Exception ex)
            {
                Dispose(bus);
                Dispose(reverb);
                status = "shared-bus=failed:" + DescribeException(ex);
                V2DebugLog.WriteEvent("reverb-shared-bus", status);
                state = null;
                return false;
            }
        }

        private static void RefreshSharedCueBuses(RealisticSoundPlusSettings settings, DateTime now)
        {
            if (SharedCueBuses.Count == 0)
                return;

            List<string> remove = null;
            TimeSpan idleLifetime = CalculateSharedBusIdleLifetime(settings);
            foreach (KeyValuePair<string, SharedWetBusState> pair in SharedCueBuses)
            {
                SharedWetBusState state = pair.Value;
                if (state == null || state.Bus == null || now - state.LastUsedUtc > idleLifetime)
                {
                    if (remove == null)
                        remove = new List<string>();
                    remove.Add(pair.Key);
                    continue;
                }
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
            {
                if (!SharedCueBuses.TryGetValue(remove[i], out SharedWetBusState state))
                    continue;
                if (HasActiveCueForBus(remove[i]))
                    continue;
                DisposeSharedBusTailPump(state);
                Dispose(state.Bus);
                Dispose(state.Reverb);
                SharedCueBuses.Remove(remove[i]);
                V2DebugLog.WriteEvent("reverb-shared-bus", "disposed idle " + remove[i]);
            }
        }

        private static bool HasActiveCueForBus(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            for (int i = 0; i < ActiveCues.Count; i++)
            {
                CueState cue = ActiveCues[i];
                if (cue != null && string.Equals(cue.BusKey, key, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void DisposeSharedCueBuses(string reason)
        {
            if (SharedCueBuses.Count == 0)
                return;

            foreach (SharedWetBusState state in SharedCueBuses.Values)
            {
                if (state == null)
                    continue;
                DisposeSharedBusTailPump(state);
                Dispose(state.Bus);
                Dispose(state.Reverb);
            }

            SharedCueBuses.Clear();
            _lastAppliedParameterStatus = "xParams=disposed:" + (reason ?? "?");
            V2DebugLog.WriteEvent("reverb-shared-bus", "disposed " + (reason ?? "?"));
        }

        private static TimeSpan CalculateSharedBusIdleLifetime(RealisticSoundPlusSettings settings)
        {
            float decay = Clamp(settings?.GlobalReverbDecaySeconds ?? 6f, 0.1f, 30f);
            float predelay = Clamp(settings?.GlobalReverbPredelayMs ?? 0f, 0f, 300f) / 1000f;
            float lateDelay = Clamp(settings?.GlobalReverbLateDelayMs ?? 0f, 0f, 85f) / 1000f;
            float seconds = Clamp(decay + predelay + lateDelay + 1f, 2f, 31.5f);
            return TimeSpan.FromSeconds(seconds);
        }

        private static TimeSpan CalculateParameterTailLifetime(ReverbParameters parameters)
        {
            float decay = Clamp(parameters.DecayTime, 0.1f, 30f);
            float predelay = Clamp(parameters.ReflectionsDelay, 0f, 300f) / 1000f;
            float lateDelay = Clamp(parameters.ReverbDelay, 0f, 85f) / 1000f;
            float seconds = Clamp(decay + predelay + lateDelay + 1f, 2f, 31.5f);
            return TimeSpan.FromSeconds(seconds);
        }

        private static void EnsureSharedBusTailPump(XAudio2 engine, SharedWetBusState state, RealisticSoundPlusSettings settings, DateTime now, string reason)
        {
            if (engine == null || state == null || state.Bus == null)
                return;

            TimeSpan lifetime = CalculateSharedBusIdleLifetime(settings);
            if (state.TailPumpSource != null && state.TailPumpExpiresUtc - now > TimeSpan.FromSeconds(5))
                return;

            DisposeSharedBusTailPump(state);

            SourceVoice source = null;
            DataStream stream = null;
            try
            {
                int channels = Math.Max(1, Math.Min(2, state.Channels));
                int sampleCount = Math.Max(WetBusSampleRate, (int)Math.Ceiling(WetBusSampleRate * lifetime.TotalSeconds)) * channels;
                float[] silence = new float[sampleCount];
                if (silence.Length > 0)
                    silence[0] = 0.00000001f;

                WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(WetBusSampleRate, channels);
                stream = new DataStream(silence.Length * sizeof(float), true, true);
                stream.WriteRange(silence);
                stream.Position = 0;

                AudioBuffer buffer = new AudioBuffer(stream)
                {
                    Flags = BufferFlags.EndOfStream
                };

                source = new SourceVoice(engine, format);
                source.SetOutputVoices(new[] { new VoiceSendDescriptor(state.Bus) });
                TrySetOutputMatrix(source, state.Bus, channels, channels, out _);
                source.SetVolume(1f, 0);
                source.SubmitSourceBuffer(buffer, null);
                source.Start();

                state.TailPumpSource = source;
                state.TailPumpStream = stream;
                state.TailPumpExpiresUtc = now + lifetime;
                V2DebugLog.WriteEvent("reverb-tail-pump", string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1}Hz/{2}ch hold={3:0.0}s key={4}",
                    reason ?? "?",
                    WetBusSampleRate,
                    channels,
                    lifetime.TotalSeconds,
                    state.Key ?? "?"));
            }
            catch (Exception ex)
            {
                Dispose(source);
                Dispose(stream);
                V2DebugLog.WriteEvent("reverb-tail-pump", "failed " + (reason ?? "?") + " " + DescribeException(ex));
            }
        }

        private static void DisposeSharedBusTailPump(SharedWetBusState state)
        {
            if (state == null)
                return;

            try
            {
                state.TailPumpSource?.Stop();
                state.TailPumpSource?.FlushSourceBuffers();
                state.TailPumpSource?.DestroyVoice();
            }
            catch
            {
            }

            Dispose(state.TailPumpSource);
            Dispose(state.TailPumpStream);
            state.TailPumpSource = null;
            state.TailPumpStream = null;
            state.TailPumpExpiresUtc = DateTime.MinValue;
        }

        private static void RefreshBusParameters(SubmixVoice bus, ref string signature, RealisticSoundPlusSettings settings)
        {
            if (bus == null)
                return;

            string next = BuildParameterSignature(settings);
            if (string.Equals(signature, next, StringComparison.Ordinal))
                return;

            try
            {
                ReverbParameters parameters = CreateParameters(settings);
                bus.SetEffectParameters(0, parameters);
                bus.EnableEffect(0);
                signature = next;
                _lastAppliedParameterStatus = "shared " + DescribeParameters(parameters);
                V2DebugLog.WriteEvent("reverb-bus-params", "updated " + next + " " + _lastAppliedParameterStatus + " expected=" + DescribeExpectedMode(settings));
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("reverb-bus-params", "failed " + DescribeException(ex));
            }
        }

        private static string BuildSharedBusKey(int channels, RealisticSoundPlusSettings settings)
        {
            return channels.ToString(CultureInfo.InvariantCulture) + "ch|" + BuildParameterSignature(settings);
        }

        private static float CalculateWetVolume(float baseVolume, RealisticSoundPlusSettings settings)
        {
            float send = Clamp(settings?.GlobalReverbWetSend ?? 1f, 0f, 4f);
            return Clamp(baseVolume * send, 0f, 4f);
        }

        private static bool TrySetOutputMatrix(SourceVoice source, Voice destination, int sourceChannels, int destinationChannels, out string status)
        {
            status = "matrix=skip";
            if (source == null || destination == null)
                return false;

            sourceChannels = Math.Max(1, Math.Min(2, sourceChannels));
            destinationChannels = Math.Max(1, Math.Min(2, destinationChannels));
            try
            {
                source.SetOutputMatrix(destination, sourceChannels, destinationChannels, BuildOutputMatrix(sourceChannels, destinationChannels));
                status = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", sourceChannels, destinationChannels);
                return true;
            }
            catch (Exception ex)
            {
                status = "matrix-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("reverb-matrix", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static float[] BuildOutputMatrix(int sourceChannels, int destinationChannels)
        {
            if (sourceChannels <= 1 && destinationChannels >= 2)
                return new[] { 1f, 1f };
            if (sourceChannels >= 2 && destinationChannels >= 2)
                return new[] { 1f, 0f, 0f, 1f };
            if (sourceChannels >= 2)
                return new[] { 0.5f, 0.5f };
            return new[] { 1f };
        }

        private static string SetStatus(string status)
        {
            _lastStatus = status;
            return status;
        }

        private static string SetCueStatus(string status, bool updateLastStatus)
        {
            if (updateLastStatus)
                _lastStatus = status;
            V2DebugLog.WriteEvent("reverb-cue", status);
            return status;
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null)
                return "unknown";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} hr=0x{1:X8} msg={2}",
                ex.GetType().Name,
                ex.HResult,
                ex.Message);
        }

        private static void DisposeActiveWetVoices(string reason)
        {
            int pings = ActivePings.Count;
            int cues = ActiveCues.Count;
            for (int i = ActivePings.Count - 1; i >= 0; i--)
                DisposePing(ActivePings[i], reason);
            for (int i = ActiveCues.Count - 1; i >= 0; i--)
                DisposeCue(ActiveCues[i], reason);
            ActivePings.Clear();
            ActiveCues.Clear();

            if (pings > 0 || cues > 0)
                V2DebugLog.WriteEvent("reverb-wet-clear", string.Format(
                    CultureInfo.InvariantCulture,
                    "reason={0} pings={1} cues={2} buses={3}",
                    reason ?? "?",
                    pings,
                    cues,
                    SharedCueBuses.Count));
        }

        private static void DisposePing(PingState ping, string reason)
        {
            if (ping == null)
                return;

            try
            {
                ping.Source?.Stop();
                ping.Source?.FlushSourceBuffers();
                ping.Source?.DestroyVoice();
            }
            catch
            {
            }

            Dispose(ping.Source);
            Dispose(ping.Bus);
            Dispose(ping.Stream);
            Dispose(ping.Reverb);
            V2DebugLog.WriteEvent("reverb-ping-dispose", "reason=" + (reason ?? "?"));
        }

        private static void DisposeCue(CueState cue, string reason)
        {
            if (cue == null)
                return;

            bool valid = false;
            bool playing = false;
            try
            {
                valid = cue.Voice != null && cue.Voice.IsValid;
                playing = valid && cue.Voice.IsPlaying;
            }
            catch
            {
            }

            TryRestoreGameVoiceOutputVoices(cue.Voice, cue.OriginalOutputVoices, out string restoreStatus);
            StopGameVoice(cue.Voice, reason);
            V2DebugLog.WriteEvent("reverb-cue-dispose", string.Format(
                CultureInfo.InvariantCulture,
                "reason={0} cue={1} source={2} valid={3} playing={4} restore={5} origOut={6} bus={7}",
                reason ?? "?",
                cue.CueName ?? "?",
                cue.Reason ?? "?",
                valid ? "Y" : "N",
                playing ? "Y" : "N",
                restoreStatus ?? "?",
                cue.OriginalOutputVoices?.Length ?? 0,
                cue.BusKey ?? "?"));
        }

        private static VoiceSendDescriptor[] ResolveCurrentOutputVoices(IMySourceVoice voice)
        {
            if (voice == null)
                return null;

            try
            {
                PropertyInfo property = voice.GetType().GetProperty("CurrentOutputVoices", InstanceMembers);
                return property?.GetValue(voice, null) as VoiceSendDescriptor[];
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetGameVoiceOutputVoices(IMySourceVoice voice, VoiceSendDescriptor[] descriptors, out string status)
        {
            status = "voice-missing";
            if (voice == null)
                return false;

            try
            {
                MethodInfo method = voice.GetType().GetMethod("SetOutputVoices", InstanceMembers, null, new[] { typeof(VoiceSendDescriptor[]) }, null);
                if (method == null)
                {
                    status = "set-output-missing:" + voice.GetType().Name;
                    return false;
                }

                method.Invoke(voice, new object[] { descriptors });
                status = "routed";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = inner.GetType().Name + ":" + inner.Message;
                return false;
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        private static bool TryRestoreGameVoiceOutputVoices(IMySourceVoice voice, VoiceSendDescriptor[] descriptors, out string status)
        {
            status = "none";
            if (voice == null || descriptors == null || descriptors.Length == 0)
                return false;

            try
            {
                bool restored = TrySetGameVoiceOutputVoices(voice, descriptors, out status);
                return restored;
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        private static void StopGameVoice(IMySourceVoice voice, string reason)
        {
            try
            {
                if (voice != null && voice.IsValid)
                    voice.Stop(true);
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("reverb-cue-stop-failed", (reason ?? "?") + " " + DescribeException(ex));
            }
        }

        private static void PurgeAutoWetSources(DateTime now)
        {
            if (AutoWetSourceVoices.Count > 0)
            {
                List<IMySourceVoice> remove = null;
                foreach (KeyValuePair<IMySourceVoice, DateTime> pair in AutoWetSourceVoices)
                {
                    IMySourceVoice voice = pair.Key;
                    if (voice == null || !voice.IsValid || !voice.IsPlaying || now - pair.Value > AutoWetSourceLifetime)
                    {
                        if (remove == null)
                            remove = new List<IMySourceVoice>();
                        remove.Add(voice);
                    }
                }

                if (remove != null)
                {
                    for (int i = 0; i < remove.Count; i++)
                        AutoWetSourceVoices.Remove(remove[i]);
                }
            }

            if (AutoWetCueCooldowns.Count == 0)
                return;

            List<string> expired = null;
            foreach (KeyValuePair<string, DateTime> pair in AutoWetCueCooldowns)
            {
                if (now - pair.Value > TimeSpan.FromSeconds(2))
                {
                    if (expired == null)
                        expired = new List<string>();
                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
                return;

            for (int i = 0; i < expired.Count; i++)
                AutoWetCueCooldowns.Remove(expired[i]);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return min;
            if (value <= min)
                return min;
            return value >= max ? max : value;
        }

        private static int ResolveVoiceInputSampleRate(SourceVoice source)
        {
            try
            {
                int sampleRate = source.VoiceDetails.InputSampleRate;
                return sampleRate >= 6000 ? sampleRate : 48000;
            }
            catch
            {
                return 48000;
            }
        }

        private static int ResolveSourceInputChannelCount(SourceVoice source)
        {
            try
            {
                int channels = source.VoiceDetails.InputChannelCount;
                return Math.Max(1, channels);
            }
            catch
            {
                return 1;
            }
        }

        private static void Dispose(IDisposable disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
            }
        }

        private static byte ToByte(float value, int min, int max)
        {
            if (value <= min)
                return (byte)min;
            if (value >= max)
                return (byte)max;
            return (byte)Math.Round(value);
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0f;
            if (value <= 0f)
                return 0f;
            return value >= 1f ? 1f : value;
        }

        private sealed class PingState
        {
            public SourceVoice Source;
            public SubmixVoice Bus;
            public DataStream Stream;
            public SharpDX.XAPO.AudioProcessor Reverb;
            public string Signature;
            public float SourceBaseVolume;
            public DateTime ExpiresUtc;
            public DateTime CreatedUtc;
            public bool FixedParameters;
        }

        private sealed class CueState
        {
            public IMySourceVoice Voice;
            public VoiceSendDescriptor[] OriginalOutputVoices;
            public string CueName;
            public string Reason;
            public string BusKey;
            public string Signature;
            public float WetVolumeBase;
            public DateTime CreatedUtc;
        }

        private sealed class SharedWetBusState
        {
            public SubmixVoice Bus;
            public Reverb Reverb;
            public string Key;
            public int Channels;
            public string Constructor;
            public bool EffectEnabled;
            public string Signature;
            public DateTime LastUsedUtc;
            public SourceVoice TailPumpSource;
            public DataStream TailPumpStream;
            public DateTime TailPumpExpiresUtc;
        }
    }
}
