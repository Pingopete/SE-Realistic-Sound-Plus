using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Audio;

namespace RealisticSoundPlus.AudioEngineV2
{
    [HarmonyPatch]
    internal static class V2VanillaShipCueSuppressionPatch
    {
        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySound", new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) })]
        [HarmonyPrefix]
        private static bool BeforePlaySoundPair(MyEntity3DSoundEmitter __instance, MySoundPair soundId)
        {
            return !AudioEngineV2Runtime.ShouldSuppressVanillaShipCue(__instance, soundId?.ToString());
        }

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySoundWithDistance", new[] { typeof(MyCueId), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) })]
        [HarmonyPrefix]
        private static bool BeforePlaySoundWithDistance(MyEntity3DSoundEmitter __instance, MyCueId soundId)
        {
            return !AudioEngineV2Runtime.ShouldSuppressVanillaShipCue(__instance, soundId.ToString());
        }

        public static void ResetRuntimeState()
        {
        }
    }
}
