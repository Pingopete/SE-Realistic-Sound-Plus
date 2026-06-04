using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Audio;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class CenteredSpoolSuppressionPatch
    {
        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySound", new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) })]
        [HarmonyPrefix]
        private static bool BeforePlaySoundPair(MyEntity3DSoundEmitter __instance, MySoundPair soundId)
        {
            return AllowOrSuppress(__instance, soundId?.ToString());
        }

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySoundWithDistance", new[] { typeof(MyCueId), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) })]
        [HarmonyPrefix]
        private static bool BeforePlaySoundWithDistance(MyEntity3DSoundEmitter __instance, MyCueId soundId)
        {
            return AllowOrSuppress(__instance, soundId.ToString());
        }

        private static bool AllowOrSuppress(MyEntity3DSoundEmitter emitter, string cueName)
        {
            if (emitter == null || DirectionalSpoolAudioPatch.IsDirectionalSpoolEmitter(emitter))
                return true;

            if (!EngineAudioClassifier.IsKnownCenteredShipSpoolCue(cueName))
                return true;

            Vector3D sourcePosition = emitter.SourcePosition;
            AudioDiagnostics.RecordVirtualCue(cueName, "center-spool-blocked", emitter.VolumeMultiplier, 0f, 0f, 0f, sourcePosition);
            return false;
        }
    }
}