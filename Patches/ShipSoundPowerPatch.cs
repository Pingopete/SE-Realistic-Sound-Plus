using HarmonyLib;
using Sandbox.Game.EntityComponents;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateSpeedBasedShipSound")]
    internal static class ShipSoundPowerPatch
    {
        public static bool Enabled = false;

        private static void Postfix(MyShipSoundComponent __instance)
        {
            if (!Enabled)
                return;

            // First scaffold only: prove the patch is loadable before changing audio behavior.
            MyLog.Default.WriteLine("[RealisticSoundPlus] Ship sound power patch reached.");
        }
    }
}
