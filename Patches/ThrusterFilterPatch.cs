using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "SelectEffect")]
    internal static class ThrusterFilterPatch
    {
        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyEntity3DSoundEmitter __instance, ref MyStringHash __result)
        {
            if (_disabled)
                return;

            try
            {
                if (!ShipInteriorMufflingPatch.IsKnownThrusterEmitter(__instance))
                    return;

                string effectSubtype = SettingsManager.GetEngineFilterEffectSubtype();
                if (string.IsNullOrEmpty(effectSubtype))
                    return;

                __result = MyStringHash.GetOrCompute(effectSubtype);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Thruster low-pass filter override is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling thruster filter patch after error: " + ex);
            }
        }
    }
}