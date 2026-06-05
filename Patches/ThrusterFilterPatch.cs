using System;
using System.Collections.Generic;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "SelectEffect")]
    internal static class ThrusterFilterPatch
    {
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownEngineCueEmitters = new HashSet<MyEntity3DSoundEmitter>();

        private static bool _disabled;
        private static int _patchHits;

        public static bool Disabled => _disabled;

        public static int PatchHits => _patchHits;

        private static void Postfix(MyEntity3DSoundEmitter __instance, ref MyStringHash __result)
        {
            if (_disabled)
                return;

            try
            {
                if (AudioEngineV2Runtime.ShouldSkipEngineFilter(__instance))
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "v2-filter-skip", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    return;
                }

                bool engineAudio = IsEngineAudioEmitter(__instance);
                if (!engineAudio)
                    return;

                if (ExteriorSoundTransmission.CalculateEffectiveMufflingStrength(__instance.SourcePosition) <= 0f)
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "filter-off", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    return;
                }

                string effectSubtype = SettingsManager.GetEngineFilterEffectSubtype();
                if (string.IsNullOrEmpty(effectSubtype))
                {
                    __result = MyStringHash.NullOrEmpty;
                    AudioDiagnostics.RecordEmitter(__instance, "filter-none", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);
                    return;
                }

                __result = MyStringHash.GetOrCompute(effectSubtype);
                AudioDiagnostics.RecordEmitter(__instance, "filter", __instance.VolumeMultiplier, 1f, 1f, __instance.VolumeMultiplier, __instance.SourcePosition);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Exterior audio low-pass filter override is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling engine filter patch after error: " + ex);
            }
        }

        public static void ResetRuntimeState()
        {
            KnownEngineCueEmitters.Clear();
            _disabled = false;
            _patchHits = 0;
        }

        public static void MarkKnownEngineCueEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter != null)
                KnownEngineCueEmitters.Add(emitter);
        }

        public static bool IsEngineAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (AudioEngineV2Runtime.IsV2Emitter(emitter))
                return true;

            if (KnownEngineCueEmitters.Contains(emitter))
                return true;

            return false;
        }

        public static bool IsThrusterAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            return AudioEngineV2Runtime.IsV2Emitter(emitter);
        }

    }
}
