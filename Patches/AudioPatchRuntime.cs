using VRage.Utils;
using RealisticSoundPlus.AudioEngineV2;

namespace RealisticSoundPlus.Patches
{
    internal static class AudioPatchRuntime
    {
        public static void ResetForSession(string reason)
        {
            ShipSoundPowerPatch.ResetRuntimeState();
            ShipInteriorMufflingPatch.ResetRuntimeState();
            SpatialThrusterAudioPatch.ResetRuntimeState();
            ThrusterFilterPatch.ResetRuntimeState();
            HydrogenEngineAudioPatch.ResetRuntimeState();
            ExteriorWeaponAudioPatch.ResetRuntimeState();
            CharacterBreathPatch.ResetRuntimeState();
            ShipSeatAudioPatch.ResetRuntimeState();
            ExteriorSoundTransmission.ResetRuntimeState();
            AudioDiagnostics.ResetRuntimeState();
            AudioEngineV2Runtime.ResetForSession(reason);
            V2ThrusterAudioPatch.ResetRuntimeState();

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio runtime state reset: " + reason);
        }
    }
}
