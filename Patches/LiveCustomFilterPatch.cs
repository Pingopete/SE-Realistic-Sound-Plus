using System;
using System.Reflection;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class LiveCustomFilterPatch
    {
        private static bool _disabled;
        private static bool _loggedTarget;

        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("VRage.Audio.MyEffectInstance");
            MethodInfo method = type != null ? AccessTools.Method(type, "UpdateFilter") : null;
            if (!_loggedTarget)
            {
                _loggedTarget = true;
                V2DebugLog.WriteEvent("live-filter-patch", method != null ? "target=UpdateFilter" : "target-missing");
            }

            return method;
        }

        private static void Prefix(object __0, object __1)
        {
            if (_disabled)
                return;

            try
            {
                RspDynamicAudioFilters.TryPrepareLiveCustomFilterEffect(__0, __1, SettingsManager.Current);
            }
            catch (Exception ex)
            {
                _disabled = true;
                V2DebugLog.WriteEvent("live-filter-patch-failed", ex.Message);
                MyLog.Default.WriteLine("[RealisticSoundPlus] Live custom filter patch disabled: " + ex);
            }
        }

        private static void Postfix(object __0)
        {
            if (_disabled)
                return;

            try
            {
                RspDynamicAudioFilters.TryApplyLiveCustomFilterFromSoundData(__0, SettingsManager.Current);
            }
            catch (Exception ex)
            {
                _disabled = true;
                V2DebugLog.WriteEvent("live-filter-patch-failed", "postfix " + ex.Message);
                MyLog.Default.WriteLine("[RealisticSoundPlus] Live custom filter postfix disabled: " + ex);
            }
        }
    }
}
