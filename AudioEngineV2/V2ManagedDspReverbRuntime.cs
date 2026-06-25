using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using VRage;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2ManagedDspReverbRuntime
    {
        private static readonly BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int OutputSampleRate = 48000;
        private const int OutputChannels = 2;
        private const int MaxWetTailCacheEntries = 8;
        private const int MaxToolLoopWetCacheEntries = 12;
        private const int AutoEnvironmentInputFrames = OutputSampleRate;
        private const float AutoWetTailMaxSeconds = 5f;
        private const float AutoWetFeedDelayMaxSeconds = 0.065f;
        private static readonly TimeSpan DefaultAutoWetCueCooldown = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan AutoWetSourceLifetime = TimeSpan.FromSeconds(8);
        private static readonly List<ActiveTailState> ActiveTails = new List<ActiveTailState>();
        private static readonly Dictionary<IMySourceVoice, ActiveToolLoopState> ActiveToolLoops = new Dictionary<IMySourceVoice, ActiveToolLoopState>();
        private static readonly Dictionary<string, DecodedCuePcm> DecodedCueCache = new Dictionary<string, DecodedCuePcm>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DecodedCuePcm> ToolLoopDecodedCache = new Dictionary<string, DecodedCuePcm>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float[]> WetTailCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> WetTailCacheOrder = new Queue<string>();
        private static readonly Dictionary<string, float[]> ToolLoopWetCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> ToolLoopWetCacheOrder = new Queue<string>();
        private static readonly Dictionary<IMySourceVoice, DateTime> AutoWetSourceVoices = new Dictionary<IMySourceVoice, DateTime>();
        private static readonly Dictionary<string, DateTime> AutoWetCueCooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> LogThrottle = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastAutoWetLogUtc = DateTime.MinValue;
        private static string _lastStatus = "dspReverb=not-run";
        private static string _lastDecodeStatus = "decode=not-run";
        private static string _lastTailStatus = "tail=not-run";
        private static bool _loggedEnabled;

        public static string LastStatus => _lastStatus;

        public static string FormatStatus()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            DspParameters parameters = ResolveDspParameters(settings);
            return string.Format(
                CultureInfo.InvariantCulture,
                "managedDsp enabled={0} active={1} decoded={2} tails={3} sends={4} wet={5:0.00} auto={6} room={7:0.00} radius={8:0.0}m decay={9:0.0}s pre={10:0}ms pressure={11:0.00} | {12} | {13}",
                settings != null && settings.GlobalReverbEnabled ? "Y" : "N",
                ActiveTails.Count + ActiveToolLoops.Count,
                DecodedCueCache.Count + ToolLoopDecodedCache.Count,
                WetTailCache.Count + ToolLoopWetCache.Count,
                AutoWetSourceVoices.Count,
                settings?.GlobalReverbWetSend ?? 1f,
                parameters.Source,
                parameters.RoomSize,
                parameters.EquivalentRadius,
                parameters.DecaySeconds,
                parameters.PredelayMs,
                parameters.AirPressure,
                _lastDecodeStatus,
                _lastTailStatus);
        }

        internal static V2LiveReverbParameters ResolveLiveParameters(RealisticSoundPlusSettings settings)
        {
            DspParameters parameters = ResolveDspParameters(settings);
            V2LiveReverbParameters live = new V2LiveReverbParameters
            {
                Source = parameters.Source,
                EquivalentRadius = parameters.EquivalentRadius,
                RoomSize = parameters.RoomSize,
                Diffusion = parameters.Diffusion,
                Density = parameters.Density,
                DecaySeconds = parameters.DecaySeconds,
                EarlyGainDb = parameters.EarlyGainDb,
                TailGainDb = parameters.TailGainDb,
                PredelayMs = parameters.PredelayMs,
                LateDelayMs = parameters.LateDelayMs,
                ToneHz = parameters.ToneHz,
                HighFrequencyDb = parameters.HighFrequencyDb,
                AirPressure = parameters.AirPressure,
                WetSend = CalculateWetSend(settings),
                ApertureFraction = 0.5f,
                StructuralOcclusion = 0.5f,
                FinalMuffling = 0.5f,
                ClosedFraction = 0.5f
            };

            if (V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample))
            {
                live.ApertureFraction = Clamp01(sample.ApertureFraction);
                live.StructuralOcclusion = Clamp01(sample.StructuralOcclusion);
                live.FinalMuffling = Clamp01(sample.FinalMuffling);
                live.ClosedFraction = Clamp01(sample.ReverbRoomClosedFraction);
            }

            return live;
        }

        public static string FormatAutoValue(string key)
        {
            if (!TryResolveAutoParameters(out DspParameters parameters))
                return "auto --";

            parameters = ApplyAutoModifiers(parameters, SettingsManager.Current);
            parameters = ApplyPressureLossTone(parameters, SettingsManager.Current);

            switch ((key ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "diffusion":
                    return "auto " + Format(parameters.Diffusion, 2);
                case "room":
                case "roomsize":
                    return "auto " + Format(parameters.RoomSize, 2);
                case "decay":
                    return "auto " + Format(parameters.DecaySeconds, 1) + " s";
                case "early":
                    return "auto " + Format(parameters.EarlyGainDb, 1) + " dB";
                case "tail":
                    return "auto " + Format(parameters.TailGainDb, 1) + " dB";
                case "predelay":
                    return "auto " + Format(parameters.PredelayMs, 0) + " ms";
                case "latedelay":
                    return "auto " + Format(parameters.LateDelayMs, 0) + " ms";
                case "density":
                    return "auto " + Format(parameters.Density, 0) + "%";
                case "tone":
                    return "auto " + Format(parameters.ToneHz, 0) + " Hz";
                case "hf":
                    return "auto " + Format(parameters.HighFrequencyDb, 1) + " dB";
                default:
                    return "auto ?";
            }
        }

        public static void Update()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            DateTime now = DateTime.UtcNow;
            if (settings == null || !settings.GlobalReverbEnabled)
            {
                DisposeActiveTails("disabled");
                DisposeActiveToolLoops("disabled");
                AutoWetSourceVoices.Clear();
                AutoWetCueCooldowns.Clear();
                _loggedEnabled = false;
                return;
            }

            if (!_loggedEnabled)
            {
                _loggedEnabled = true;
                V2ToolLoopWaveCatalog.Warmup();
                V2DebugLog.WriteEvent("dsp-reverb", "managed route active; legacy XAudio effect route suppressed");
            }

            for (int i = ActiveTails.Count - 1; i >= 0; i--)
            {
                ActiveTailState tail = ActiveTails[i];
                if (now < tail.ExpiresUtc)
                    continue;

                DisposeTail(tail, "expired");
                ActiveTails.RemoveAt(i);
            }

            PurgeAutoWetSources(now);
            PurgeActiveToolLoops(now);
        }

        public static void Reset()
        {
            DisposeActiveTails("reset");
            DisposeActiveToolLoops("reset");
            DecodedCueCache.Clear();
            ToolLoopDecodedCache.Clear();
            WetTailCache.Clear();
            WetTailCacheOrder.Clear();
            ToolLoopWetCache.Clear();
            ToolLoopWetCacheOrder.Clear();
            AutoWetSourceVoices.Clear();
            AutoWetCueCooldowns.Clear();
            LogThrottle.Clear();
            _lastAutoWetLogUtc = DateTime.MinValue;
            _lastStatus = "dspReverb=reset";
            _lastDecodeStatus = "decode=reset";
            _lastTailStatus = "tail=reset";
            _loggedEnabled = false;
        }

        public static string PlayImpulse()
        {
            IMyAudio audio = MyAudio.Static;
            if (audio == null)
                return SetStatus("dspPing=audio-missing");

            XAudio2 engine = ResolveEngine(audio);
            if (engine == null)
                return SetStatus("dspPing=xaudio-missing");

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float[] impulse = CreateTestImpulse();
            DspTailInfo info;
            float[] wet = BuildWetTail(impulse, settings, 1f, 0f, out info);
            string routeStatus;
            if (!TryPlayWetTail(audio, engine, wet, 1f, "manual-ping", out routeStatus))
                return SetStatus("dspPing=play-failed:" + routeStatus);

            _lastTailStatus = info.ToStatus();
            string status = string.Format(
                CultureInfo.InvariantCulture,
                "dspPing=played tail={0:0.0}s decay={1:0.0}s lines={2} room={3:0.00} wet={4:0.00} route={5}",
                wet.Length / (float)(OutputSampleRate * OutputChannels),
                info.DecaySeconds,
                info.DelayLineCount,
                info.RoomSize,
                CalculateWetSend(settings),
                routeStatus);
            V2DebugLog.WriteEvent("dsp-reverb-ping", status + " | " + _lastTailStatus);
            return SetStatus(status);
        }

        public static string PlayCue(string cueName)
        {
            return PlayCueInternal(cueName, 1f, true, "manual", true);
        }

        public static string DiagnoseCue(string cueName)
        {
            cueName = NormalizeCueName(cueName);
            IMyAudio audio = MyAudio.Static;
            if (audio == null)
                return SetStatus("dspDiag=audio-missing");

            DecodedCuePcm decoded;
            string decodeStatus;
            bool ok = TryDecodeCue(audio, cueName, out decoded, out decodeStatus);
            string status = ok
                ? "dspDiag=decoded cue=" + cueName + " " + decodeStatus
                : "dspDiag=decode-failed cue=" + cueName + " " + decodeStatus;
            V2DebugLog.WriteEvent("dsp-reverb-diag", status);
            return SetStatus(status);
        }

        public static bool TryPlayAutomaticWetSend(IMySourceVoice sourceVoice, string cueName, string category, float wetVolume, out string status)
        {
            status = "dspAuto=skip";
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.GlobalReverbEnabled || settings.GlobalReverbWetSend <= 0.001f)
                return false;
            if (SettingsManager.IsGlobalReverbGlobalBusRoute(settings))
            {
                status = "dspAuto=suppressed-livebus";
                return false;
            }
            if (sourceVoice == null || !sourceVoice.IsValid || !sourceVoice.IsPlaying)
                return false;

            cueName = NormalizeCueName(string.IsNullOrWhiteSpace(cueName) ? sourceVoice.CueEnum.ToString() : cueName);
            if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty")
                return false;
            if (ShouldMuteAutomaticWetTail(category, cueName))
            {
                status = "dspAuto=tail-muted cue=" + cueName + " category=" + (category ?? "?");
                WriteThrottledEvent("dsp-reverb-auto-muted", status, "auto-muted:" + (category ?? "?") + ":" + cueName, TimeSpan.FromSeconds(3));
                return false;
            }

            DateTime now = DateTime.UtcNow;
            PurgeAutoWetSources(now);
            PurgeActiveToolLoops(now);
            float adjustedWetVolume = AdjustAutomaticWetVolume(category, cueName, wetVolume);
            if (adjustedWetVolume <= 0.001f)
                return false;

            if (TryEnsureToolLoopWetSend(sourceVoice, cueName, category, adjustedWetVolume, out status))
                return true;

            if (AutoWetSourceVoices.TryGetValue(sourceVoice, out DateTime lastSource) && now - lastSource < GetAutoWetSourceCooldown(category, cueName))
            {
                status = "dspAuto=source-seen";
                return false;
            }

            if (AutoWetCueCooldowns.TryGetValue(cueName, out DateTime lastCue) && now - lastCue < GetAutoWetCueCooldown(category, cueName))
            {
                status = "dspAuto=cue-cooldown";
                return false;
            }

            AutoWetSourceVoices[sourceVoice] = now;
            AutoWetCueCooldowns[cueName] = now;

            status = PlayCueInternal(cueName, Clamp(adjustedWetVolume, 0.03f, 1.5f), false, "auto-" + (category ?? "?"), false);
            bool played = status.StartsWith("dspCue=played", StringComparison.OrdinalIgnoreCase);
            if (played && now - _lastAutoWetLogUtc > TimeSpan.FromSeconds(1))
            {
                _lastAutoWetLogUtc = now;
                V2DebugLog.WriteEvent("dsp-reverb-auto", status);
            }

            return played;
        }

        private static TimeSpan GetAutoWetSourceCooldown(string category, string cueName)
        {
            if (string.Equals(category, "block", StringComparison.OrdinalIgnoreCase))
            {
                if (Contains(cueName, "Door"))
                    return TimeSpan.FromSeconds(3.0);
                if (IsRefineryCue(cueName))
                    return TimeSpan.FromSeconds(4.0);
                if (V2AuxCueClassifier.IsSustainedBlockReverbCue(cueName))
                    return TimeSpan.FromSeconds(5.0);
                return TimeSpan.FromSeconds(1.25);
            }

            if (string.Equals(category, "local", StringComparison.OrdinalIgnoreCase) && V2AuxCueClassifier.IsToolActionCue(cueName))
            {
                return V2AuxCueClassifier.IsSustainedLocalReverbCue(cueName)
                    ? TimeSpan.FromSeconds(2.5)
                    : TimeSpan.FromSeconds(0.45);
            }

            if (string.Equals(category, "local", StringComparison.OrdinalIgnoreCase) && V2AuxCueClassifier.IsImmersiveUiCue(cueName))
                return TimeSpan.FromMilliseconds(220);

            if (string.Equals(category, "local", StringComparison.OrdinalIgnoreCase)
                && (V2AuxCueClassifier.IsPlayerImpactCue(cueName) || V2AuxCueClassifier.IsWorldImpactCue(cueName)))
                return TimeSpan.FromMilliseconds(550);

            if (string.Equals(category, "local", StringComparison.OrdinalIgnoreCase) && V2AuxCueClassifier.IsWeaponCue(cueName))
                return V2AuxCueClassifier.IsSustainedWeaponCue(cueName)
                    ? TimeSpan.FromSeconds(1.25)
                    : TimeSpan.FromMilliseconds(550);

            if (string.Equals(category, "env", StringComparison.OrdinalIgnoreCase))
                return TimeSpan.FromSeconds(2.5);

            return AutoWetSourceLifetime;
        }

        private static TimeSpan GetAutoWetCueCooldown(string category, string cueName)
        {
            if (string.Equals(category, "block", StringComparison.OrdinalIgnoreCase))
            {
                if (Contains(cueName, "Door"))
                    return TimeSpan.FromSeconds(2.8);
                if (IsRefineryCue(cueName))
                    return TimeSpan.FromSeconds(4.0);
                if (V2AuxCueClassifier.IsSustainedBlockReverbCue(cueName))
                    return TimeSpan.FromSeconds(4.5);
                return TimeSpan.FromSeconds(1.4);
            }

            if (string.Equals(category, "local", StringComparison.OrdinalIgnoreCase))
            {
                if (V2AuxCueClassifier.IsSustainedLocalReverbCue(cueName))
                    return TimeSpan.FromSeconds(2.5);
                if (V2AuxCueClassifier.IsImmersiveUiCue(cueName))
                    return TimeSpan.FromMilliseconds(120);
                if (V2AuxCueClassifier.IsPlayerImpactCue(cueName) || V2AuxCueClassifier.IsWorldImpactCue(cueName))
                    return TimeSpan.FromMilliseconds(450);
                if (V2AuxCueClassifier.IsToolActionCue(cueName))
                    return TimeSpan.FromMilliseconds(350);
                if (V2AuxCueClassifier.IsWeaponCue(cueName))
                    return V2AuxCueClassifier.IsSustainedWeaponCue(cueName)
                        ? TimeSpan.FromMilliseconds(800)
                        : TimeSpan.FromMilliseconds(260);
                return TimeSpan.FromMilliseconds(120);
            }

            if (string.Equals(category, "env", StringComparison.OrdinalIgnoreCase))
                return TimeSpan.FromSeconds(2.5);

            return DefaultAutoWetCueCooldown;
        }

        private static bool TryEnsureToolLoopWetSend(IMySourceVoice sourceVoice, string cueName, string category, float wetVolume, out string status)
        {
            status = "dspToolLoop=skip";
            if (sourceVoice == null || !sourceVoice.IsValid || !sourceVoice.IsPlaying)
                return false;
            if (!V2AuxCueClassifier.IsToolActionCue(cueName))
                return false;
            if (!string.Equals(category, "local", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(category, "block", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!V2ToolLoopWaveCatalog.TryGetLoopWave(cueName, out V2ToolLoopWaveCatalog.ToolLoopWaveInfo loopInfo))
                return false;

            DateTime now = DateTime.UtcNow;
            float volume = Clamp(wetVolume * CalculateWetSend(SettingsManager.Current), 0f, 2.5f);
            if (volume <= 0.005f)
            {
                if (ActiveToolLoops.TryGetValue(sourceVoice, out ActiveToolLoopState quietLoop))
                {
                    DisposeToolLoop(quietLoop, "quiet");
                    ActiveToolLoops.Remove(sourceVoice);
                }

                status = "dspToolLoop=quiet cue=" + cueName;
                return true;
            }

            if (ActiveToolLoops.TryGetValue(sourceVoice, out ActiveToolLoopState active))
            {
                if (string.Equals(active.CueName, cueName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(active.Path, loopInfo.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    active.LastSeenUtc = now;
                    UpdateToolLoopVolume(active, volume);
                    status = string.Format(CultureInfo.InvariantCulture, "dspToolLoop=active cue={0} vol={1:0.00}", cueName, volume);
                    return true;
                }

                DisposeToolLoop(active, "cue-change");
                ActiveToolLoops.Remove(sourceVoice);
            }

            if (TryFindReusableToolLoop(cueName, loopInfo.AbsolutePath, now, out IMySourceVoice reusableKey, out ActiveToolLoopState reusable))
            {
                ActiveToolLoops.Remove(reusableKey);
                reusable.OriginalVoice = sourceVoice;
                reusable.LastSeenUtc = now;
                ActiveToolLoops[sourceVoice] = reusable;
                UpdateToolLoopVolume(reusable, volume);
                status = string.Format(CultureInfo.InvariantCulture, "dspToolLoop=adopted cue={0} vol={1:0.00}", cueName, volume);
                return true;
            }

            DecodedCuePcm decoded;
            string decodeStatus;
            if (!TryDecodeToolLoopWave(loopInfo, out decoded, out decodeStatus))
            {
                status = "dspToolLoop=decode-failed cue=" + cueName + " " + decodeStatus;
                WriteThrottledEvent("dsp-tool-loop-failed", status, "tool-decode:" + cueName, TimeSpan.FromSeconds(4));
                return false;
            }

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            string cacheKey = loopInfo.AbsolutePath + "|" + BuildDspSignature(settings);
            if (!ToolLoopWetCache.TryGetValue(cacheKey, out float[] wet))
            {
                DspTailInfo info;
                wet = BuildWetTail(decoded.Mono48k, settings, 0.9f, AutoWetTailMaxSeconds, true, false, out info);
                AddToolLoopWetCache(cacheKey, wet);
                _lastTailStatus = info.ToStatus();
            }

            IMyAudio audio = MyAudio.Static;
            XAudio2 engine = ResolveEngine(audio);
            if (audio == null || engine == null)
            {
                status = "dspToolLoop=audio-missing";
                return false;
            }

            if (!TryPlayToolLoop(audio, engine, wet, volume, cueName, loopInfo.AbsolutePath, sourceVoice, out status))
                return false;

            status += " " + decodeStatus;
            WriteThrottledEvent("dsp-tool-loop", status, "tool-loop:" + cueName, TimeSpan.FromSeconds(2));
            return true;
        }

        private static bool TryPlayToolLoop(IMyAudio audio, XAudio2 engine, float[] samples, float volume, string cueName, string path, IMySourceVoice originalVoice, out string status)
        {
            status = "dspToolLoop=route?";
            if (engine == null || samples == null || samples.Length == 0)
            {
                status = "dspToolLoop=empty";
                return false;
            }

            SourceVoice source = null;
            DataStream stream = null;
            try
            {
                WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);
                stream = new DataStream(samples.Length * sizeof(float), true, true);
                stream.WriteRange(samples);
                stream.Position = 0;
                AudioBuffer buffer = new AudioBuffer(stream)
                {
                    LoopBegin = 0,
                    LoopLength = samples.Length / OutputChannels,
                    LoopCount = AudioBuffer.LoopInfinite
                };

                source = new SourceVoice(engine, format);
                Voice output = ResolveGameOutputVoice(audio);
                string outputStatus = "default";
                if (output != null)
                {
                    source.SetOutputVoices(new[] { new VoiceSendDescriptor(output) });
                    outputStatus = output.GetType().Name;
                }

                source.SetVolume(volume, 0);
                source.SubmitSourceBuffer(buffer, null);
                source.Start();

                DateTime now = DateTime.UtcNow;
                ActiveToolLoops[originalVoice] = new ActiveToolLoopState
                {
                    OriginalVoice = originalVoice,
                    Source = source,
                    Stream = stream,
                    CueName = cueName,
                    Path = path,
                    CreatedUtc = now,
                    LastSeenUtc = now,
                    Frames = samples.Length / OutputChannels,
                    Volume = volume
                };

                status = string.Format(CultureInfo.InvariantCulture, "dspToolLoop=played cue={0} out={1} vol={2:0.00} frames={3}", cueName, outputStatus, volume, samples.Length / OutputChannels);
                return true;
            }
            catch (Exception ex)
            {
                Dispose(source);
                Dispose(stream);
                status = "dspToolLoop=failed:" + DescribeException(ex);
                V2DebugLog.WriteEvent("dsp-tool-loop-failed", status);
                return false;
            }
        }

        private static void UpdateToolLoopVolume(ActiveToolLoopState active, float volume)
        {
            if (active == null || active.Source == null)
                return;

            if (Math.Abs(active.Volume - volume) < 0.01f)
                return;

            try
            {
                active.Source.SetVolume(volume, 0);
                active.Volume = volume;
            }
            catch
            {
            }
        }

        private static bool TryFindReusableToolLoop(string cueName, string path, DateTime now, out IMySourceVoice key, out ActiveToolLoopState active)
        {
            key = null;
            active = null;
            foreach (KeyValuePair<IMySourceVoice, ActiveToolLoopState> pair in ActiveToolLoops)
            {
                ActiveToolLoopState loop = pair.Value;
                if (loop == null)
                    continue;

                if (now - loop.LastSeenUtc > TimeSpan.FromMilliseconds(1100))
                    continue;

                if (!string.Equals(loop.CueName, cueName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(loop.Path, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsPlaying(loop.Source))
                    continue;

                key = pair.Key;
                active = loop;
                return true;
            }

            return false;
        }

        private static bool ShouldMuteAutomaticWetTail(string category, string cueName)
        {
            return false;
        }

        private static float AdjustAutomaticWetVolume(string category, string cueName, float wetVolume)
        {
            float volume = wetVolume;

            if (V2AuxCueClassifier.IsToolActionCue(cueName))
                volume *= V2AuxCueClassifier.IsSustainedLocalReverbCue(cueName) ? 1.55f : 1.2f;

            if (V2AuxCueClassifier.IsWeaponCue(cueName))
                volume *= V2AuxCueClassifier.IsSustainedWeaponCue(cueName) ? 0.45f : 0.62f;

            if (Contains(cueName, "Door")
                || Contains(cueName, "Hatch")
                || Contains(cueName, "Hangar")
                || Contains(cueName, "Airtight"))
            {
                volume *= 0.58f;
            }

            if (string.Equals(category, "block", StringComparison.OrdinalIgnoreCase))
            {
                if (IsRefineryCue(cueName))
                    volume *= 1.45f;
                else if (V2AuxCueClassifier.IsSustainedBlockReverbCue(cueName))
                    volume *= 0.42f;
                else
                    volume *= 1.12f;
            }

            return Clamp(volume, 0f, 1.5f);
        }

        private static bool IsRefineryCue(string cueName)
        {
            return Contains(cueName, "Rafinery")
                || Contains(cueName, "Refinery");
        }

        private static string ResolveWetInputMode(string reason)
        {
            return reason != null && reason.StartsWith("auto-env", StringComparison.OrdinalIgnoreCase)
                ? "env-slice"
                : "full";
        }

        private static float[] SelectWetInput(float[] input, string mode)
        {
            if (!string.Equals(mode, "env-slice", StringComparison.OrdinalIgnoreCase) || input == null || input.Length <= AutoEnvironmentInputFrames)
                return input;

            float[] sliced = new float[AutoEnvironmentInputFrames];
            Array.Copy(input, sliced, sliced.Length);
            return sliced;
        }

        private static string PlayCueInternal(string cueName, float wetVolume, bool playDryCue, string reason, bool updateLastStatus)
        {
            cueName = NormalizeCueName(cueName);
            IMyAudio audio = MyAudio.Static;
            if (audio == null)
                return SetCueStatus("dspCue=audio-missing", updateLastStatus);

            XAudio2 engine = ResolveEngine(audio);
            if (engine == null)
                return SetCueStatus("dspCue=xaudio-missing", updateLastStatus);

            DecodedCuePcm decoded;
            string decodeStatus;
            if (!TryDecodeCue(audio, cueName, out decoded, out decodeStatus))
                return SetCueStatus("dspCue=decode-failed cue=" + cueName + " " + decodeStatus, updateLastStatus);

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            string inputMode = ResolveWetInputMode(reason);
            bool automatic = IsAutomaticReason(reason);
            string tailKey = cueName + "|" + inputMode + "|" + (automatic ? "auto-real-decorrelated" : "full") + "|" + BuildDspSignature(settings);
            float[] wet;
            if (!WetTailCache.TryGetValue(tailKey, out wet))
            {
                DspTailInfo info;
                float[] wetInput = SelectWetInput(decoded.Mono48k, inputMode);
                wet = BuildWetTail(wetInput, settings, 1f, automatic ? AutoWetTailMaxSeconds : 0f, automatic, out info);
                AddWetTailCache(tailKey, wet);
                _lastTailStatus = info.ToStatus();
            }

            string dryStatus = playDryCue ? PlayDryCue(audio, cueName) : "dry=skip";
            string routeStatus;
            if (!TryPlayWetTail(audio, engine, wet, wetVolume, reason, out routeStatus))
                return SetCueStatus("dspCue=play-failed cue=" + cueName + " " + routeStatus, updateLastStatus);

            string status = string.Format(
                CultureInfo.InvariantCulture,
                "dspCue=played cue={0} {1} wetFrames={2} tail={3:0.0}s wet={4:0.00} reason={5} {6} route={7}",
                cueName,
                decodeStatus,
                wet.Length / OutputChannels,
                wet.Length / (float)(OutputSampleRate * OutputChannels),
                wetVolume * CalculateWetSend(settings),
                reason ?? "?",
                dryStatus,
                routeStatus);
            return SetCueStatus(status, updateLastStatus);
        }

        private static bool TryDecodeCue(IMyAudio audio, string cueName, out DecodedCuePcm decoded, out string status)
        {
            decoded = null;
            cueName = NormalizeCueName(cueName);
            if (DecodedCueCache.TryGetValue(cueName, out decoded))
            {
                status = decoded.Status + " cached=Y";
                _lastDecodeStatus = status;
                return true;
            }

            MyCueId cueId = new MyCueId(MyStringHash.GetOrCompute(cueName));
            MySoundData cue;
            try
            {
                cue = audio.GetCue(cueId);
            }
            catch (Exception ex)
            {
                status = "cue-lookup-failed:" + DescribeException(ex);
                if (TryDecodeToolReverbFallback(cueName, status, out decoded, out status))
                    return true;

                _lastDecodeStatus = status;
                return false;
            }

            if (cue == null)
            {
                status = "cue-missing";
                if (TryDecodeToolReverbFallback(cueName, status, out decoded, out status))
                    return true;

                _lastDecodeStatus = status;
                return false;
            }

            object wave;
            string waveStatus;
            if (!TryResolveWave(audio, cue, out wave, out waveStatus) || wave == null)
            {
                status = "wave-missing " + waveStatus;
                if (TryDecodeToolReverbFallback(cueName, status, out decoded, out status))
                    return true;

                _lastDecodeStatus = status;
                return false;
            }

            WaveFormat format = GetMember<WaveFormat>(wave, "WaveFormat");
            if (format == null)
            {
                status = "format-missing wave=" + wave.GetType().FullName;
                if (TryDecodeToolReverbFallback(cueName, status, out decoded, out status))
                    return true;

                _lastDecodeStatus = status;
                return false;
            }

            byte[] raw;
            string rawStatus;
            if (!TryCopyWaveBytes(wave, out raw, out rawStatus))
            {
                status = "raw-missing " + rawStatus + " fmt=" + DescribeFormat(format);
                if (TryDecodeToolReverbFallback(cueName, status, out decoded, out status))
                    return true;

                _lastDecodeStatus = status;
                return false;
            }

            float[] mono;
            string pcmStatus;
            if (!TryConvertToMono(raw, format, out mono, out pcmStatus))
            {
                status = "pcm-unsupported " + pcmStatus + " raw=" + rawStatus + " fmt=" + DescribeFormat(format);
                if (TryDecodeToolReverbFallback(cueName, status, out decoded, out status))
                    return true;

                _lastDecodeStatus = status;
                return false;
            }

            float[] mono48 = ResampleTo48k(mono, format.SampleRate);
            decoded = new DecodedCuePcm
            {
                CueName = cueName,
                Mono48k = mono48,
                SourceSampleRate = format.SampleRate,
                SourceChannels = Math.Max(1, format.Channels),
                SourceFrames = mono.Length,
                Status = string.Format(
                    CultureInfo.InvariantCulture,
                    "pcm={0}Hz/{1}ch frames={2} mono48={3} fmt={4} wave={5}",
                    format.SampleRate,
                    Math.Max(1, format.Channels),
                    mono.Length,
                    mono48.Length,
                    DescribeFormat(format),
                    waveStatus)
            };

            DecodedCueCache[cueName] = decoded;
            status = decoded.Status + " cached=N";
            _lastDecodeStatus = status;
            return true;
        }

        private static bool TryDecodeToolReverbFallback(string cueName, string failureStatus, out DecodedCuePcm decoded, out string status)
        {
            decoded = null;
            status = failureStatus;
            if (!V2ToolLoopWaveCatalog.TryGetReverbWave(cueName, out V2ToolLoopWaveCatalog.ToolLoopWaveInfo waveInfo))
                return false;

            string fallbackStatus;
            if (!TryDecodeToolLoopWave(waveInfo, out decoded, out fallbackStatus))
            {
                status = failureStatus + " fallback-failed " + fallbackStatus;
                return false;
            }

            DecodedCueCache[cueName] = decoded;
            status = failureStatus + " fallback=tool-wav " + fallbackStatus;
            _lastDecodeStatus = status;
            WriteThrottledEvent("dsp-tool-wav-fallback", "cue=" + cueName + " " + status, "tool-fallback:" + cueName, TimeSpan.FromSeconds(5));
            return true;
        }

        private static bool TryDecodeToolLoopWave(V2ToolLoopWaveCatalog.ToolLoopWaveInfo loopInfo, out DecodedCuePcm decoded, out string status)
        {
            decoded = null;
            string path = loopInfo.AbsolutePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                status = "path-missing";
                return false;
            }

            string cacheKey = "tool-loop:" + path;
            if (ToolLoopDecodedCache.TryGetValue(cacheKey, out decoded))
            {
                status = decoded.Status + " cached=Y";
                _lastDecodeStatus = status;
                return true;
            }

            byte[] raw;
            WaveFormat format;
            string waveStatus;
            if (!TryReadWaveFile(path, out raw, out format, out waveStatus))
            {
                status = "wav-read-failed " + waveStatus;
                _lastDecodeStatus = status;
                return false;
            }

            float[] mono;
            string pcmStatus;
            if (!TryConvertToMono(raw, format, out mono, out pcmStatus))
            {
                status = "wav-pcm-unsupported " + pcmStatus + " " + waveStatus + " fmt=" + DescribeFormat(format);
                _lastDecodeStatus = status;
                return false;
            }

            float[] mono48 = ResampleTo48k(mono, format.SampleRate);
            decoded = new DecodedCuePcm
            {
                CueName = loopInfo.CueName,
                Mono48k = mono48,
                SourceSampleRate = format.SampleRate,
                SourceChannels = Math.Max(1, format.Channels),
                SourceFrames = mono.Length,
                Status = string.Format(
                    CultureInfo.InvariantCulture,
                    "toolwav={0} pcm={1}Hz/{2}ch frames={3} mono48={4} fmt={5}",
                    loopInfo.RelativePath,
                    format.SampleRate,
                    Math.Max(1, format.Channels),
                    mono.Length,
                    mono48.Length,
                    DescribeFormat(format))
            };

            ToolLoopDecodedCache[cacheKey] = decoded;
            status = decoded.Status + " cached=N";
            _lastDecodeStatus = status;
            return true;
        }

        private static bool TryReadWaveFile(string path, out byte[] raw, out WaveFormat format, out string status)
        {
            raw = null;
            format = null;
            status = "wav=?";
            try
            {
                byte[] file = File.ReadAllBytes(path);
                if (file.Length < 44 || !FourCcEquals(file, 0, "RIFF") || !FourCcEquals(file, 8, "WAVE"))
                {
                    status = "not-riff";
                    return false;
                }

                int audioFormat = 0;
                int channels = 0;
                int sampleRate = 0;
                int bits = 0;
                int dataOffset = -1;
                int dataSize = 0;

                int offset = 12;
                while (offset + 8 <= file.Length)
                {
                    string chunk = Encoding.ASCII.GetString(file, offset, 4);
                    int size = BitConverter.ToInt32(file, offset + 4);
                    int dataStart = offset + 8;
                    if (size < 0 || dataStart + size > file.Length)
                        break;

                    if (string.Equals(chunk, "fmt ", StringComparison.Ordinal))
                    {
                        if (size < 16)
                        {
                            status = "fmt-short";
                            return false;
                        }

                        audioFormat = BitConverter.ToUInt16(file, dataStart);
                        channels = BitConverter.ToUInt16(file, dataStart + 2);
                        sampleRate = BitConverter.ToInt32(file, dataStart + 4);
                        bits = BitConverter.ToUInt16(file, dataStart + 14);
                    }
                    else if (string.Equals(chunk, "data", StringComparison.Ordinal))
                    {
                        dataOffset = dataStart;
                        dataSize = size;
                    }

                    offset = dataStart + size + (size & 1);
                }

                if (audioFormat == 0 || channels <= 0 || sampleRate <= 0 || bits <= 0)
                {
                    status = "fmt-missing";
                    return false;
                }

                if (dataOffset < 0 || dataSize <= 0)
                {
                    status = "data-missing";
                    return false;
                }

                raw = new byte[dataSize];
                Buffer.BlockCopy(file, dataOffset, raw, 0, dataSize);
                if (audioFormat == 3)
                    format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                else if (audioFormat == 1 || audioFormat == 0xFFFE)
                    format = new WaveFormat(sampleRate, bits, channels);
                else
                {
                    status = "format=" + audioFormat.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                status = string.Format(CultureInfo.InvariantCulture, "wav bytes={0} format={1} sr={2} ch={3} bits={4}", dataSize, audioFormat, sampleRate, channels, bits);
                return true;
            }
            catch (Exception ex)
            {
                status = DescribeException(ex);
                return false;
            }
        }

        private static bool FourCcEquals(byte[] data, int offset, string value)
        {
            return data != null
                && value != null
                && value.Length == 4
                && offset >= 0
                && offset + 4 <= data.Length
                && data[offset] == value[0]
                && data[offset + 1] == value[1]
                && data[offset + 2] == value[2]
                && data[offset + 3] == value[3];
        }

        private static bool TryResolveWave(IMyAudio audio, MySoundData cue, out object wave, out string status)
        {
            wave = null;
            status = "wave=?";
            object cueBank = GetField(audio, "m_cueBank");
            if (cueBank == null)
            {
                status = "cueBank-missing";
                return false;
            }

            MethodInfo getWave = FindGetCueWaveMethod(cueBank.GetType());
            Type cuePartType = cueBank.GetType().GetNestedType("CuePart", InstanceMembers);
            if (getWave != null && cuePartType != null)
            {
                object[] cueParts = BuildCueParts(cuePartType);
                int waveCount = cue.Waves != null ? Math.Max(1, cue.Waves.Count) : 1;
                MySoundDimensions[] dims = new[] { MySoundDimensions.D2, MySoundDimensions.D3 };
                for (int d = 0; d < dims.Length; d++)
                {
                    for (int waveNumber = 0; waveNumber < waveCount; waveNumber++)
                    {
                        for (int p = 0; p < cueParts.Length; p++)
                        {
                            try
                            {
                                object candidate = getWave.Invoke(cueBank, new[] { cue, dims[d], (object)waveNumber, cueParts[p] });
                                if (candidate != null)
                                {
                                    wave = candidate;
                                    status = string.Format(CultureInfo.InvariantCulture, "cueBank.GetWave dim={0} wave={1} part={2}", dims[d], waveNumber, cueParts[p]);
                                    return true;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }

            object waveBank = GetField(cueBank, "m_waveBank");
            MethodInfo bankGetWave = waveBank?.GetType().GetMethod("GetWave", InstanceMembers, null, new[] { typeof(string) }, null);
            if (waveBank != null && bankGetWave != null && cue.Waves != null)
            {
                for (int i = 0; i < cue.Waves.Count; i++)
                {
                    MyAudioWave cueWave = cue.Waves[i];
                    string[] filenames = new[] { cueWave?.Start, cueWave?.Loop, cueWave?.End };
                    for (int f = 0; f < filenames.Length; f++)
                    {
                        string filename = filenames[f];
                        if (string.IsNullOrWhiteSpace(filename))
                            continue;

                        try
                        {
                            object candidate = bankGetWave.Invoke(waveBank, new object[] { filename });
                            if (candidate != null)
                            {
                                wave = candidate;
                                status = "waveBank.GetWave file=" + filename;
                                return true;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            status = "no-wave-method";
            return false;
        }

        private static MethodInfo FindGetCueWaveMethod(Type cueBankType)
        {
            if (cueBankType == null)
                return null;

            MethodInfo[] methods = cueBankType.GetMethods(InstanceMembers);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "GetWave", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 4
                    && parameters[0].ParameterType == typeof(MySoundData)
                    && parameters[1].ParameterType == typeof(MySoundDimensions)
                    && parameters[2].ParameterType == typeof(int))
                    return method;
            }

            return null;
        }

        private static object[] BuildCueParts(Type cuePartType)
        {
            List<object> parts = new List<object>();
            AddCuePart(parts, cuePartType, "Start");
            AddCuePart(parts, cuePartType, "Loop");
            AddCuePart(parts, cuePartType, "End");
            if (parts.Count == 0)
            {
                Array values = Enum.GetValues(cuePartType);
                for (int i = 0; i < values.Length; i++)
                    parts.Add(values.GetValue(i));
            }

            return parts.ToArray();
        }

        private static void AddCuePart(List<object> parts, Type cuePartType, string name)
        {
            try
            {
                if (Enum.IsDefined(cuePartType, name))
                    parts.Add(Enum.Parse(cuePartType, name));
            }
            catch
            {
            }
        }

        private static bool TryCopyWaveBytes(object wave, out byte[] raw, out string status)
        {
            raw = null;
            status = "raw=?";
            AudioBuffer buffer = GetMember<AudioBuffer>(wave, "Buffer");
            if (buffer != null && buffer.AudioBytes > 0 && buffer.AudioDataPointer != IntPtr.Zero)
            {
                try
                {
                    raw = new byte[buffer.AudioBytes];
                    Marshal.Copy(buffer.AudioDataPointer, raw, 0, raw.Length);
                    status = "buffer bytes=" + raw.Length.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex)
                {
                    status = "buffer-copy-failed:" + DescribeException(ex);
                }
            }

            SoundStream soundStream = GetMember<SoundStream>(wave, "Stream");
            if (soundStream != null && soundStream.CanRead && soundStream.Length > 0 && soundStream.Length <= int.MaxValue)
            {
                try
                {
                    long originalPosition = soundStream.CanSeek ? soundStream.Position : 0L;
                    if (soundStream.CanSeek)
                        soundStream.Position = 0L;
                    raw = new byte[(int)soundStream.Length];
                    int offset = 0;
                    while (offset < raw.Length)
                    {
                        int read = soundStream.Read(raw, offset, raw.Length - offset);
                        if (read <= 0)
                            break;
                        offset += read;
                    }

                    if (soundStream.CanSeek)
                        soundStream.Position = originalPosition;
                    if (offset != raw.Length)
                        Array.Resize(ref raw, offset);

                    status = "stream bytes=" + raw.Length.ToString(CultureInfo.InvariantCulture);
                    return raw.Length > 0;
                }
                catch (Exception ex)
                {
                    status = "stream-copy-failed:" + DescribeException(ex);
                }
            }

            return false;
        }

        private static bool TryConvertToMono(byte[] raw, WaveFormat format, out float[] mono, out string status)
        {
            mono = null;
            status = "pcm=?";
            if (raw == null || raw.Length == 0 || format == null)
            {
                status = "empty";
                return false;
            }

            int channels = Math.Max(1, format.Channels);
            int bits = Math.Max(1, format.BitsPerSample);
            int blockAlign = Math.Max(1, format.BlockAlign);
            int bytesPerSample = Math.Max(1, bits / 8);
            int frames = raw.Length / blockAlign;
            int maxFrames = Math.Min(frames, Math.Max(1, format.SampleRate * 3));
            string encoding = format.Encoding.ToString();
            if (encoding.IndexOf("Adpcm", StringComparison.OrdinalIgnoreCase) >= 0
                || encoding.IndexOf("Wma", StringComparison.OrdinalIgnoreCase) >= 0
                || encoding.IndexOf("Xma", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status = "compressed-encoding=" + encoding;
                return false;
            }

            bool isFloat = encoding.IndexOf("Float", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isPcm = isFloat || encoding.IndexOf("Pcm", StringComparison.OrdinalIgnoreCase) >= 0 || encoding.IndexOf("Extensible", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isPcm)
            {
                status = "encoding=" + encoding;
                return false;
            }
            if (bits != 8 && bits != 16 && bits != 24 && bits != 32)
            {
                status = "unsupported-bits=" + bits.ToString(CultureInfo.InvariantCulture) + " encoding=" + encoding;
                return false;
            }
            if (isFloat && bits != 32)
            {
                status = "unsupported-float-bits=" + bits.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (blockAlign < channels * bytesPerSample)
            {
                status = string.Format(CultureInfo.InvariantCulture, "bad-align={0} channels={1} bytes={2}", blockAlign, channels, bytesPerSample);
                return false;
            }

            mono = new float[maxFrames];
            float peak = 0f;
            for (int frame = 0; frame < maxFrames; frame++)
            {
                int frameOffset = frame * blockAlign;
                float sum = 0f;
                int usedChannels = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int offset = frameOffset + ch * bytesPerSample;
                    if (offset + bytesPerSample > raw.Length)
                        break;

                    sum += ReadSample(raw, offset, bits, isFloat);
                    usedChannels++;
                }

                float value = usedChannels > 0 ? sum / usedChannels : 0f;
                mono[frame] = value;
                float abs = Math.Abs(value);
                if (abs > peak)
                    peak = abs;
            }

            if (peak > 1.5f)
            {
                float scale = 1f / peak;
                for (int i = 0; i < mono.Length; i++)
                    mono[i] *= scale;
            }

            status = string.Format(CultureInfo.InvariantCulture, "encoding={0} bits={1} frames={2} peak={3:0.000}", encoding, bits, mono.Length, peak);
            return mono.Length > 0;
        }

        private static float ReadSample(byte[] raw, int offset, int bits, bool isFloat)
        {
            if (isFloat && bits == 32)
                return Clamp(BitConverter.ToSingle(raw, offset), -4f, 4f);

            switch (bits)
            {
                case 8:
                    return (raw[offset] - 128) / 128f;
                case 16:
                    return BitConverter.ToInt16(raw, offset) / 32768f;
                case 24:
                    int sample24 = raw[offset] | (raw[offset + 1] << 8) | (raw[offset + 2] << 16);
                    if ((sample24 & 0x800000) != 0)
                        sample24 |= unchecked((int)0xFF000000);
                    return sample24 / 8388608f;
                case 32:
                    return BitConverter.ToInt32(raw, offset) / 2147483648f;
                default:
                    return 0f;
            }
        }

        private static float[] ResampleTo48k(float[] source, int sourceRate)
        {
            if (source == null || source.Length == 0)
                return new float[1];
            if (sourceRate == OutputSampleRate || sourceRate <= 0)
                return source;

            int targetLength = Math.Max(1, (int)Math.Round(source.Length * (OutputSampleRate / (double)sourceRate)));
            float[] result = new float[targetLength];
            double ratio = sourceRate / (double)OutputSampleRate;
            for (int i = 0; i < targetLength; i++)
            {
                double src = i * ratio;
                int i0 = Math.Min(source.Length - 1, (int)src);
                int i1 = Math.Min(source.Length - 1, i0 + 1);
                float t = (float)(src - i0);
                result[i] = source[i0] + (source[i1] - source[i0]) * t;
            }

            return result;
        }

        private static float[] BuildWetTail(float[] dryMono48, RealisticSoundPlusSettings settings, float inputVolume, float maxOutputSeconds, out DspTailInfo info)
        {
            return BuildWetTail(dryMono48, settings, inputVolume, maxOutputSeconds, false, out info);
        }

        private static float[] BuildWetTail(float[] dryMono48, RealisticSoundPlusSettings settings, float inputVolume, float maxOutputSeconds, bool automaticWetSend, out DspTailInfo info)
        {
            return BuildWetTail(dryMono48, settings, inputVolume, maxOutputSeconds, automaticWetSend, true, out info);
        }

        private static float[] BuildWetTail(float[] dryMono48, RealisticSoundPlusSettings settings, float inputVolume, float maxOutputSeconds, bool automaticWetSend, bool shapeAutomaticOutput, out DspTailInfo info)
        {
            dryMono48 = dryMono48 != null && dryMono48.Length > 0 ? dryMono48 : CreateTestImpulse();
            DspParameters parameters = ResolveDspParameters(settings);
            float roomSize = parameters.RoomSize;
            float diffusion = Clamp01(parameters.Diffusion);
            float density = Clamp(parameters.Density / 100f, 0f, 1f);
            if (automaticWetSend)
            {
                diffusion = Math.Max(diffusion, 0.72f);
                density = Math.Max(density, 0.58f);
            }

            float decaySeconds = parameters.DecaySeconds;
            float preDelaySeconds = parameters.PredelayMs / 1000f;
            float lateDelaySeconds = parameters.LateDelayMs / 1000f;
            float feedDelaySeconds = preDelaySeconds + lateDelaySeconds;
            if (automaticWetSend)
            {
                dryMono48 = DecorrelateAutomaticWetInput(dryMono48);
                feedDelaySeconds = Clamp(feedDelaySeconds * 0.35f, 0.032f, AutoWetFeedDelayMaxSeconds);
                preDelaySeconds = feedDelaySeconds;
                lateDelaySeconds = 0f;
            }

            float tailSeconds = Clamp(decaySeconds + preDelaySeconds + lateDelaySeconds + 1.25f, 1.25f, 24f);
            int inputOffset = Math.Max(0, (int)Math.Round(feedDelaySeconds * OutputSampleRate));
            int totalFrames = Math.Max(OutputSampleRate / 2, dryMono48.Length + inputOffset + (int)Math.Round(tailSeconds * OutputSampleRate));
            if (maxOutputSeconds > 0f)
            {
                int maxFrames = Math.Max(OutputSampleRate / 2, (int)Math.Round(maxOutputSeconds * OutputSampleRate));
                totalFrames = Math.Min(totalFrames, maxFrames);
            }

            float[] output = new float[totalFrames * OutputChannels];

            int[] delays = BuildDelayLineLengths(roomSize, density);
            float[][] lines = new float[delays.Length][];
            int[] positions = new int[delays.Length];
            float[] dampState = new float[delays.Length];
            float[] y = new float[delays.Length];
            float[] mixed = new float[delays.Length];
            float[] feedback = new float[delays.Length];
            for (int i = 0; i < delays.Length; i++)
            {
                lines[i] = new float[delays[i]];
                feedback[i] = (float)Math.Pow(10.0, -3.0 * (delays[i] / (double)OutputSampleRate) / Math.Max(0.35f, decaySeconds));
            }

            float toneHz = parameters.ToneHz;
            float hfDb = parameters.HighFrequencyDb;
            float hfFactor = Clamp(DbToLinear(hfDb), 0.05f, 1f);
            float cutoff = Clamp(toneHz * (0.55f + 0.45f * hfFactor), 400f, 20000f);
            float lowPassA = (float)Math.Exp(-2.0 * Math.PI * cutoff / OutputSampleRate);
            float inputGain = Clamp(inputVolume, 0f, 2f) * (0.32f + 0.68f * density);
            float lateGain = DbToLinear(parameters.TailGainDb) * (automaticWetSend ? 0.078f : 0.032f);
            float earlyBase = DbToLinear(parameters.EarlyGainDb);
            float earlyGain = automaticWetSend
                ? Math.Max(earlyBase, 0.14f) * 0.055f
                : earlyBase * 0.045f;
            float diffusionMix = automaticWetSend
                ? 0.88f + diffusion * 0.12f
                : 0.35f + diffusion * 0.65f;

            int[] earlyTaps = BuildEarlyTapOffsets(roomSize, preDelaySeconds);
            for (int frame = 0; frame < totalFrames; frame++)
            {
                int inputIndex = frame - inputOffset;
                float input = inputIndex >= 0 && inputIndex < dryMono48.Length ? dryMono48[inputIndex] * inputGain : 0f;

                for (int i = 0; i < lines.Length; i++)
                {
                    float read = lines[i][positions[i]];
                    dampState[i] = read * (1f - lowPassA) + dampState[i] * lowPassA;
                    y[i] = dampState[i];
                }

                Hadamard8(y, mixed);
                for (int i = 0; i < lines.Length; i++)
                {
                    float spreadInput = input * (i % 2 == 0 ? 0.85f : -0.73f) * (1f - i * 0.035f);
                    float diffuse = mixed[i] * diffusionMix + y[i] * (1f - diffusionMix);
                    lines[i][positions[i]] = spreadInput + diffuse * feedback[i];
                    positions[i]++;
                    if (positions[i] >= lines[i].Length)
                        positions[i] = 0;
                }

                float lateL = (y[0] - y[1] + y[2] * 0.82f - y[3] * 0.71f + y[4] * 0.64f - y[5] * 0.55f + y[6] * 0.48f - y[7] * 0.41f) * lateGain;
                float lateR = (y[7] - y[6] + y[5] * 0.82f - y[4] * 0.71f + y[3] * 0.64f - y[2] * 0.55f + y[1] * 0.48f - y[0] * 0.41f) * lateGain;
                float earlyL = 0f;
                float earlyR = 0f;
                for (int t = 0; t < earlyTaps.Length; t++)
                {
                    int tapIndex = frame - earlyTaps[t];
                    if (tapIndex < 0 || tapIndex >= dryMono48.Length)
                        continue;

                    float tap = dryMono48[tapIndex] * earlyGain * (1f - t * 0.11f);
                    if ((t & 1) == 0)
                        earlyL += tap;
                    else
                        earlyR += tap;
                }

                int outIndex = frame * OutputChannels;
                output[outIndex] = SoftClip(earlyL + lateL);
                output[outIndex + 1] = SoftClip(earlyR + lateR);
            }

            if (automaticWetSend && shapeAutomaticOutput)
                ShapeAutomaticWetOutput(output);

            info = new DspTailInfo
            {
                DelayLineCount = delays.Length,
                RoomSize = roomSize,
                Diffusion = diffusion,
                Density = density,
                DecaySeconds = decaySeconds,
                TailSeconds = output.Length / (float)(OutputSampleRate * OutputChannels),
                CutoffHz = cutoff,
                PreDelayMs = preDelaySeconds * 1000f,
                LateDelayMs = lateDelaySeconds * 1000f,
                EquivalentRadius = parameters.EquivalentRadius,
                AirPressure = parameters.AirPressure,
                Source = parameters.Source
            };
            return output;
        }

        private static float[] DecorrelateAutomaticWetInput(float[] source)
        {
            if (source == null || source.Length == 0)
                return source;

            int[] taps =
            {
                0,
                (int)Math.Round(0.0070 * OutputSampleRate),
                (int)Math.Round(0.0130 * OutputSampleRate),
                (int)Math.Round(0.0230 * OutputSampleRate),
                (int)Math.Round(0.0370 * OutputSampleRate),
                (int)Math.Round(0.0590 * OutputSampleRate)
            };
            float[] gains = { 0.78f, -0.20f, 0.15f, -0.10f, 0.07f, -0.045f };
            int maxTap = taps[taps.Length - 1];
            float[] diffused = new float[source.Length + maxTap + 1];

            float previousSource = 0f;
            float highPass = 0f;
            for (int i = 0; i < source.Length; i++)
            {
                float x = Clamp(source[i], -2f, 2f);
                highPass = highPass * 0.995f + x - previousSource;
                previousSource = x;

                float feed = SoftClip(x * 0.86f + highPass * 0.14f);

                for (int t = 0; t < taps.Length; t++)
                    diffused[i + taps[t]] += feed * gains[t];
            }

            float peak = 0f;
            for (int i = 0; i < diffused.Length; i++)
                peak = Math.Max(peak, Math.Abs(diffused[i]));

            if (peak > 1.25f)
            {
                float scale = 1.25f / peak;
                for (int i = 0; i < diffused.Length; i++)
                    diffused[i] *= scale;
            }

            return diffused;
        }

        private static void ShapeAutomaticWetOutput(float[] output)
        {
            if (output == null || output.Length < OutputChannels)
                return;

            int frames = output.Length / OutputChannels;
            int fadeFrames = Math.Min(frames, (int)Math.Round(OutputSampleRate * 0.040));
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float t = frame / (float)Math.Max(1, fadeFrames);
                float gain = t * t;
                int index = frame * OutputChannels;
                output[index] *= gain;
                output[index + 1] *= gain;
            }

            int fadeOutFrames = Math.Min(frames, (int)Math.Round(OutputSampleRate * 0.060));
            for (int frame = 0; frame < fadeOutFrames; frame++)
            {
                float t = frame / (float)Math.Max(1, fadeOutFrames);
                float gain = (1f - t) * (1f - t);
                int index = (frames - 1 - frame) * OutputChannels;
                output[index] *= gain;
                output[index + 1] *= gain;
            }
        }

        private static DspParameters ResolveDspParameters(RealisticSoundPlusSettings settings)
        {
            DspParameters parameters;
            if (TryResolveAutoParameters(out parameters))
            {
                parameters = ApplyAutoModifiers(parameters, settings);
                return ApplyPressureLossTone(parameters, settings);
            }

            parameters = new DspParameters
            {
                Source = "manual",
                EquivalentRadius = 0f,
                RoomSize = Clamp01(settings?.GlobalReverbRoomSize ?? 0.8f),
                Diffusion = Clamp01(settings?.GlobalReverbDiffusion ?? 0.8f),
                Density = Clamp(settings?.GlobalReverbDensity ?? 100f, 0f, 100f),
                DecaySeconds = Clamp(settings?.GlobalReverbDecaySeconds ?? 6f, 0.35f, 30f),
                EarlyGainDb = Clamp(settings?.GlobalReverbEarlyGainDb ?? -6f, -60f, 20f),
                TailGainDb = Clamp(settings?.GlobalReverbTailGainDb ?? 0f, -60f, 20f),
                PredelayMs = Clamp(settings?.GlobalReverbPredelayMs ?? 30f, 0f, 300f),
                LateDelayMs = Clamp(settings?.GlobalReverbLateDelayMs ?? 40f, 0f, 300f),
                ToneHz = Clamp(settings?.GlobalReverbToneHz ?? 12000f, 500f, 20000f),
                HighFrequencyDb = Clamp(settings?.GlobalReverbHighFrequencyDb ?? 0f, -60f, 0f)
            };
            return ApplyPressureLossTone(parameters, settings);
        }

        private static bool TryResolveAutoParameters(out DspParameters parameters)
        {
            parameters = default(DspParameters);
            if (!V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample) || !sample.ReverbRoomAvailable)
                return false;

            parameters = new DspParameters
            {
                Source = sample.ReverbRoomSource ?? "ray",
                EquivalentRadius = Clamp(sample.ReverbRoomEquivalentRadius, 0.8f, Math.Max(1f, sample.RayLength)),
                RoomSize = Clamp01(sample.ReverbAutoRoomSize),
                Diffusion = Clamp01(sample.ReverbAutoDiffusion),
                Density = Clamp(sample.ReverbAutoDensity, 0f, 100f),
                DecaySeconds = Clamp(sample.ReverbAutoDecaySeconds, 0.35f, 30f),
                EarlyGainDb = Clamp(sample.ReverbAutoEarlyGainDb, -60f, 20f),
                TailGainDb = Clamp(sample.ReverbAutoTailGainDb, -60f, 20f),
                PredelayMs = Clamp(sample.ReverbAutoPredelayMs, 0f, 300f),
                LateDelayMs = Clamp(sample.ReverbAutoLateDelayMs, 0f, 300f),
                ToneHz = Clamp(sample.ReverbAutoToneHz, 500f, 20000f),
                HighFrequencyDb = Clamp(sample.ReverbAutoHighFrequencyDb, -60f, 0f)
            };
            return true;
        }

        private static DspParameters ApplyAutoModifiers(DspParameters parameters, RealisticSoundPlusSettings settings)
        {
            if (settings == null)
                return parameters;

            parameters.Source = string.IsNullOrWhiteSpace(parameters.Source) ? "ray+mod" : parameters.Source + "+mod";
            parameters.RoomSize = Clamp01(parameters.RoomSize * Clamp(settings.GlobalReverbRoomSizeModifier, 0.25f, 2f));
            parameters.Diffusion = Clamp01(parameters.Diffusion * Clamp(settings.GlobalReverbDiffusionModifier, 0.25f, 2f));
            parameters.DecaySeconds = Clamp(parameters.DecaySeconds * Clamp(settings.GlobalReverbDecayModifier, 0.25f, 2.5f), 0.35f, 30f);
            parameters.EarlyGainDb = Clamp(parameters.EarlyGainDb + Clamp(settings.GlobalReverbEarlyGainOffsetDb, -12f, 12f), -60f, 20f);
            parameters.TailGainDb = Clamp(parameters.TailGainDb + Clamp(settings.GlobalReverbTailGainOffsetDb, -12f, 12f), -60f, 20f);
            parameters.PredelayMs = Clamp(parameters.PredelayMs * Clamp(settings.GlobalReverbPredelayModifier, 0.25f, 2.5f), 0f, 300f);
            parameters.LateDelayMs = Clamp(parameters.LateDelayMs * Clamp(settings.GlobalReverbLateDelayModifier, 0.25f, 2.5f), 0f, 300f);
            parameters.Density = Clamp(parameters.Density * Clamp(settings.GlobalReverbDensityModifier, 0.5f, 1.5f), 0f, 100f);
            parameters.ToneHz = Clamp(parameters.ToneHz * Clamp(settings.GlobalReverbToneModifier, 0.5f, 2f), 500f, 20000f);
            parameters.HighFrequencyDb = Clamp(parameters.HighFrequencyDb + Clamp(settings.GlobalReverbHighFrequencyOffsetDb, -12f, 12f), -60f, 0f);
            return parameters;
        }

        private static DspParameters ApplyPressureLossTone(DspParameters parameters, RealisticSoundPlusSettings settings)
        {
            float pressure = ResolveReverbAirPressure(settings);
            parameters.AirPressure = pressure;
            if (pressure >= 0.995f)
                return parameters;

            float pressureCurve = (float)Math.Sqrt(Clamp01(pressure));
            float vacuumToneHz = 650f;
            float hfLossDb = (1f - pressureCurve) * 36f;
            parameters.ToneHz = Clamp(vacuumToneHz + (parameters.ToneHz - vacuumToneHz) * pressureCurve, 500f, 20000f);
            parameters.HighFrequencyDb = Clamp(parameters.HighFrequencyDb - hfLossDb, -60f, 0f);
            parameters.Source = string.IsNullOrWhiteSpace(parameters.Source)
                ? "pressure"
                : parameters.Source + "+p" + pressure.ToString("0.00", CultureInfo.InvariantCulture);
            return parameters;
        }

        private static float ResolveReverbAirPressure(RealisticSoundPlusSettings settings)
        {
            if (settings != null && settings.PlayerFilterAtmosphereOverrideEnabled)
                return Clamp01(settings.PlayerFilterAtmosphereOverride);

            if (V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample))
                return Clamp01(sample.LocalAtmosphere);

            return 1f;
        }

        private static int[] BuildDelayLineLengths(float roomSize, float density)
        {
            float[] baseMs = new[] { 17.9f, 23.7f, 29.3f, 31.7f, 37.1f, 41.9f, 43.7f, 47.9f };
            float scale = 0.85f + roomSize * 3.35f;
            float densityScale = 0.82f + density * 0.25f;
            int[] result = new int[baseMs.Length];
            for (int i = 0; i < baseMs.Length; i++)
            {
                int samples = (int)Math.Round(baseMs[i] * scale * densityScale * OutputSampleRate / 1000f);
                result[i] = Math.Max(37, samples | 1);
            }

            return result;
        }

        private static int[] BuildEarlyTapOffsets(float roomSize, float preDelaySeconds)
        {
            float scale = 0.7f + roomSize * 2.4f;
            float preMs = preDelaySeconds * 1000f;
            float[] taps = new[] { 8f, 13f, 21f, 34f, 55f, 89f };
            int[] result = new int[taps.Length];
            for (int i = 0; i < taps.Length; i++)
                result[i] = Math.Max(1, (int)Math.Round((preMs + taps[i] * scale) * OutputSampleRate / 1000f));
            return result;
        }

        private static void Hadamard8(float[] input, float[] output)
        {
            float a0 = input[0] + input[1];
            float a1 = input[0] - input[1];
            float a2 = input[2] + input[3];
            float a3 = input[2] - input[3];
            float a4 = input[4] + input[5];
            float a5 = input[4] - input[5];
            float a6 = input[6] + input[7];
            float a7 = input[6] - input[7];
            float b0 = a0 + a2;
            float b1 = a1 + a3;
            float b2 = a0 - a2;
            float b3 = a1 - a3;
            float b4 = a4 + a6;
            float b5 = a5 + a7;
            float b6 = a4 - a6;
            float b7 = a5 - a7;
            const float norm = 0.3535533906f;
            output[0] = (b0 + b4) * norm;
            output[1] = (b1 + b5) * norm;
            output[2] = (b2 + b6) * norm;
            output[3] = (b3 + b7) * norm;
            output[4] = (b0 - b4) * norm;
            output[5] = (b1 - b5) * norm;
            output[6] = (b2 - b6) * norm;
            output[7] = (b3 - b7) * norm;
        }

        private static bool TryPlayWetTail(IMyAudio audio, XAudio2 engine, float[] samples, float wetVolume, string reason, out string status)
        {
            status = "route=?";
            if (engine == null || samples == null || samples.Length == 0)
            {
                status = "empty";
                return false;
            }

            SourceVoice source = null;
            DataStream stream = null;
            try
            {
                WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);
                stream = new DataStream(samples.Length * sizeof(float), true, true);
                stream.WriteRange(samples);
                stream.Position = 0;
                AudioBuffer buffer = new AudioBuffer(stream)
                {
                    Flags = BufferFlags.EndOfStream
                };

                source = new SourceVoice(engine, format);
                Voice output = ResolveGameOutputVoice(audio);
                string outputStatus = "default";
                if (output != null)
                {
                    source.SetOutputVoices(new[] { new VoiceSendDescriptor(output) });
                    outputStatus = output.GetType().Name;
                }

                float volume = Clamp(wetVolume * CalculateWetSend(SettingsManager.Current), 0f, 4f);
                source.SetVolume(volume, 0);
                source.SubmitSourceBuffer(buffer, null);
                source.Start();
                ActiveTails.Add(new ActiveTailState
                {
                    Source = source,
                    Stream = stream,
                    Reason = reason,
                    CreatedUtc = DateTime.UtcNow,
                    ExpiresUtc = DateTime.UtcNow + TimeSpan.FromSeconds(samples.Length / (double)(OutputSampleRate * OutputChannels) + 0.5),
                    Frames = samples.Length / OutputChannels,
                    Volume = volume
                });

                status = string.Format(CultureInfo.InvariantCulture, "out={0} vol={1:0.00} active={2}", outputStatus, volume, ActiveTails.Count);
                return true;
            }
            catch (Exception ex)
            {
                Dispose(source);
                Dispose(stream);
                status = DescribeException(ex);
                V2DebugLog.WriteEvent("dsp-reverb-play-failed", status);
                return false;
            }
        }

        private static string PlayDryCue(IMyAudio audio, string cueName)
        {
            try
            {
                MyCueId cueId = new MyCueId(MyStringHash.GetOrCompute(cueName));
                IMySourceVoice voice = audio.GetSound(cueId, null, MySoundDimensions.D2);
                if (voice == null)
                    return "dry=missing";

                voice.SetVolume(1f);
                voice.Start(false, false);
                return "dry=played";
            }
            catch (Exception ex)
            {
                return "dry=failed:" + ex.GetType().Name;
            }
        }

        private static XAudio2 ResolveEngine(IMyAudio audio)
        {
            return GetField(audio, "m_audioEngine") as XAudio2;
        }

        private static Voice ResolveGameOutputVoice(IMyAudio audio)
        {
            return GetField(audio, "m_gameAudioVoice") as Voice
                ?? GetField(audio, "m_masterVoice") as Voice;
        }

        private static object GetField(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                return instance.GetType().GetField(name, InstanceMembers)?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static T GetMember<T>(object instance, string name) where T : class
        {
            if (instance == null)
                return null;

            try
            {
                Type type = instance.GetType();
                object value = type.GetProperty(name, InstanceMembers)?.GetValue(instance, null);
                if (value == null)
                    value = type.GetField(name, InstanceMembers)?.GetValue(instance);
                return value as T;
            }
            catch
            {
                return null;
            }
        }

        private static void AddWetTailCache(string key, float[] wet)
        {
            if (string.IsNullOrWhiteSpace(key) || wet == null)
                return;

            WetTailCache[key] = wet;
            WetTailCacheOrder.Enqueue(key);
            while (WetTailCache.Count > MaxWetTailCacheEntries && WetTailCacheOrder.Count > 0)
            {
                string remove = WetTailCacheOrder.Dequeue();
                if (WetTailCache.ContainsKey(remove) && !string.Equals(remove, key, StringComparison.OrdinalIgnoreCase))
                    WetTailCache.Remove(remove);
            }
        }

        private static void AddToolLoopWetCache(string key, float[] wet)
        {
            if (string.IsNullOrWhiteSpace(key) || wet == null)
                return;

            ToolLoopWetCache[key] = wet;
            ToolLoopWetCacheOrder.Enqueue(key);
            while (ToolLoopWetCache.Count > MaxToolLoopWetCacheEntries && ToolLoopWetCacheOrder.Count > 0)
            {
                string remove = ToolLoopWetCacheOrder.Dequeue();
                if (ToolLoopWetCache.ContainsKey(remove) && !string.Equals(remove, key, StringComparison.OrdinalIgnoreCase))
                    ToolLoopWetCache.Remove(remove);
            }
        }

        private static float[] CreateTestImpulse()
        {
            int frames = OutputSampleRate / 10;
            float[] samples = new float[frames];
            uint seed = 0xC0DEC0DEu;
            int burstFrames = OutputSampleRate / 70;
            for (int i = 0; i < burstFrames && i < samples.Length; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                float noise = ((seed >> 8) / 16777215f) * 2f - 1f;
                float envelope = (float)Math.Pow(1f - i / (float)burstFrames, 2.0);
                samples[i] = noise * envelope;
            }

            samples[0] = 1f;
            return samples;
        }

        private static string BuildDspSignature(RealisticSoundPlusSettings settings)
        {
            DspParameters parameters = ResolveDspParameters(settings);

            return string.Format(
                CultureInfo.InvariantCulture,
                "dsp|{0}|{1:0.000}|{2:0.000}|{3:0.000}|{4:0.000}|{5:0.000}|{6:0.000}|{7:0.000}|{8:0.000}|{9:0.0}|{10:0.0}|{11:0.0}|p{12:0.000}",
                parameters.Source ?? "?",
                parameters.Diffusion,
                parameters.RoomSize,
                parameters.DecaySeconds,
                parameters.EarlyGainDb,
                parameters.TailGainDb,
                parameters.PredelayMs,
                parameters.LateDelayMs,
                parameters.Density,
                parameters.ToneHz,
                parameters.HighFrequencyDb,
                parameters.EquivalentRadius,
                parameters.AirPressure);
        }

        private static string NormalizeCueName(string cueName)
        {
            return string.IsNullOrWhiteSpace(cueName) ? "ArcPlayStepsMetal" : cueName.Trim();
        }

        private static bool IsAutomaticReason(string reason)
        {
            return reason != null && reason.StartsWith("auto-", StringComparison.OrdinalIgnoreCase);
        }

        private static float CalculateWetSend(RealisticSoundPlusSettings settings)
        {
            return Clamp(settings?.GlobalReverbWetSend ?? 1f, 0f, 4f);
        }

        private static string DescribeFormat(WaveFormat format)
        {
            if (format == null)
                return "?";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}Hz/{1}ch/{2}bit/{3}/align{4}",
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                format.Encoding,
                format.BlockAlign);
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
            if (Contains(status, "reason=auto-"))
                WriteThrottledEvent("dsp-reverb-cue", status, BuildAutoStatusThrottleKey(status), TimeSpan.FromSeconds(3));
            else
                V2DebugLog.WriteEvent("dsp-reverb-cue", status);
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

        private static bool IsPlaying(SourceVoice source)
        {
            if (source == null)
                return false;

            try
            {
                return source.State.BuffersQueued > 0;
            }
            catch
            {
                return false;
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
                if (now - pair.Value > TimeSpan.FromSeconds(8))
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

        private static void PurgeActiveToolLoops(DateTime now)
        {
            if (ActiveToolLoops.Count == 0)
                return;

            List<IMySourceVoice> remove = null;
            foreach (KeyValuePair<IMySourceVoice, ActiveToolLoopState> pair in ActiveToolLoops)
            {
                IMySourceVoice original = pair.Key;
                ActiveToolLoopState loop = pair.Value;
                bool originalGone = original == null || !original.IsValid || !original.IsPlaying;
                TimeSpan staleWindow = originalGone ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromMilliseconds(450);
                bool stale = now - loop.LastSeenUtc > staleWindow;
                if (!originalGone && !stale && IsPlaying(loop.Source))
                    continue;

                if (remove == null)
                    remove = new List<IMySourceVoice>();
                remove.Add(original);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
            {
                if (!ActiveToolLoops.TryGetValue(remove[i], out ActiveToolLoopState loop))
                    continue;

                DisposeToolLoop(loop, "source-ended");
                ActiveToolLoops.Remove(remove[i]);
            }
        }

        private static void DisposeActiveTails(string reason)
        {
            int count = ActiveTails.Count;
            for (int i = ActiveTails.Count - 1; i >= 0; i--)
                DisposeTail(ActiveTails[i], reason);
            ActiveTails.Clear();
            if (count > 0)
                V2DebugLog.WriteEvent("dsp-reverb-clear", "reason=" + (reason ?? "?") + " tails=" + count.ToString(CultureInfo.InvariantCulture));
        }

        private static void DisposeActiveToolLoops(string reason)
        {
            int count = ActiveToolLoops.Count;
            foreach (ActiveToolLoopState loop in ActiveToolLoops.Values)
                DisposeToolLoop(loop, reason);

            ActiveToolLoops.Clear();
            if (count > 0)
                V2DebugLog.WriteEvent("dsp-tool-loop-clear", "reason=" + (reason ?? "?") + " loops=" + count.ToString(CultureInfo.InvariantCulture));
        }

        private static void DisposeTail(ActiveTailState tail, string reason)
        {
            if (tail == null)
                return;

            try
            {
                tail.Source?.Stop();
                tail.Source?.FlushSourceBuffers();
                tail.Source?.DestroyVoice();
            }
            catch
            {
            }

            Dispose(tail.Source);
            Dispose(tail.Stream);
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "reason={0} source={1} frames={2} vol={3:0.00}",
                reason ?? "?",
                tail.Reason ?? "?",
                tail.Frames,
                tail.Volume);
            if (tail.Reason != null && tail.Reason.StartsWith("auto-", StringComparison.OrdinalIgnoreCase))
                WriteThrottledEvent("dsp-reverb-dispose", message, "dispose:" + tail.Reason, TimeSpan.FromSeconds(3));
            else
                V2DebugLog.WriteEvent("dsp-reverb-dispose", message);
        }

        private static void DisposeToolLoop(ActiveToolLoopState loop, string reason)
        {
            if (loop == null)
                return;

            try
            {
                loop.Source?.Stop();
                loop.Source?.FlushSourceBuffers();
                loop.Source?.DestroyVoice();
            }
            catch
            {
            }

            Dispose(loop.Source);
            Dispose(loop.Stream);
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "reason={0} cue={1} frames={2} vol={3:0.00}",
                reason ?? "?",
                loop.CueName ?? "?",
                loop.Frames,
                loop.Volume);
            WriteThrottledEvent("dsp-tool-loop-dispose", message, "tool-dispose:" + (loop.CueName ?? "?"), TimeSpan.FromSeconds(2));
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

        private static float DbToLinear(float db)
        {
            if (db <= -59.9f)
                return 0f;
            return (float)Math.Pow(10.0, db / 20.0);
        }

        private static float SoftClip(float value)
        {
            return value / (1f + Math.Abs(value));
        }

        private static void WriteThrottledEvent(string kind, string message, string key, TimeSpan interval)
        {
            DateTime now = DateTime.UtcNow;
            string safeKey = kind + ":" + (key ?? "?");
            if (LogThrottle.TryGetValue(safeKey, out DateTime last) && now - last < interval)
                return;

            LogThrottle[safeKey] = now;
            V2DebugLog.WriteEvent(kind, message);
        }

        private static string BuildAutoStatusThrottleKey(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return "?";

            int cueIndex = status.IndexOf("cue=", StringComparison.OrdinalIgnoreCase);
            int reasonIndex = status.IndexOf("reason=", StringComparison.OrdinalIgnoreCase);
            string cue = cueIndex >= 0 ? ReadToken(status, cueIndex + 4) : "?";
            string reason = reasonIndex >= 0 ? ReadToken(status, reasonIndex + 7) : "auto";
            return reason + ":" + cue;
        }

        private static string ReadToken(string value, int start)
        {
            if (string.IsNullOrWhiteSpace(value) || start < 0 || start >= value.Length)
                return "?";

            int end = start;
            while (end < value.Length && !char.IsWhiteSpace(value[end]))
                end++;

            return value.Substring(start, Math.Max(0, end - start));
        }

        private static bool Contains(string value, string fragment)
        {
            return (value ?? string.Empty).IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static uint HashString(string value)
        {
            uint hash = 2166136261u;
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619u;
                }
            }

            return hash;
        }

        private static uint HashUInt(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return min;
            if (value <= min)
                return min;
            return value >= max ? max : value;
        }

        private static float Clamp01(float value)
        {
            return Clamp(value, 0f, 1f);
        }

        private static string Format(float value, int decimals)
        {
            return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private sealed class DecodedCuePcm
        {
            public string CueName;
            public float[] Mono48k;
            public int SourceSampleRate;
            public int SourceChannels;
            public int SourceFrames;
            public string Status;
        }

        private sealed class ActiveTailState
        {
            public SourceVoice Source;
            public DataStream Stream;
            public string Reason;
            public DateTime CreatedUtc;
            public DateTime ExpiresUtc;
            public int Frames;
            public float Volume;
        }

        private sealed class ActiveToolLoopState
        {
            public IMySourceVoice OriginalVoice;
            public SourceVoice Source;
            public DataStream Stream;
            public string CueName;
            public string Path;
            public DateTime CreatedUtc;
            public DateTime LastSeenUtc;
            public int Frames;
            public float Volume;
        }

        private struct DspParameters
        {
            public string Source;
            public float EquivalentRadius;
            public float RoomSize;
            public float Diffusion;
            public float Density;
            public float DecaySeconds;
            public float EarlyGainDb;
            public float TailGainDb;
            public float PredelayMs;
            public float LateDelayMs;
            public float ToneHz;
            public float HighFrequencyDb;
            public float AirPressure;
        }

        private struct DspTailInfo
        {
            public int DelayLineCount;
            public float RoomSize;
            public float Diffusion;
            public float Density;
            public float DecaySeconds;
            public float TailSeconds;
            public float CutoffHz;
            public float PreDelayMs;
            public float LateDelayMs;
            public float EquivalentRadius;
            public float AirPressure;
            public string Source;

            public string ToStatus()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "tail={0:0.0}s decay={1:0.0}s room={2:0.00} radius={9:0.0}m pressure={11:0.00} auto={10} diff={3:0.00} dens={4:0.00} lines={5} cutoff={6:0}Hz pre={7:0}ms late={8:0}ms",
                    TailSeconds,
                    DecaySeconds,
                    RoomSize,
                    Diffusion,
                    Density,
                    DelayLineCount,
                    CutoffHz,
                    PreDelayMs,
                    LateDelayMs,
                    EquivalentRadius,
                    Source ?? "?",
                    AirPressure);
            }
        }
    }
}
