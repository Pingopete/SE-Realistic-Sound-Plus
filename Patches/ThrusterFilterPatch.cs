using System;
using System.Collections.Generic;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "SelectEffect")]
    internal static class ThrusterFilterPatch
    {
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownEngineCueEmitters = new HashSet<MyEntity3DSoundEmitter>();
        private static readonly Dictionary<string, DateTime> LastFilterLogs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan FilterLogInterval = TimeSpan.FromSeconds(2);

        private static bool _disabled;
        private static int _patchHits;

        public static bool Disabled => _disabled;

        public static int PatchHits => _patchHits;

        private static void Postfix(MyEntity3DSoundEmitter __instance, ref MyStringHash __result)
        {
            if (_disabled)
                return;

            try
            {
                if (AudioEngineV2Runtime.ShouldSkipEngineFilter(__instance))
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "v2-filter-skip", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    LogFilterSelection(__instance, "skip", "none", "skip");
                    return;
                }

                bool engineAudio = IsEngineAudioEmitter(__instance);
                if (!engineAudio)
                    return;

                bool v2Emitter = AudioEngineV2Runtime.IsV2Emitter(__instance);
                if (!v2Emitter && ExteriorSoundTransmission.CalculateEffectiveMufflingStrength(__instance.SourcePosition) <= 0f)
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "filter-off", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    LogFilterSelection(__instance, AudioEngineV2Runtime.GetEmitterFilterRouteName(__instance), "none", "muffling-off");
                    return;
                }

                string effectSubtype = AudioEngineV2Runtime.GetEngineFilterEffectSubtype(__instance);
                if (string.IsNullOrEmpty(effectSubtype))
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "filter-none", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    LogFilterSelection(__instance, AudioEngineV2Runtime.GetEmitterFilterRouteName(__instance), "none", "setting-off");
                    return;
                }

                if (RspDynamicAudioFilters.IsCustomFilterSubtype(effectSubtype) && !RspDynamicAudioFilters.UpdateFromSettings(SettingsManager.Current))
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "filter-custom-missing", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    LogFilterSelection(__instance, AudioEngineV2Runtime.GetEmitterFilterRouteName(__instance), effectSubtype, "custom-missing");
                    return;
                }

                __result = MyStringHash.GetOrCompute(effectSubtype);
                string filterRoute = AudioEngineV2Runtime.GetEmitterFilterRouteName(__instance);
                AudioDiagnostics.RecordEmitter(__instance, "filter-" + filterRoute + "-" + effectSubtype, __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                LogFilterSelection(__instance, filterRoute, effectSubtype, "selected");

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Exterior audio low-pass filter override is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling engine filter patch after error: " + ex);
            }
        }

        public static void ResetRuntimeState()
        {
            KnownEngineCueEmitters.Clear();
            LastFilterLogs.Clear();
            _disabled = false;
            _patchHits = 0;
        }

        public static void MarkKnownEngineCueEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter != null)
                KnownEngineCueEmitters.Add(emitter);
        }

        public static bool IsEngineAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (AudioEngineV2Runtime.IsV2Emitter(emitter))
                return true;

            if (KnownEngineCueEmitters.Contains(emitter))
                return true;

            return EngineAudioClassifier.IsKnownEngineCue(DescribeCue(emitter))
                || EngineAudioClassifier.IsKnownEngineCue(DescribeSecondaryCue(emitter));
        }

        public static bool IsThrusterAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            return AudioEngineV2Runtime.IsV2Emitter(emitter);
        }

        private static void LogFilterSelection(MyEntity3DSoundEmitter emitter, string route, string effectSubtype, string reason)
        {
            if (emitter == null || !SettingsManager.Current.V2DebugLogEnabled)
                return;

            string cueName = DescribeCue(emitter);
            string key = (route ?? "?") + "|" + (effectSubtype ?? "none") + "|" + cueName + "|" + reason;
            DateTime now = DateTime.UtcNow;
            if (LastFilterLogs.TryGetValue(key, out DateTime last) && now - last < FilterLogInterval)
                return;

            LastFilterLogs[key] = now;
            V2DebugLog.WriteEvent("filter-select", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "route={0} effect={1} reason={2} cue={3} v={4:0.00} pos={5:0.0},{6:0.0},{7:0.0}",
                route ?? "?",
                string.IsNullOrEmpty(effectSubtype) ? "none" : effectSubtype,
                reason ?? "?",
                cueName,
                emitter.VolumeMultiplier,
                emitter.SourcePosition.X,
                emitter.SourcePosition.Y,
                emitter.SourcePosition.Z));
        }

        private static string DescribeCue(MyEntity3DSoundEmitter emitter)
        {
            try
            {
                string sound = emitter.Sound?.CueEnum.ToString();
                if (!string.IsNullOrWhiteSpace(sound) && sound != "NullOrEmpty")
                    return sound;
            }
            catch
            {
            }

            try
            {
                string soundId = emitter.SoundId.ToString();
                if (!string.IsNullOrWhiteSpace(soundId) && soundId != "NullOrEmpty")
                    return soundId;
            }
            catch
            {
            }

            return "?";
        }

        private static string DescribeSecondaryCue(MyEntity3DSoundEmitter emitter)
        {
            try
            {
                string sound = emitter.SecondarySound?.CueEnum.ToString();
                if (!string.IsNullOrWhiteSpace(sound) && sound != "NullOrEmpty")
                    return sound;
            }
            catch
            {
            }

            return "?";
        }

    }
}
