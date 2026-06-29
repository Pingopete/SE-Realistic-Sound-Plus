using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Audio;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2PlayerFilterRuntime
    {
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan SampleLifetime = TimeSpan.FromSeconds(2);
        private const float FilterBypassMuffle = 0.10f;
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<IMySourceVoice, string> LastVoiceSignatures = new Dictionary<IMySourceVoice, string>();
        private static readonly Dictionary<IMySourceVoice, AuxFilterSmoothingState> FilterSmoothingStates = new Dictionary<IMySourceVoice, AuxFilterSmoothingState>();
        private static readonly Dictionary<IMySourceVoice, AuxVolumeSmoothingState> VolumeSmoothingStates = new Dictionary<IMySourceVoice, AuxVolumeSmoothingState>();
        private static readonly Dictionary<IMySourceVoice, float> BaseVoiceMultipliers = new Dictionary<IMySourceVoice, float>();
        private static readonly Dictionary<IMySourceVoice, float> BaseVoiceRawVolumes = new Dictionary<IMySourceVoice, float>();
        private static readonly Dictionary<IMySourceVoice, string> LastVoiceVolumeSignatures = new Dictionary<IMySourceVoice, string>();
        private static readonly Dictionary<Type, MemberInfo> RawVoiceVolumeMembers = new Dictionary<Type, MemberInfo>();
        private static readonly Dictionary<Type, HashSet<string>> RejectedRawVoiceVolumeMembers = new Dictionary<Type, HashSet<string>>();
        private static readonly HashSet<Type> RawVoiceVolumeMemberMisses = new HashSet<Type>();
        private static readonly HashSet<Type> RawVoiceVolumeMemberLogs = new HashSet<Type>();
        private static readonly HashSet<Type> RawVoiceVolumeWriteFailureLogs = new HashSet<Type>();
        private static readonly HashSet<Type> RawVoiceVolumeVerifyFailureLogs = new HashSet<Type>();
        private static readonly Dictionary<string, V2PlayerFilterSample> Samples = new Dictionary<string, V2PlayerFilterSample>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastUpdateUtc = DateTime.MinValue;
        private static DateTime _lastLogUtc = DateTime.MinValue;
        private static bool _wasEnabled;

        public static void Reset()
        {
            ClearTrackedVoices();
            LastVoiceSignatures.Clear();
            FilterSmoothingStates.Clear();
            VolumeSmoothingStates.Clear();
            BaseVoiceMultipliers.Clear();
            BaseVoiceRawVolumes.Clear();
            LastVoiceVolumeSignatures.Clear();
            RawVoiceVolumeMembers.Clear();
            RejectedRawVoiceVolumeMembers.Clear();
            RawVoiceVolumeMemberMisses.Clear();
            RawVoiceVolumeMemberLogs.Clear();
            RawVoiceVolumeWriteFailureLogs.Clear();
            RawVoiceVolumeVerifyFailureLogs.Clear();
            Samples.Clear();
            _lastUpdateUtc = DateTime.MinValue;
            _lastLogUtc = DateTime.MinValue;
            _wasEnabled = false;
        }

        public static void Update()
        {
            DateTime now = DateTime.UtcNow;
            if (UpdateInterval > TimeSpan.Zero && now - _lastUpdateUtc < UpdateInterval)
                return;

            _lastUpdateUtc = now;
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (MyAudio.Static == null)
                return;

            MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
            if (!settings.PlayerFilterEnabled)
            {
                if (_wasEnabled)
                    ClearTrackedVoices();
                _wasEnabled = false;
                PurgeSamples();
                return;
            }

            _wasEnabled = true;
            ProcessVoices(played.Sound, settings, now);
            if (ShouldProcessHudVoices(settings))
                ProcessVoices(played.Hud, settings, now);
            PurgeSamples();
            LogIfDue();
        }

        private static bool ShouldProcessHudVoices(RealisticSoundPlusSettings settings)
        {
            if (settings == null || !settings.PlayerFilterLocalEnabled)
                return false;

            if (MyAPIGateway.Session == null)
                return false;

            return V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample env) && env.Valid;
        }

        public static string FormatSummary()
        {
            PurgeSamples();
            int env = 0;
            int block = 0;
            int local = 0;
            float strongest = 0f;
            // Aggregate block-bus level: sum and peak of the per-voice effective output across all block voices. The
            // individual reverb/filter paths are each clamped, so an overload ("distortion builds, then cuts out with
            // lots of loud blocks") can only come from many voices SUMMING. blkSum >> 1 = level-driven (the sum is
            // clipping the shared bus); a high block COUNT with a modest blkSum points at voice/CPU starvation instead.
            float blockSum = 0f;
            float blockPeak = 0f;
            V2PlayerFilterSample strongestSample = default(V2PlayerFilterSample);

            foreach (V2PlayerFilterSample sample in Samples.Values)
            {
                switch (sample.Category)
                {
                    case "env": env++; break;
                    case "block":
                        block++;
                        float output = sample.EffectiveOutput;
                        if (output > 0f)
                            blockSum += output;
                        if (output > blockPeak)
                            blockPeak = output;
                        break;
                    case "local": local++; break;
                }

                if (sample.Score >= strongest)
                {
                    strongest = sample.Score;
                    strongestSample = sample;
                }
            }

            if (env + block + local == 0)
                return "No player-filtered voices observed yet.";

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            return string.Format(
                CultureInfo.InvariantCulture,
                "env={0} block={1} local={2} blkOut={16:0.00}sum/{17:0.00}pk auxAtm={3} envFloor={4:0.00} envCut={14:0}Hz smooth={15:0}ms volW={11:0.00}/{12:0.00}/{13:0.00} strongest={5}/{6} muff={7:0.00} freq={8:0}Hz q={9:0.00} gain={10:0.00}",
                env,
                block,
                local,
                settings.PlayerFilterAtmosphereOverrideEnabled ? settings.PlayerFilterAtmosphereOverride.ToString("0.00", CultureInfo.InvariantCulture) : "real",
                settings.PlayerFilterEnvironmentMinGain,
                strongestSample.Category ?? "?",
                Trim(strongestSample.CueName, 24),
                strongestSample.Muffle,
                strongestSample.Frequency,
                strongestSample.Q,
                strongestSample.VolumeGain,
                settings.PlayerFilterEnvironmentVolumeMuffleWeight,
                settings.PlayerFilterBlockVolumeMuffleWeight,
                settings.PlayerFilterLocalVolumeMuffleWeight,
                settings.PlayerFilterEnvironmentMuffledFrequency,
                settings.PlayerFilterSmoothingMs,
                blockSum,
                blockPeak);
        }

        public static string FormatSources(int maxLines)
        {
            PurgeSamples();
            if (Samples.Count == 0)
                return "cat  cue  muff  freq  q  atm  dist range gain raw base target out/req car";

            List<V2PlayerFilterSample> sorted = new List<V2PlayerFilterSample>(Samples.Values);
            sorted.Sort((left, right) => right.Score.CompareTo(left.Score));

            StringBuilder builder = new StringBuilder();
            builder.Append("cat cue                 muff  freq   q   atm  dist range        gain raw base target out/req car");
            int count = Math.Min(maxLines, sorted.Count);
            for (int i = 0; i < count; i++)
            {
                V2PlayerFilterSample sample = sorted[i];
                builder.AppendLine();
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0,-5}{1,-20} {2:0.00} {3,5:0} {4:0.00} {5:0.00} {6,4:0} {15,-11} {7:0.00} {8:0.00}/{9:0.00} {10:0.00} {11:0.00} {12:0.00}/{13:0.00} {14}",
                    sample.Category ?? "?",
                    Trim(sample.CueName, 19),
                    sample.Muffle,
                    sample.Frequency,
                    sample.Q,
                    sample.LocalAtmosphere,
                    sample.Distance,
                    sample.VolumeGain,
                    sample.VoiceVolume,
                    sample.VoiceMultiplier,
                    sample.BaseVoiceMultiplier,
                    sample.TargetMultiplier,
                    sample.EffectiveOutput,
                    sample.RequestedOutput,
                    sample.EnvironmentCarrierForced ? "F" : sample.EnvironmentCarrierUnavailable ? "!" : "-",
                    FormatRange(sample));
            }

            return builder.ToString();
        }

        public static string FormatEnvironmentLiveReadout()
        {
            if (!V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample env))
                return "Coverage: --  Muffled: --  Volume: --";

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float localAtmosphere = GetEffectiveAtmosphere(env.LocalAtmosphere, settings);
            float pressureMuffle = 1f - Clamp01(localAtmosphere);
            float muffle = Combine(env.FinalMuffling, pressureMuffle);
            float volume = CalculateEnvironmentGain(env, localAtmosphere, muffle, settings, false);
            float coverage = Clamp01(1f - env.OpenFraction);

            return string.Format(
                CultureInfo.InvariantCulture,
                "Coverage: {0:0}%    Muffled: {1:0}%    Volume: {2:0}%",
                coverage * 100f,
                muffle * 100f,
                volume * 100f);
        }

        private static string FormatRange(V2PlayerFilterSample sample)
        {
            if (!string.Equals(sample.Category, "block", StringComparison.OrdinalIgnoreCase) || sample.EffectiveRange <= 0.5f)
                return "-";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0}/{1:0}{2}",
                sample.VanillaMaxDistance,
                sample.EffectiveRange,
                sample.CustomRangeApplied ? "*" : "");
        }

        private static void ProcessVoices(List<IMySourceVoice> voices, RealisticSoundPlusSettings settings, DateTime now)
        {
            if (voices == null || voices.Count == 0)
                return;

            for (int i = 0; i < voices.Count; i++)
            {
                IMySourceVoice voice = voices[i];
                if (voice == null || !voice.IsValid || !voice.IsPlaying)
                    continue;
                if (V2ReverbDiagnosticPing.IsOwnedWetVoice(voice))
                    continue;

                string cueName = voice.CueEnum.ToString();
                float baseMultiplier = BaseVoiceMultipliers.TryGetValue(voice, out float trackedBase)
                    ? trackedBase
                    : voice.VolumeMultiplier;
                float score = Math.Max(0f, voice.Volume * baseMultiplier);
                bool possibleEnvironment = settings.PlayerFilterEnvironmentEnabled && V2AuxCueClassifier.IsEnvironmentCue(cueName);
                if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty" || (score <= 0.001f && !possibleEnvironment))
                    continue;

                if (!TryClassifyAndCalculate(voice, cueName, score, settings, out V2PlayerFilterSample sample))
                {
                    ClearVoiceControlsIfTracked(voice, settings);
                    continue;
                }

                sample.VoiceVolume = voice.Volume;
                sample.VoiceMultiplier = voice.VolumeMultiplier;
                sample.UpdatedUtc = now;
                // Feed the block per-voice muffle AND the muffle-driven volume gain to the per-voice reverb (block-
                // source feature) so its dry/wet split rides the SAME air-path muffle envelope as the biquad, and its
                // wet can be compensated for the occlusion volume drop (distance gain is intentionally excluded).
                if (string.Equals(sample.Category, "block", StringComparison.OrdinalIgnoreCase))
                    V2GlobalReverbRuntime.ReportBlockMuffle(voice, sample.Muffle, CalculateMuffleVolumeGain(sample.Muffle, settings.PlayerFilterBlockVolumeMuffleWeight));
                if (sample.Muffle > FilterBypassMuffle)
                    sample.Applied = ApplyFilterIfChanged(voice, settings.Filter2Type, sample.Frequency, sample.Q, sample.Category);
                else if (LastVoiceSignatures.ContainsKey(voice))
                    // Below the bypass threshold, glide the cutoff back toward clear through the SAME smoothing
                    // path instead of hard-snapping it wide open (which also deleted the smoothing state).
                    // Hard-clearing here is what turned every brief occlusion dip into an instant audible jump;
                    // gliding keeps muffle/clear transitions continuous. Genuine release happens when the voice
                    // stops being a block candidate (ClearVoiceControlsIfTracked above).
                    sample.Applied = ApplyFilterIfChanged(voice, settings.Filter2Type, RspDynamicAudioFilters.MaxFilterFrequency, settings.Filter2Q, sample.Category);
                else
                    ClearVoiceFilterIfTracked(voice, settings);

                if (ApplyVolumeIfNeeded(voice, ref sample))
                    sample.Applied = true;

                Samples[BuildKey(sample)] = sample;
            }
        }

        private static bool TryClassifyAndCalculate(IMySourceVoice voice, string cueName, float score, RealisticSoundPlusSettings settings, out V2PlayerFilterSample sample)
        {
            sample = default(V2PlayerFilterSample);
            if (V2AuxCueClassifier.IsNonWorldCue(cueName) && !V2AuxCueClassifier.IsImmersiveUiCue(cueName))
                return false;

            if (V2AuxCueClassifier.IsEngineCue(cueName))
                return false;

            bool controllableActionCue = V2AuxCueClassifier.IsControllableActionCue(cueName);
            if (settings.PlayerFilterBlockEnabled
                && controllableActionCue
                && TryCalculatePhysicalBlockEmitter(voice, cueName, score, settings, out sample))
                return true;

            if (settings.PlayerFilterLocalEnabled && V2AuxCueClassifier.IsPlayerLocalCue(cueName))
                return TryCalculatePlayerLocal(cueName, score, settings, out sample);

            if (settings.PlayerFilterBlockEnabled && TryCalculatePhysicalBlockEmitter(voice, cueName, score, settings, out sample))
                return true;

            bool blockCueNeedsResolvedSource = V2AuxCueClassifier.IsKnownBlockCueButNeedsPhysicalSource(cueName);
            if (blockCueNeedsResolvedSource)
                return false;

            if (settings.PlayerFilterLocalEnabled && controllableActionCue)
                return TryCalculatePlayerLocal(cueName, score, settings, out sample);

            if (settings.PlayerFilterEnvironmentEnabled && V2AuxCueClassifier.IsEnvironmentCue(cueName))
                return TryCalculateEnvironment(cueName, score, settings, out sample);

            return false;
        }

        private static bool TryCalculatePhysicalBlockEmitter(IMySourceVoice voice, string cueName, float score, RealisticSoundPlusSettings settings, out V2PlayerFilterSample sample)
        {
            sample = default(V2PlayerFilterSample);
            if (!RspDynamicAudioFilters.TryResolveEmitter(voice, out MyEntity3DSoundEmitter emitter) || !IsReliablePhysicalEmitter(emitter))
                return false;

            if (!TryCalculateBlock(cueName, score, settings, emitter, out sample))
                return false;

            V2BlockSoundSourceResolver.RecordPhysicalEmitterBlock();
            return true;
        }

        private static bool IsReliablePhysicalEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            Vector3D source = emitter.SourcePosition;
            return source != Vector3D.Zero
                && !double.IsNaN(source.X)
                && !double.IsNaN(source.Y)
                && !double.IsNaN(source.Z)
                && !double.IsInfinity(source.X)
                && !double.IsInfinity(source.Y)
                && !double.IsInfinity(source.Z);
        }

        private static bool TryCalculateEnvironment(string cueName, float score, RealisticSoundPlusSettings settings, out V2PlayerFilterSample sample)
        {
            sample = default(V2PlayerFilterSample);
            if (!V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample env))
                return false;
            if (!ShouldFilterEnvironmentVoice(score, env))
                return false;

            float localAtmosphere = GetEffectiveAtmosphere(env.LocalAtmosphere, settings);
            float pressureMuffle = 1f - Clamp01(localAtmosphere);
            float muffle = Combine(env.FinalMuffling, pressureMuffle);
            bool vanillaSuppressed = score <= 0.001f;
            if (muffle <= FilterBypassMuffle)
            {
                if (!vanillaSuppressed || settings.PlayerFilterEnvironmentMinGain <= 0f)
                    return false;

                if (vanillaSuppressed && settings.PlayerFilterEnvironmentMinGain > 0f)
                    muffle = 1f;
            }

            sample = new V2PlayerFilterSample
            {
                Category = "env",
                CueName = cueName,
                Score = score,
                Distance = 0f,
                Muffle = muffle,
                Frequency = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterEnvironmentMuffledFrequency, muffle),
                Q = settings.Filter2Q,
                LocalAtmosphere = localAtmosphere,
                OpenFraction = env.OpenFraction,
                VolumeGain = CalculateEnvironmentGain(env, localAtmosphere, muffle, settings, vanillaSuppressed)
            };
            return true;
        }

        private static bool ShouldFilterEnvironmentVoice(float score, V2PlayerEnvironmentSample env)
        {
            if (!env.Valid)
                return false;

            return env.PlanetEnvironmentAvailable
                || score > 0.001f
                || env.WindAudibility > 0.001f
                || env.WindExposure > 0.001f;
        }

        private static bool TryCalculateBlock(string cueName, float score, RealisticSoundPlusSettings settings, MyEntity3DSoundEmitter emitter, out V2PlayerFilterSample sample)
        {
            sample = default(V2PlayerFilterSample);
            if (!V2AuxSourceOcclusionTelemetry.TryCalculate("S", cueName, score, emitter, out V2AuxSourceOcclusionSample aux))
                return false;

            float localAtmosphere = GetEffectiveAtmosphere(aux.LocalAtmosphere, settings);
            float pressureMuffle = 1f - Clamp01(localAtmosphere);
            float muffle = Combine(aux.FinalMuffling, pressureMuffle);
            sample = new V2PlayerFilterSample
            {
                Category = "block",
                CueName = cueName,
                Score = score,
                SourcePosition = aux.SourcePosition,
                EntityWorldPosition = TryGetEntityWorldPosition(emitter),
                SourceEntityId = emitter.Entity?.EntityId ?? 0L,
                Distance = aux.Distance,
                Muffle = muffle,
                Frequency = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterBlockMuffledFrequency, muffle),
                Q = settings.Filter2Q,
                LocalAtmosphere = localAtmosphere,
                OpenFraction = aux.OpenFraction,
                VanillaMaxDistance = aux.VanillaMaxDistance,
                EffectiveRange = aux.EffectiveRange,
                RangeScale = aux.RangeScale,
                CustomRangeApplied = aux.CustomRangeApplied,
                VolumeGain = aux.EstimatedGain * CalculateMuffleVolumeGain(muffle, settings.PlayerFilterBlockVolumeMuffleWeight)
            };
            return true;
        }

        private static bool TryCalculatePlayerLocal(string cueName, float score, RealisticSoundPlusSettings settings, out V2PlayerFilterSample sample)
        {
            sample = default(V2PlayerFilterSample);
            float localAtmosphere = 1f;
            if (V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample env))
                localAtmosphere = env.LocalAtmosphere;

            localAtmosphere = GetEffectiveAtmosphere(localAtmosphere, settings);
            float muffle = Clamp01(1f - localAtmosphere);
            if (muffle <= FilterBypassMuffle)
                return false;

            sample = new V2PlayerFilterSample
            {
                Category = "local",
                CueName = cueName,
                Score = score,
                Distance = 0f,
                Muffle = muffle,
                Frequency = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterMuffledFrequency, muffle),
                Q = settings.Filter2Q,
                LocalAtmosphere = localAtmosphere,
                OpenFraction = 1f,
                VolumeGain = CalculateMuffleVolumeGain(muffle, settings.PlayerFilterLocalVolumeMuffleWeight)
            };
            return true;
        }

        private static bool ApplyVolumeIfNeeded(IMySourceVoice voice, ref V2PlayerFilterSample sample)
        {
            if (voice == null || !voice.IsValid)
                return false;

            bool controlsVolume = string.Equals(sample.Category, "block", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sample.Category, "env", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sample.Category, "local", StringComparison.OrdinalIgnoreCase);
            if (!controlsVolume)
            {
                RestoreVoiceVolumeIfTracked(voice);
                return false;
            }

            bool environment = string.Equals(sample.Category, "env", StringComparison.OrdinalIgnoreCase);
            float gainLimit = string.Equals(sample.Category, "block", StringComparison.OrdinalIgnoreCase) ? 6f : 1f;
            float gain = Clamp(sample.VolumeGain <= 0f ? 0f : sample.VolumeGain, 0f, gainLimit);
            if (!BaseVoiceMultipliers.TryGetValue(voice, out float baseMultiplier))
            {
                baseMultiplier = voice.VolumeMultiplier;
                BaseVoiceMultipliers[voice] = baseMultiplier;
            }

            float controlBase = baseMultiplier;
            if (environment && controlBase <= 0.001f)
                controlBase = 1f;

            float target = controlBase * gain;
            float observedVolume = voice.Volume;
            if (environment && observedVolume <= 0.001f && target > 0.001f)
            {
                if (TryForceEnvironmentCarrierVolume(voice, 1f, out observedVolume))
                    sample.EnvironmentCarrierForced = true;
                else
                    sample.EnvironmentCarrierUnavailable = true;
            }

            sample.BaseVoiceMultiplier = baseMultiplier;
            sample.TargetMultiplier = target;
            sample.VoiceVolume = observedVolume;
            sample.RequestedOutput = observedVolume * target;
            sample.EffectiveOutput = observedVolume * voice.VolumeMultiplier;
            float smoothedTarget = SmoothVolumeMultiplier(voice, target, sample.Category);
            string signature = string.Format(CultureInfo.InvariantCulture, "{0}:{1:0.000}:{2:0.000}", sample.Category ?? "?", smoothedTarget, controlBase);
            if (LastVoiceVolumeSignatures.TryGetValue(voice, out string previous)
                && string.Equals(previous, signature, StringComparison.Ordinal)
                && Math.Abs(voice.VolumeMultiplier - smoothedTarget) <= 0.002f)
            {
                sample.EffectiveOutput = observedVolume * voice.VolumeMultiplier;
                return true;
            }

            voice.VolumeMultiplier = smoothedTarget;
            sample.VoiceMultiplier = voice.VolumeMultiplier;
            sample.EffectiveOutput = observedVolume * voice.VolumeMultiplier;
            LastVoiceVolumeSignatures[voice] = signature;
            return true;
        }

        private static void RestoreVoiceVolumeIfTracked(IMySourceVoice voice)
        {
            if (voice == null)
                return;

            FilterSmoothingStates.Remove(voice);
            VolumeSmoothingStates.Remove(voice);

            if (BaseVoiceMultipliers.TryGetValue(voice, out float baseMultiplier) && voice.IsValid)
                voice.VolumeMultiplier = baseMultiplier;

            if (BaseVoiceRawVolumes.TryGetValue(voice, out float rawVolume) && voice.IsValid && rawVolume > 0.001f)
                TrySetRawVoiceVolume(voice, rawVolume, false);

            BaseVoiceMultipliers.Remove(voice);
            BaseVoiceRawVolumes.Remove(voice);
            LastVoiceVolumeSignatures.Remove(voice);
        }

        private static bool TryForceEnvironmentCarrierVolume(IMySourceVoice voice, float targetVolume, out float observedVolume)
        {
            observedVolume = voice?.Volume ?? 0f;
            if (voice == null || !voice.IsValid)
                return false;

            if (observedVolume > 0.001f)
                return true;

            if (!BaseVoiceRawVolumes.ContainsKey(voice))
                BaseVoiceRawVolumes[voice] = observedVolume;

            if (!TrySetRawVoiceVolume(voice, targetVolume, true))
            {
                BaseVoiceRawVolumes.Remove(voice);
                return false;
            }

            observedVolume = voice.Volume;
            if (observedVolume > 0.001f)
                return true;

            Type type = voice.GetType();
            RawVoiceVolumeMembers.TryGetValue(type, out MemberInfo member);
            RejectRawVoiceVolumeMember(type, member, "readback-zero");
            if (RawVoiceVolumeVerifyFailureLogs.Add(type))
            {
                V2DebugLog.WriteEvent(
                    "env-carrier-verify-failed",
                    type.FullName + " target=" + targetVolume.ToString("0.000", CultureInfo.InvariantCulture)
                    + " readback=" + observedVolume.ToString("0.000", CultureInfo.InvariantCulture)
                    + " candidates=" + DescribeVolumeCandidates(type));
            }

            BaseVoiceRawVolumes.Remove(voice);
            return false;
        }

        private static bool TrySetRawVoiceVolume(IMySourceVoice voice, float value, bool logDiagnostics)
        {
            if (voice == null)
                return false;

            Type type = voice.GetType();
            MemberInfo member = ResolveRawVoiceVolumeMember(type, logDiagnostics);
            if (member == null)
                return false;

            try
            {
                if (member is PropertyInfo property)
                    property.SetValue(voice, ConvertRawVolumeValue(value, property.PropertyType), null);
                else if (member is FieldInfo field)
                    field.SetValue(voice, ConvertRawVolumeValue(value, field.FieldType));
                else
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                RejectRawVoiceVolumeMember(type, member, "write-failed");
                if (logDiagnostics && RawVoiceVolumeWriteFailureLogs.Add(type))
                    V2DebugLog.WriteEvent("env-carrier-write-failed", type.FullName + "." + member.Name + " " + ex.GetType().Name + ": " + ex.Message);

                return false;
            }
        }

        private static object ConvertRawVolumeValue(float value, Type targetType)
        {
            if (targetType == typeof(double))
                return (double)value;

            return value;
        }

        private static MemberInfo ResolveRawVoiceVolumeMember(Type type, bool logDiagnostics)
        {
            if (type == null)
                return null;

            if (RawVoiceVolumeMembers.TryGetValue(type, out MemberInfo cached))
                return cached;

            if (RawVoiceVolumeMemberMisses.Contains(type))
                return null;

            List<MemberInfo> candidates = new List<MemberInfo>();
            AddCandidate(candidates, FindWritableVolumeProperty(type));
            AddCandidate(candidates, FindVolumeField(type, "Volume"));
            AddCandidate(candidates, FindVolumeField(type, "m_volume"));
            AddCandidate(candidates, FindVolumeField(type, "volume"));
            AddCandidate(candidates, FindVolumeField(type, "<Volume>k__BackingField"));
            AddCandidate(candidates, FindFallbackVolumeField(type));

            MemberInfo member = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsRejectedRawVoiceVolumeMember(type, candidates[i]))
                    continue;

                member = candidates[i];
                break;
            }

            if (member == null)
            {
                RawVoiceVolumeMemberMisses.Add(type);
                if (logDiagnostics)
                    V2DebugLog.WriteEvent("env-carrier-unavailable", "No writable raw Volume member on " + type.FullName + " candidates=" + DescribeVolumeCandidates(type));
                return null;
            }

            RawVoiceVolumeMembers[type] = member;
            if (logDiagnostics && RawVoiceVolumeMemberLogs.Add(type))
                V2DebugLog.WriteEvent("env-carrier-member", type.FullName + "." + member.Name);

            return member;
        }

        private static void AddCandidate(List<MemberInfo> candidates, MemberInfo member)
        {
            if (member == null)
                return;

            string key = BuildMemberKey(member);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(BuildMemberKey(candidates[i]), key, StringComparison.Ordinal))
                    return;
            }

            candidates.Add(member);
        }

        private static bool IsRejectedRawVoiceVolumeMember(Type type, MemberInfo member)
        {
            if (type == null || member == null)
                return false;

            return RejectedRawVoiceVolumeMembers.TryGetValue(type, out HashSet<string> rejected)
                && rejected.Contains(BuildMemberKey(member));
        }

        private static void RejectRawVoiceVolumeMember(Type type, MemberInfo member, string reason)
        {
            if (type == null || member == null)
                return;

            if (!RejectedRawVoiceVolumeMembers.TryGetValue(type, out HashSet<string> rejected))
            {
                rejected = new HashSet<string>(StringComparer.Ordinal);
                RejectedRawVoiceVolumeMembers[type] = rejected;
            }

            string key = BuildMemberKey(member);
            if (rejected.Add(key))
                V2DebugLog.WriteEvent("env-carrier-member-rejected", type.FullName + "." + member.Name + " reason=" + reason);

            RawVoiceVolumeMembers.Remove(type);
            RawVoiceVolumeMemberMisses.Remove(type);
        }

        private static string BuildMemberKey(MemberInfo member)
        {
            return member == null ? "?" : member.MemberType + ":" + member.Name;
        }

        private static PropertyInfo FindWritableVolumeProperty(Type type)
        {
            PropertyInfo property = type.GetProperty("Volume", InstanceMembers);
            if (property == null || property.GetSetMethod(true) == null || !IsSupportedRawVolumeType(property.PropertyType))
                return null;

            return property;
        }

        private static FieldInfo FindVolumeField(Type type, string name)
        {
            FieldInfo field = type.GetField(name, InstanceMembers);
            if (field == null || !IsSupportedRawVolumeType(field.FieldType))
                return null;

            return field;
        }

        private static FieldInfo FindFallbackVolumeField(Type type)
        {
            FieldInfo[] fields = type.GetFields(InstanceMembers);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                string name = field.Name ?? string.Empty;
                if (!IsSupportedRawVolumeType(field.FieldType))
                    continue;

                if (name.IndexOf("volume", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (name.IndexOf("multiplier", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("curve", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                return field;
            }

            return null;
        }

        private static bool IsSupportedRawVolumeType(Type type)
        {
            return type == typeof(float) || type == typeof(double);
        }

        private static string DescribeVolumeCandidates(Type type)
        {
            StringBuilder builder = new StringBuilder();
            FieldInfo[] fields = type.GetFields(InstanceMembers);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.Name.IndexOf("volume", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (builder.Length > 0)
                    builder.Append(',');

                builder.Append("field:");
                builder.Append(field.Name);
                builder.Append('/');
                builder.Append(field.FieldType.Name);
            }

            PropertyInfo[] properties = type.GetProperties(InstanceMembers);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (property.Name.IndexOf("volume", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (builder.Length > 0)
                    builder.Append(',');

                builder.Append("prop:");
                builder.Append(property.Name);
                builder.Append('/');
                builder.Append(property.PropertyType.Name);
                builder.Append(property.GetSetMethod(true) != null ? "/set" : "/get");
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        // Pre-apply the per-voice block/aux muffle to a freshly-bound voice BEFORE its first audio frame. ProcessVoices
        // runs at most every 50ms and only sees a voice once it IsPlaying, so a cold block voice (a door opening, a
        // cockpit cue) plays UNFILTERED for up to a frame or two - a bright full-volume burst that is the "first time
        // this sound plays" pop. Running the SAME classify+apply once at bind time closes that window with the SAME
        // occlusion-driven target (no per-block special-casing). Only applies when the target is actually muffled; an
        // unmuffled voice is correct unfiltered, so it is left alone. The 50ms ProcessVoices pass still refines it.
        public static bool TryPreFilterNewVoice(IMySourceVoice voice, RealisticSoundPlusSettings settings)
        {
            if (voice == null || settings == null || !settings.PlayerFilterEnabled || !voice.IsValid)
                return false;

            string cueName = voice.CueEnum.ToString();
            if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty")
                return false;

            // Volume isn't established at bind, so pass a nominal score: classification only needs the cue + physical
            // source to derive the cutoff target. We apply ONLY the filter here, never the volume.
            if (!TryClassifyAndCalculate(voice, cueName, 1f, settings, out V2PlayerFilterSample sample))
                return false;

            if (sample.Muffle <= FilterBypassMuffle)
                return false;

            return ApplyFilterIfChanged(voice, settings.Filter2Type, sample.Frequency, sample.Q, sample.Category);
        }

        private static bool ApplyFilterIfChanged(IMySourceVoice voice, string filterType, float frequency, float q, string category)
        {
            if (voice == null || !voice.IsValid)
                return false;

            SmoothFilterParameters(voice, filterType, frequency, q, category, out float smoothedFrequency, out float smoothedQ);

            string signature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}:{2:0}:{3:0.00}",
                category ?? "?",
                filterType ?? "LowPass",
                RspDynamicAudioFilters.SanitizeFrequency(smoothedFrequency),
                RspDynamicAudioFilters.SanitizeQ(smoothedQ));

            if (LastVoiceSignatures.TryGetValue(voice, out string previous) && string.Equals(previous, signature, StringComparison.Ordinal))
                return true;

            bool applied = RspDynamicAudioFilters.TryApplyLiveFilterParameters(voice, filterType, smoothedFrequency, smoothedQ);
            if (applied)
                LastVoiceSignatures[voice] = signature;

            return applied;
        }

        private static void SmoothFilterParameters(IMySourceVoice voice, string filterType, float targetFrequency, float targetQ, string category, out float smoothedFrequency, out float smoothedQ)
        {
            DateTime now = DateTime.UtcNow;
            targetFrequency = RspDynamicAudioFilters.SanitizeFrequency(targetFrequency);
            targetQ = RspDynamicAudioFilters.SanitizeQ(targetQ);
            string normalizedType = filterType ?? "LowPass";
            string normalizedCategory = category ?? "?";

            if (!FilterSmoothingStates.TryGetValue(voice, out AuxFilterSmoothingState state)
                || !string.Equals(state.FilterType, normalizedType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase)
                || now - state.UpdatedUtc > SampleLifetime)
            {
                state = new AuxFilterSmoothingState
                {
                    FilterType = normalizedType,
                    Category = normalizedCategory,
                    Frequency = targetFrequency,
                    Q = targetQ,
                    UpdatedUtc = now
                };
                FilterSmoothingStates[voice] = state;
                smoothedFrequency = targetFrequency;
                smoothedQ = targetQ;
                return;
            }

            double elapsedSeconds = Math.Max(0.0, (now - state.UpdatedUtc).TotalSeconds);
            float smoothingMs = SettingsManager.Current?.PlayerFilterSmoothingMs ?? 1000f;
            float alpha = smoothingMs <= 0.001f
                ? 1f
                : (float)Math.Max(0.0, Math.Min(1.0, elapsedSeconds / (smoothingMs / 1000.0)));

            float currentLog = (float)Math.Log(Math.Max(1f, state.Frequency));
            float targetLog = (float)Math.Log(Math.Max(1f, targetFrequency));
            smoothedFrequency = RspDynamicAudioFilters.SanitizeFrequency((float)Math.Exp(currentLog + (targetLog - currentLog) * alpha));
            smoothedQ = RspDynamicAudioFilters.SanitizeQ(state.Q + (targetQ - state.Q) * alpha);

            state.Frequency = smoothedFrequency;
            state.Q = smoothedQ;
            state.UpdatedUtc = now;
            FilterSmoothingStates[voice] = state;
        }

        private static void ClearVoiceFilterIfTracked(IMySourceVoice voice, RealisticSoundPlusSettings settings)
        {
            if (voice == null)
                return;

            FilterSmoothingStates.Remove(voice);
            if (!LastVoiceSignatures.ContainsKey(voice))
                return;

            if (voice.IsValid)
                RspDynamicAudioFilters.TryApplyLiveFilterParameters(voice, "LowPass", RspDynamicAudioFilters.MaxFilterFrequency, settings.Filter2Q);

            LastVoiceSignatures.Remove(voice);
        }

        private static void ClearVoiceControlsIfTracked(IMySourceVoice voice, RealisticSoundPlusSettings settings)
        {
            ClearVoiceFilterIfTracked(voice, settings);
            RestoreVoiceVolumeIfTracked(voice);
        }

        private static void ClearTrackedVoices()
        {
            HashSet<IMySourceVoice> voices = new HashSet<IMySourceVoice>(LastVoiceSignatures.Keys);
            foreach (IMySourceVoice voice in BaseVoiceMultipliers.Keys)
                voices.Add(voice);
            foreach (IMySourceVoice voice in FilterSmoothingStates.Keys)
                voices.Add(voice);
            foreach (IMySourceVoice voice in VolumeSmoothingStates.Keys)
                voices.Add(voice);

            List<IMySourceVoice> snapshot = new List<IMySourceVoice>(voices);
            for (int i = 0; i < snapshot.Count; i++)
            {
                IMySourceVoice voice = snapshot[i];
                if (voice != null && voice.IsValid)
                {
                    RspDynamicAudioFilters.TryApplyLiveFilterParameters(voice, "LowPass", RspDynamicAudioFilters.MaxFilterFrequency, SettingsManager.Current.Filter2Q);
                    RestoreVoiceVolumeIfTracked(voice);
                }
            }

            LastVoiceSignatures.Clear();
            BaseVoiceMultipliers.Clear();
            BaseVoiceRawVolumes.Clear();
            LastVoiceVolumeSignatures.Clear();
            FilterSmoothingStates.Clear();
            VolumeSmoothingStates.Clear();
            Samples.Clear();
        }

        private static float SmoothVolumeMultiplier(IMySourceVoice voice, float target, string category)
        {
            DateTime now = DateTime.UtcNow;
            target = Clamp(target, 0f, 6f);
            string normalizedCategory = category ?? "?";

            if (!VolumeSmoothingStates.TryGetValue(voice, out AuxVolumeSmoothingState state)
                || !string.Equals(state.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase)
                || now - state.UpdatedUtc > SampleLifetime)
            {
                state = new AuxVolumeSmoothingState
                {
                    Category = normalizedCategory,
                    Multiplier = voice != null && voice.IsValid ? Clamp(voice.VolumeMultiplier, 0f, 6f) : target,
                    UpdatedUtc = now
                };
                VolumeSmoothingStates[voice] = state;
            }

            double elapsedSeconds = Math.Max(0.0, (now - state.UpdatedUtc).TotalSeconds);
            float smoothingMs = SettingsManager.Current?.PlayerFilterSmoothingMs ?? 1000f;
            float alpha = smoothingMs <= 0.001f
                ? 1f
                : (float)Math.Max(0.0, Math.Min(1.0, elapsedSeconds / (smoothingMs / 1000.0)));
            float smoothed = state.Multiplier + (target - state.Multiplier) * alpha;

            state.Multiplier = smoothed;
            state.UpdatedUtc = now;
            VolumeSmoothingStates[voice] = state;
            return smoothed;
        }

        private static void PurgeSamples()
        {
            DateTime now = DateTime.UtcNow;
            List<string> remove = null;
            foreach (KeyValuePair<string, V2PlayerFilterSample> pair in Samples)
            {
                if (now - pair.Value.UpdatedUtc <= SampleLifetime)
                    continue;

                if (remove == null)
                    remove = new List<string>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                Samples.Remove(remove[i]);
        }

        private static void LogIfDue()
        {
            if (!SettingsManager.Current.V2DebugLogEnabled)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastLogUtc = now;
            V2DebugLog.WriteEvent("player-filter", FormatSummary()
                + " | nearblk: " + FormatNearestBlocks(6)
                + " | " + FormatSources(8).Replace(Environment.NewLine, "; "));
        }

        private static string BuildKey(V2PlayerFilterSample sample)
        {
            string key = (sample.Category ?? "?") + ":" + (sample.CueName ?? "?");

            // Block emitters are physical sources, so the same block cue can play from many positions at
            // once (e.g. the player's nearby base and a second base 60 km away). Keying by cue name alone
            // made them collide, and the last writer won — which is why the readout only ever showed the
            // distant base. Discriminate by the owning block's entity id (stable; a source 60 km out
            // jitters position by metres each frame, so a position key churns into hundreds of rows).
            // Fall back to a coarse position only when no entity id is available. Env/local stay
            // cue-keyed (they are listener-global).
            if (string.Equals(sample.Category, "block", StringComparison.OrdinalIgnoreCase))
            {
                if (sample.SourceEntityId != 0L)
                    key += "@e" + sample.SourceEntityId.ToString(CultureInfo.InvariantCulture);
                else if (sample.SourcePosition != Vector3D.Zero)
                    key += string.Format(
                        CultureInfo.InvariantCulture,
                        "@p{0:0}:{1:0}:{2:0}",
                        Math.Round(sample.SourcePosition.X / 4.0) * 4.0,
                        Math.Round(sample.SourcePosition.Y / 4.0) * 4.0,
                        Math.Round(sample.SourcePosition.Z / 4.0) * 4.0);
            }

            return key;
        }

        // Block sources sorted by the emitter's TRUE entity distance (not the source distance occlusion
        // uses, and not voice volume). This surfaces a block physically next to the listener even when
        // its SourcePosition is stale/far. Each row shows entDist (truth) vs srcDist (what occlusion
        // sees) vs srcVsEnt (how far SourcePosition has drifted from the real block). If srcVsEnt is
        // large while entDist is small, the source position is stale — the actual defect.
        public static string FormatNearestBlocks(int maxLines)
        {
            PurgeSamples();
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            List<V2PlayerFilterSample> blocks = new List<V2PlayerFilterSample>();
            foreach (V2PlayerFilterSample sample in Samples.Values)
            {
                if (string.Equals(sample.Category, "block", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(sample);
            }

            if (blocks.Count == 0)
                return "none";

            blocks.Sort((left, right) => EntityDistance(left, listener).CompareTo(EntityDistance(right, listener)));
            StringBuilder builder = new StringBuilder();
            int count = Math.Min(maxLines, blocks.Count);
            for (int i = 0; i < count; i++)
            {
                V2PlayerFilterSample sample = blocks[i];
                float divergence = (sample.SourcePosition != Vector3D.Zero && sample.EntityWorldPosition != Vector3D.Zero)
                    ? (float)Vector3D.Distance(sample.SourcePosition, sample.EntityWorldPosition)
                    : -1f;
                if (i > 0)
                    builder.Append("; ");

                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0} entDist={1:0}m srcDist={2:0}m srcVsEnt={3:0}m muff={4:0.00} open={5:0.00}",
                    Trim(sample.CueName, 20),
                    EntityDistance(sample, listener),
                    sample.Distance,
                    divergence,
                    sample.Muffle,
                    sample.OpenFraction);
            }

            return string.Format(CultureInfo.InvariantCulture, "count={0} {1}", blocks.Count, builder);
        }

        private static float EntityDistance(V2PlayerFilterSample sample, Vector3D listener)
        {
            if (sample.EntityWorldPosition == Vector3D.Zero || listener == Vector3D.Zero)
                return sample.Distance;

            return (float)Vector3D.Distance(sample.EntityWorldPosition, listener);
        }

        private static Vector3D TryGetEntityWorldPosition(MyEntity3DSoundEmitter emitter)
        {
            try
            {
                var entity = emitter?.Entity;
                if (entity == null)
                    return Vector3D.Zero;

                if (entity.PositionComp != null)
                    return entity.PositionComp.GetPosition();

                return entity.WorldMatrix.Translation;
            }
            catch
            {
                return Vector3D.Zero;
            }
        }

        private static float GetEffectiveAtmosphere(float realAtmosphere, RealisticSoundPlusSettings settings)
        {
            // Manual override is used verbatim (it's a direct test value). The real atmosphere already arrives blended
            // (physical density + synthetic altitude ramp) from GetAtmosphericPressure, so the gradual entry/exit
            // easing is baked in - just clamp it here.
            if (settings != null && settings.PlayerFilterAtmosphereOverrideEnabled)
                return Clamp01(settings.PlayerFilterAtmosphereOverride);

            return Clamp01(realAtmosphere);
        }

        private static float CalculateEnvironmentGain(V2PlayerEnvironmentSample env, float localAtmosphere, float muffle, RealisticSoundPlusSettings settings, bool vanillaSuppressed)
        {
            float pressure = Clamp01(localAtmosphere);
            float naturalAudibility = Clamp01(pressure * CalculateMuffleVolumeGain(muffle, settings?.PlayerFilterEnvironmentVolumeMuffleWeight ?? 1f));
            float floor = Clamp01((settings?.PlayerFilterEnvironmentMinGain ?? 0f) * pressure);
            return Math.Max(naturalAudibility, floor);
        }

        private static float CalculateMuffleVolumeGain(float muffle, float weight)
        {
            if (weight <= 0f)
                return 1f;

            float clear = 1f - Clamp01(muffle);
            return Clamp01((float)Math.Pow(clear, Math.Max(0.01f, weight)));
        }

        private static float Combine(float first, float second)
        {
            first = Clamp01(first);
            second = Clamp01(second);
            return Clamp01(first + (1f - first) * second);
        }

        private static float BlendCutoff(float clearCutoff, float muffledCutoff, float amount)
        {
            double logClear = Math.Log(Math.Max(1f, clearCutoff));
            double logMuffled = Math.Log(Math.Max(1f, muffledCutoff));
            return (float)Math.Exp(logClear + (logMuffled - logClear) * Clamp01(amount));
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;

            return value > max ? max : value;
        }

        private struct AuxFilterSmoothingState
        {
            public string FilterType;
            public string Category;
            public float Frequency;
            public float Q;
            public DateTime UpdatedUtc;
        }

        private struct AuxVolumeSmoothingState
        {
            public string Category;
            public float Multiplier;
            public DateTime UpdatedUtc;
        }

    }
}
