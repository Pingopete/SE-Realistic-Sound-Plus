using VRage.Utils;
using RealisticSoundPlus.AudioEngineV2;

namespace RealisticSoundPlus.Patches
{
    internal static class AudioPatchRuntime
    {
        public static void ResetForSession(string reason)
        {
            ThrusterFilterPatch.ResetRuntimeState();
            ExteriorWeaponAudioPatch.ResetRuntimeState();
            CharacterBreathPatch.ResetRuntimeState();
            ExteriorSoundTransmission.ResetRuntimeState();
            AudioDiagnostics.ResetRuntimeState();
            AudioEngineV2Runtime.ResetForSession(reason);
            V2ThrusterAudioPatch.ResetRuntimeState();
            V2ShipEnvironmentPatch.ResetRuntimeState();

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio runtime state reset: " + reason);
        }
    }
}
