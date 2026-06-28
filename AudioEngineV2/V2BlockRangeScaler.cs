using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2BlockRangeScaler
    {
        private static readonly Dictionary<MyEntity3DSoundEmitter, TrackedCustomRange> CustomRangeEmitters = new Dictionary<MyEntity3DSoundEmitter, TrackedCustomRange>();
        private static readonly Dictionary<string, DateTime> LastLogBySignature = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, RangeCacheEntry> EffectiveRangeCache = new Dictionary<string, RangeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static bool _loggedCustomRangeFailure;
        private static string _lastStatus = "waiting for block/world emitter";
        private static string _rangeCacheSignature = string.Empty;
        private static DateTime _lastSkippedStatusUtc = DateTime.MinValue;
        private static DateTime _lastDisplayStatusUtc = DateTime.MinValue;

        public static void ResetRuntimeState()
        {
            PurgeCustomRanges(true);
            CustomRangeEmitters.Clear();
            LastLogBySignature.Clear();
            EffectiveRangeCache.Clear();
            _loggedCustomRangeFailure = false;
            _lastStatus = "reset";
            _rangeCacheSignature = string.Empty;
            _lastSkippedStatusUtc = DateTime.MinValue;
            _lastDisplayStatusUtc = DateTime.MinValue;
        }

        public static void Update()
        {
            bool rangeChanged = EnsureRangeCacheCurrent(SettingsManager.Current);
            if (rangeChanged)
                ReapplyTrackedCustomRanges();
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
                SetDisplayStatus("disabled", true);
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
                SetDisplayStatus("disabled", true);
                return false;
            }

            if (!TryResolveEffectiveRange(cueName, settings, out float vanillaRange, out float effectiveRange, out string category))
            {
                ReportSkippedCue(cueName, reason, "gate not scalable");
                return false;
            }

            float existing = Math.Max(0f, customMaxDistance.GetValueOrDefault(0f));
            float safeRange = Math.Max(1f, effectiveRange);
            float safeVanillaRange = Math.Max(1f, vanillaRange);
            bool extendsVanilla = safeRange > safeVanillaRange + 0.5f;
            bool capsVanilla = safeRange < safeVanillaRange - 0.5f;
            bool changesExisting = existing > 0f && Math.Abs(safeRange - existing) > 0.5f;
            if (!extendsVanilla && !capsVanilla && !changesExisting)
            {
                SetDisplayStatus(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} vanilla={2:0}m existing={3:0}m range={4:0}m no gate override",
                    reason ?? category ?? "gate",
                    Trim(cueName, 24),
                    safeVanillaRange,
                    existing,
                    ResolveBlockSoundRange(settings)), false);
                return false;
            }

            customMaxDistance = safeRange;
            SetDisplayStatus(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} gate={2:0}m vanilla={3:0}m existing={4:0}m range={5:0}m",
                reason ?? category ?? "gate",
                Trim(cueName, 24),
                safeRange,
                safeVanillaRange,
                existing,
                ResolveBlockSoundRange(settings)), false);
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
                if (Math.Abs(safeRange - safeVanillaRange) <= 0.5f)
                {
                    ClearIfTracked(emitter);
                    SetDisplayStatus(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} vanilla={2:0}m range={3:0}m no custom range needed",
                        reason ?? "range",
                        Trim(cueName, 24),
                        safeVanillaRange,
                        ResolveBlockSoundRange(SettingsManager.Current)), false);
                    return false;
                }

                emitter.CustomMaxDistance = safeRange;
                CustomRangeEmitters[emitter] = new TrackedCustomRange
                {
                    CueName = cueName
                };
                SetDisplayStatus(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} custom={2:0}m vanilla={3:0}m range={4:0}m",
                    reason ?? "range",
                    Trim(cueName, 24),
                    safeRange,
                    safeVanillaRange,
                    ResolveBlockSoundRange(SettingsManager.Current)), false);
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

            EnsureRangeCacheCurrent(settings);
            string key = cueName ?? string.Empty;
            if (EffectiveRangeCache.TryGetValue(key, out RangeCacheEntry cached))
            {
                vanillaRange = cached.VanillaRange;
                effectiveRange = cached.EffectiveRange;
                category = cached.Category;
                return cached.Resolved;
            }

            RangeCacheEntry entry = new RangeCacheEntry();
            if (settings == null || !IsScalableCue(cueName, out category))
            {
                StoreRangeCacheEntry(key, entry);
                return false;
            }

            vanillaRange = ResolveVanillaMaxDistance(cueName, settings);
            effectiveRange = string.Equals(category, "engine-air", StringComparison.OrdinalIgnoreCase)
                ? ResolveEngineAirRange(settings)
                : ResolveEffectiveRange(settings, vanillaRange);
            entry.Resolved = vanillaRange > 0f && effectiveRange > 0f;
            entry.VanillaRange = vanillaRange;
            entry.EffectiveRange = effectiveRange;
            entry.Category = category;
            StoreRangeCacheEntry(key, entry);
            return entry.Resolved;
        }

        public static float ResolveVanillaMaxDistance(string cueName, RealisticSoundPlusSettings settings)
        {
            if (V2AudioDefinitionCatalog.TryGet(cueName, out V2AudioDefinitionCatalog.SoundInfo info))
                return ResolveComparableVanillaMaxDistance(cueName, info.MaxDistance);

            return ResolveBlockSoundRange(settings);
        }

        public static float ResolveEffectiveRange(RealisticSoundPlusSettings settings, float vanillaRange)
        {
            return ResolveBlockSoundRange(settings);
        }

        public static float ResolveBlockSoundRange(RealisticSoundPlusSettings settings)
        {
            return Math.Max(1f, settings?.PlayerFilterBlockMaxRange ?? 100f);
        }

        public static float ResolveEngineAirRange(RealisticSoundPlusSettings settings)
        {
            return Math.Max(1f, settings?.EngineFilterAirRange ?? 1000f);
        }

        private static bool IsScalableCue(string cueName, out string category)
        {
            category = null;
            string value = cueName ?? string.Empty;
            if (value.Length == 0 || value == "NullOrEmpty")
                return false;

            if (V2AuxCueClassifier.IsNonWorldCue(value) || V2AuxCueClassifier.IsPlayerLocalCue(value))
                return false;

            if (V2AuxCueClassifier.IsEngineCue(value))
            {
                category = "engine-air";
                return true;
            }

            if (V2AuxCueClassifier.IsKnownBlockCue(value) || V2AuxCueClassifier.IsKnownBlockCueButNeedsPhysicalSource(value))
            {
                category = "block";
                return true;
            }

            if (V2AuxCueClassifier.IsEnvironmentCue(value))
                return false;

            return false;
        }

        private static float ResolveComparableVanillaMaxDistance(string cueName, float maxDistance)
        {
            float result = Math.Max(1f, maxDistance);
            if (TryGetPairedSoundMaxDistance(cueName, out float pairedMaxDistance))
                result = Math.Max(result, pairedMaxDistance);

            return result;
        }

        private static bool TryGetPairedSoundMaxDistance(string cueName, out float maxDistance)
        {
            maxDistance = 0f;
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            string pairedCueName = null;
            if (cueName.StartsWith("Real", StringComparison.OrdinalIgnoreCase))
                pairedCueName = "Arc" + cueName.Substring(4);
            else if (cueName.StartsWith("Arc", StringComparison.OrdinalIgnoreCase))
                pairedCueName = "Real" + cueName.Substring(3);

            if (string.IsNullOrWhiteSpace(pairedCueName))
                return false;

            if (!V2AudioDefinitionCatalog.TryGet(pairedCueName, out V2AudioDefinitionCatalog.SoundInfo paired))
                return false;

            maxDistance = Math.Max(1f, paired.MaxDistance);
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

            bool disable = SettingsManager.Current == null || !SettingsManager.Current.PlayerFilterBlockEnabled;
            List<MyEntity3DSoundEmitter> remove = null;
            foreach (KeyValuePair<MyEntity3DSoundEmitter, TrackedCustomRange> pair in CustomRangeEmitters)
            {
                bool expired = force || disable || !IsEmitterActive(pair.Key);
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

        private static void ReapplyTrackedCustomRanges()
        {
            if (CustomRangeEmitters.Count == 0)
                return;

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.PlayerFilterBlockEnabled)
            {
                PurgeCustomRanges(true);
                return;
            }

            List<KeyValuePair<MyEntity3DSoundEmitter, TrackedCustomRange>> tracked = new List<KeyValuePair<MyEntity3DSoundEmitter, TrackedCustomRange>>(CustomRangeEmitters);
            for (int i = 0; i < tracked.Count; i++)
            {
                MyEntity3DSoundEmitter emitter = tracked[i].Key;
                string cueName = tracked[i].Value.CueName;
                if (!IsEmitterActive(emitter) || string.IsNullOrWhiteSpace(cueName))
                {
                    ClearIfTracked(emitter);
                    continue;
                }

                if (!TryResolveEffectiveRange(cueName, settings, out float vanillaRange, out float effectiveRange, out string category))
                {
                    ClearIfTracked(emitter);
                    continue;
                }

                TryApplyToEmitter(emitter, cueName, effectiveRange, vanillaRange, "refresh-" + (category ?? "range"));
            }
        }

        private static bool IsEmitterActive(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            try
            {
                return emitter.Entity == null || !emitter.Entity.Closed;
            }
            catch
            {
                return true;
            }
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
                "{0} cue={1} vanilla={2:0}m effective={3:0}m range={4:0}m",
                reason ?? "range",
                cueName ?? "?",
                vanillaRange,
                effectiveRange,
                ResolveBlockSoundRange(SettingsManager.Current)));
        }

        private static void ReportSkippedCue(string cueName, string reason, string detail)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastSkippedStatusUtc < TimeSpan.FromMilliseconds(500))
                return;

            _lastSkippedStatusUtc = now;
            SetDisplayStatus(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} range={3:0}m",
                reason ?? "range",
                Trim(cueName, 24),
                detail ?? "skipped",
                ResolveBlockSoundRange(SettingsManager.Current)), false);

            // Skipped cues are expected for footsteps, tools, ship engines, UI, and many other non-block sounds.
            // Logging each rejected gate is too expensive in large bases where sound starts happen constantly.
        }

        private static bool EnsureRangeCacheCurrent(RealisticSoundPlusSettings settings)
        {
            string signature = settings == null
                ? "null"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1:0.0}:{2:0.0}",
                    settings.PlayerFilterBlockEnabled ? 1 : 0,
                    ResolveBlockSoundRange(settings),
                    ResolveEngineAirRange(settings));

            if (string.Equals(signature, _rangeCacheSignature, StringComparison.Ordinal))
                return false;

            EffectiveRangeCache.Clear();
            _rangeCacheSignature = signature;
            return true;
        }

        private static void StoreRangeCacheEntry(string key, RangeCacheEntry entry)
        {
            if (EffectiveRangeCache.Count > 512)
                EffectiveRangeCache.Clear();

            EffectiveRangeCache[key ?? string.Empty] = entry;
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;

            return value.Substring(0, Math.Max(0, max - 1)) + "~";
        }

        private static void SetDisplayStatus(string status, bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastDisplayStatusUtc < TimeSpan.FromMilliseconds(750))
                return;

            _lastDisplayStatusUtc = now;
            _lastStatus = status ?? string.Empty;
        }

        private struct RangeCacheEntry
        {
            public bool Resolved;
            public float VanillaRange;
            public float EffectiveRange;
            public string Category;
        }

        private struct TrackedCustomRange
        {
            public string CueName;
        }
    }
}
