using VRage.Utils;
using RealisticSoundPlus.AudioEngineV2;

namespace RealisticSoundPlus.Patches
{
    internal static class AudioPatchRuntime
    {
        public static void ResetForSession(string reason)
        {
            ThrusterFilterPatch.ResetRuntimeState();
            CharacterBreathPatch.ResetRuntimeState();
            ShipSeatAudioPatch.ResetRuntimeState();
            ExteriorSoundTransmission.ResetRuntimeState();
            AudioDiagnostics.ResetRuntimeState();
            AudioVoiceCatalog.ResetRuntimeState();
            AudioEngineV2Runtime.ResetForSession(reason);
            V2DebugLog.ResetForSession(reason);
            V2ThrusterAudioPatch.ResetRuntimeState();
            V2ShipEnvironmentPatch.ResetRuntimeState();
            V2VanillaShipCueSuppressionPatch.ResetRuntimeState();
            EnvironmentAmbiencePatch.ResetRuntimeState();
            V2BlockRangeScaler.ResetRuntimeState();

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio runtime state reset: " + reason);
        }
    }
}
