using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2BlockRangeScaler
    {
        private static readonly TimeSpan TrackedLifetime = TimeSpan.FromSeconds(8);
        private static readonly Dictionary<MyEntity3DSoundEmitter, DateTime> CustomRangeEmitters = new Dictionary<MyEntity3DSoundEmitter, DateTime>();
        private static readonly Dictionary<string, DateTime> LastLogBySignature = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static bool _loggedCustomRangeFailure;
        private static string _lastStatus = "waiting for block/world emitter";

        public static void ResetRuntimeState()
        {
            PurgeCustomRanges(true);
            CustomRangeEmitters.Clear();
            LastLogBySignature.Clear();
            _loggedCustomRangeFailure = false;
            _lastStatus = "reset";
        }

        public static void Update()
        {
            PurgeCustomRanges(false);
        }

        public static string FormatStatus()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} | activeCustom={1}",
                _lastStatus,
                CustomRangeEmitters.Count);
        }

        public static bool TryPrimeEmitter(MyEntity3DSoundEmitter emitter, string cueName, string reason)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.PlayerFilterBlockEnabled)
            {
                _lastStatus = "disabled";
                ClearIfTracked(emitter);
                return false;
            }

            if (!TryResolveEffectiveRange(cueName, settings, out float vanillaRange, out float effectiveRange, out string category))
            {
                ReportSkippedCue(cueName, reason, "not scalable");
                ClearIfTracked(emitter);
                return false;
            }

            return TryApplyToEmitter(emitter, cueName, effectiveRange, vanillaRange, reason ?? category);
        }

        public static bool TryPrimeDistanceGate(string cueName, ref float? customMaxDistance, string reason)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.PlayerFilterBlockEnabled)
            {
                _lastStatus = "disabled";
                return false;
            }

            if (!TryResolveEffectiveRange(cueName, settings, out float vanillaRange, out float effectiveRange, out string category))
            {
                ReportSkippedCue(cueName, reason, "gate not scalable");
                return false;
            }

            float existing = Math.Max(0f, customMaxDistance.GetValueOrDefault(0f));
            float safeRange = Math.Max(existing, Math.Max(1f, effectiveRange));
            float safeVanillaRange = Math.Max(1f, vanillaRange);
            if (safeRange <= Math.Max(existing, safeVanillaRange) + 0.5f)
            {
                _lastStatus = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} vanilla={2:0}m existing={3:0}m scale={4:0.00} no gate override",
                    reason ?? category ?? "gate",
                    Trim(cueName, 24),
                    safeVanillaRange,
                    existing,
                    settings.PlayerFilterBlockRangeScale);
                return false;
            }

            customMaxDistance = safeRange;
            _lastStatus = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} gate={2:0}m vanilla={3:0}m existing={4:0}m scale={5:0.00}",
                reason ?? category ?? "gate",
                Trim(cueName, 24),
                safeRange,
                safeVanillaRange,
                existing,
                settings.PlayerFilterBlockRangeScale);
            LogAppliedIfDue(cueName, safeRange, safeVanillaRange, reason ?? "gate");
            return true;
        }

        public static bool TryApplyToEmitter(MyEntity3DSoundEmitter emitter, string cueName, float effectiveRange, float vanillaRange, string reason)
        {
            if (emitter == null || effectiveRange <= 0f || vanillaRange <= 0f)
                return false;

            try
            {
                float safeRange = Math.Max(1f, effectiveRange);
                float safeVanillaRange = Math.Max(1f, vanillaRange);
                if (safeRange <= safeVanillaRange + 0.5f)
                {
                    ClearIfTracked(emitter);
                    _lastStatus = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} vanilla={2:0}m scale={3:0.00} no custom range needed",
                        reason ?? "range",
                        Trim(cueName, 24),
                        safeVanillaRange,
                        SettingsManager.Current?.PlayerFilterBlockRangeScale ?? 1f);
                    return false;
                }

                emitter.CustomMaxDistance = safeRange;
                CustomRangeEmitters[emitter] = DateTime.UtcNow;
                _lastStatus = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} custom={2:0}m vanilla={3:0}m scale={4:0.00}",
                    reason ?? "range",
                    Trim(cueName, 24),
                    safeRange,
                    safeVanillaRange,
                    SettingsManager.Current?.PlayerFilterBlockRangeScale ?? 1f);
                LogAppliedIfDue(cueName, safeRange, safeVanillaRange, reason);
                return true;
            }
            catch (Exception ex)
            {
                if (!_loggedCustomRangeFailure)
                {
                    _loggedCustomRangeFailure = true;
                    V2DebugLog.WriteEvent("block-range-scale-failed", ex.GetType().Name + ": " + ex.Message);
                    MyLog.Default.WriteLine("[RealisticSoundPlus] Block range scale failed: " + ex);
                }

                return false;
            }
        }

        public static bool TryResolveEffectiveRange(string cueName, RealisticSoundPlusSettings settings, out float vanillaRange, out float effectiveRange, out string category)
        {
            vanillaRange = 0f;
            effectiveRange = 0f;
            category = null;

            if (settings == null || !IsScalableCue(cueName, out category))
                return false;

            vanillaRange = ResolveVanillaMaxDistance(cueName, settings);
            effectiveRange = ResolveEffectiveRange(settings, vanillaRange);
            return vanillaRange > 0f && effectiveRange > 0f;
        }

        public static float ResolveVanillaMaxDistance(string cueName, RealisticSoundPlusSettings settings)
        {
            if (V2AudioDefinitionCatalog.TryGet(cueName, out V2AudioDefinitionCatalog.SoundInfo info))
                return Math.Max(1f, info.MaxDistance);

            return settings != null ? Math.Max(1f, settings.PlayerFilterBlockRange) : 80f;
        }

        public static float ResolveEffectiveRange(RealisticSoundPlusSettings settings, float vanillaRange)
        {
            float rangeScale = Math.Max(0.1f, settings?.PlayerFilterBlockRangeScale ?? 1f);
            return Math.Max(1f, Math.Max(1f, vanillaRange) * rangeScale);
        }

        private static bool IsScalableCue(string cueName, out string category)
        {
            category = null;
            string value = cueName ?? string.Empty;
            if (value.Length == 0 || value == "NullOrEmpty")
                return false;

            if (V2AuxCueClassifier.IsNonWorldCue(value) || V2AuxCueClassifier.IsEngineCue(value) || V2AuxCueClassifier.IsPlayerLocalCue(value))
                return false;

            if (V2AuxCueClassifier.IsKnownBlockCue(value) || V2AuxCueClassifier.IsKnownBlockCueButNeedsPhysicalSource(value))
            {
                category = "block";
                return true;
            }

            if (V2AuxCueClassifier.IsEnvironmentCue(value))
                return false;

            category = "world";
            return true;
        }

        private static void ClearIfTracked(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null || !CustomRangeEmitters.ContainsKey(emitter))
                return;

            try
            {
                emitter.CustomMaxDistance = null;
            }
            catch
            {
            }

            CustomRangeEmitters.Remove(emitter);
        }

        private static void PurgeCustomRanges(bool force)
        {
            if (CustomRangeEmitters.Count == 0)
                return;

            bool disable = !SettingsManager.Current.PlayerFilterBlockEnabled;
            DateTime now = DateTime.UtcNow;
            List<MyEntity3DSoundEmitter> remove = null;
            foreach (KeyValuePair<MyEntity3DSoundEmitter, DateTime> pair in CustomRangeEmitters)
            {
                bool expired = force || disable || now - pair.Value > TrackedLifetime;
                if (!expired)
                    continue;

                if (remove == null)
                    remove = new List<MyEntity3DSoundEmitter>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                ClearIfTracked(remove[i]);
        }

        private static void LogAppliedIfDue(string cueName, float effectiveRange, float vanillaRange, string reason)
        {
            if (!SettingsManager.Current.V2DebugLogEnabled)
                return;

            string signature = (reason ?? "?") + ":" + (cueName ?? "?") + ":" + effectiveRange.ToString("0", CultureInfo.InvariantCulture);
            DateTime now = DateTime.UtcNow;
            if (LastLogBySignature.TryGetValue(signature, out DateTime last) && now - last < TimeSpan.FromSeconds(3))
                return;

            LastLogBySignature[signature] = now;
            V2DebugLog.WriteEvent("block-range-scale", string.Format(
                CultureInfo.InvariantCulture,
                "{0} cue={1} vanilla={2:0}m effective={3:0}m scale={4:0.00}",
                reason ?? "range",
                cueName ?? "?",
                vanillaRange,
                effectiveRange,
                SettingsManager.Current.PlayerFilterBlockRangeScale));
        }

        private static void ReportSkippedCue(string cueName, string reason, string detail)
        {
            _lastStatus = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} scale={3:0.00}",
                reason ?? "range",
                Trim(cueName, 24),
                detail ?? "skipped",
                SettingsManager.Current?.PlayerFilterBlockRangeScale ?? 1f);

            if (SettingsManager.Current == null || !SettingsManager.Current.V2DebugLogEnabled)
                return;

            string signature = "skip:" + (reason ?? "?") + ":" + (cueName ?? "?");
            DateTime now = DateTime.UtcNow;
            if (LastLogBySignature.TryGetValue(signature, out DateTime last) && now - last < TimeSpan.FromSeconds(3))
                return;

            LastLogBySignature[signature] = now;
            V2DebugLog.WriteEvent("block-range-skip", _lastStatus);
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;

            return value.Substring(0, Math.Max(0, max - 1)) + "~";
        }
    }
}
