using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    [HarmonyPatch]
    internal static class V2ThrusterAudioPatch
    {
        private static bool _disabled;
        private static int _patchHits;

        [HarmonyPatch(typeof(MyThrust), "UpdateAfterSimulation")]
        [HarmonyPostfix]
        private static void AfterSimulation(MyThrust __instance)
        {
            if (_disabled || __instance == null)
                return;

            try
            {
                AudioEngineV2Runtime.ReportThruster(__instance);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 thruster reporter is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling V2 thruster reporter after error: " + ex);
            }
        }

        public static void ResetRuntimeState()
        {
            _disabled = false;
            _patchHits = 0;
        }
    }
}
