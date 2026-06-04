using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    internal static class AudioPatchRuntime
    {
        public static void ResetForSession(string reason)
        {
            ShipSoundPowerPatch.ResetRuntimeState();
            ShipInteriorMufflingPatch.ResetRuntimeState();
            SpatialThrusterAudioPatch.ResetRuntimeState();
            DirectionalSpoolAudioPatch.ResetRuntimeState();
            ThrusterFilterPatch.ResetRuntimeState();
            HydrogenEngineAudioPatch.ResetRuntimeState();
            ExteriorWeaponAudioPatch.ResetRuntimeState();
            CharacterBreathPatch.ResetRuntimeState();
            ShipSeatAudioPatch.ResetRuntimeState();
            ExteriorSoundTransmission.ResetRuntimeState();
            AudioDiagnostics.ResetRuntimeState();

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio runtime state reset: " + reason);
        }
    }
}