using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.EntityComponents;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateVolumes")]
    internal static class V2ShipEnvironmentPatch
    {
        private static readonly FieldInfo InsideShipField = AccessTools.Field(typeof(MyShipSoundComponent), "m_insideShip");
        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return;

            try
            {
                bool insideShip = InsideShipField != null && (bool)InsideShipField.GetValue(__instance);
                VanillaShipEnvironment.ReportShipSoundComponent(__instance, insideShip);
                Patches.ExteriorSoundTransmission.ReportListenerInsideShip(insideShip);
                Patches.AudioDiagnostics.UpdateGlobal(insideShip);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 ship environment observer is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling V2 ship environment observer after error: " + ex);
            }
        }

        public static void ResetRuntimeState()
        {
            _disabled = false;
            _patchHits = 0;
        }
    }
}
