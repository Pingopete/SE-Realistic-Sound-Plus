using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Audio;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class EngineCueEmitterMarkerPatch
    {
        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySound", new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) })]
        [HarmonyPrefix]
        private static void BeforePlaySoundPair(MyEntity3DSoundEmitter __instance, MySoundPair soundId)
        {
            Mark(__instance, soundId?.ToString());
        }

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySoundWithDistance", new[] { typeof(MyCueId), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) })]
        [HarmonyPrefix]
        private static void BeforePlaySoundWithDistance(MyEntity3DSoundEmitter __instance, MyCueId soundId)
        {
            Mark(__instance, soundId.ToString());
        }

        private static void Mark(MyEntity3DSoundEmitter emitter, string cueName)
        {
            if (emitter != null && EngineAudioClassifier.IsKnownEngineCue(cueName))
                ThrusterFilterPatch.MarkKnownEngineCueEmitter(emitter);
        }
    }
}