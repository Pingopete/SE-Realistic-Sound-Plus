using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
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
                if (!IsEngineAudioEmitter(__instance))
                    return;

                if (ExteriorSoundTransmission.CalculateEffectiveMufflingStrength(__instance.SourcePosition) <= 0f)
                {
                    __result = MyStringHash.NullOrEmpty;
                    return;
                }

                string effectSubtype = SettingsManager.GetEngineFilterEffectSubtype();
                if (string.IsNullOrEmpty(effectSubtype))
                    return;

                __result = MyStringHash.GetOrCompute(effectSubtype);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Engine low-pass filter override is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling engine filter patch after error: " + ex);
            }
        }

        public static bool IsEngineAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (IsThrusterAudioEmitter(emitter) || IsHydrogenEngineAudioEmitter(emitter))
                return true;

            if (SettingsManager.Current.AmbientMufflingEnabled && IsAmbientAudioEmitter(emitter))
                return true;

            return EngineAudioClassifier.IsKnownEngineCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownEngineCue(emitter.Sound?.CueEnum)
                || EngineAudioClassifier.IsKnownEngineCue(emitter.SecondarySound?.CueEnum);
        }

        public static bool IsThrusterAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (ShipInteriorMufflingPatch.IsKnownThrusterEmitter(emitter))
                return true;

            return emitter.Entity is MyThrust;
        }

        public static bool IsAmbientAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (HydrogenEngineAudioPatch.IsKnownAmbientEmitter(emitter))
                return true;

            return EngineAudioClassifier.IsKnownAmbientCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownAmbientCue(emitter.Sound?.CueEnum)
                || EngineAudioClassifier.IsKnownAmbientCue(emitter.SecondarySound?.CueEnum);
        }

        public static bool IsHydrogenEngineAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (HydrogenEngineAudioPatch.IsKnownHydrogenEngineEmitter(emitter))
                return true;

            return emitter.Entity is MyHydrogenEngine;
        }
    }
}
