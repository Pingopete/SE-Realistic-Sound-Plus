using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "SelectEffect")]
    internal static class ThrusterFilterPatch
    {
        private static bool _disabled;
        private static int _patchHits;
        private static int _speedAmbientFilterHits;

        private static void Postfix(MyEntity3DSoundEmitter __instance, ref MyStringHash __result)
        {
            if (_disabled)
                return;

            try
            {
                if (IsSpeedAmbientAudioEmitter(__instance))
                {
                    string speedEffectSubtype = GetSpeedAmbientEffectSubtype(__instance);
                    if (!string.IsNullOrEmpty(speedEffectSubtype))
                    {
                        __result = MyStringHash.GetOrCompute(speedEffectSubtype);

                        if (++_speedAmbientFilterHits == 1)
                            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Speed ambient filter override is active: " + speedEffectSubtype);
                    }

                    return;
                }

                if (!IsEngineAudioEmitter(__instance) && !ExteriorWeaponAudioPatch.IsExteriorWeaponAudioEmitter(__instance))
                    return;

                if (ExteriorSoundTransmission.CalculateEffectiveMufflingStrength(__instance.SourcePosition) <= 0f)
                {
                    __result = MyStringHash.NullOrEmpty;
                    return;
                }

                string effectSubtype = GetExteriorEffectSubtype(__instance);
                if (string.IsNullOrEmpty(effectSubtype))
                    return;

                __result = MyStringHash.GetOrCompute(effectSubtype);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Exterior audio low-pass filter override is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling engine filter patch after error: " + ex);
            }
        }

        private static string GetSpeedAmbientEffectSubtype(MyEntity3DSoundEmitter emitter)
        {
            if (ExteriorSoundTransmission.IsListenerInsideShip())
                return SettingsManager.GetSpeedAmbientFilterEffectSubtype();

            if (ExteriorSoundTransmission.CalculateEffectiveMufflingStrength(emitter.SourcePosition) <= 0f)
                return null;

            return IsExteriorVacuum(emitter.SourcePosition)
                ? "LowPassNoHelmetNoOxy"
                : SettingsManager.GetEngineFilterEffectSubtype();
        }

        private static string GetExteriorEffectSubtype(MyEntity3DSoundEmitter emitter)
        {
            return IsExteriorVacuum(emitter.SourcePosition)
                ? "LowPassNoHelmetNoOxy"
                : SettingsManager.GetEngineFilterEffectSubtype();
        }

        private static bool IsExteriorVacuum(Vector3D sourcePosition)
        {
            if (ExteriorSoundTransmission.IsListenerInsideShip())
                return false;

            Vector3D listenerPosition = Sandbox.ModAPI.MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (listenerPosition == Vector3D.Zero)
                return true;

            float pressure = Math.Max(
                ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition),
                ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition));
            return pressure < 0.1f;
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

        public static bool IsSpeedAmbientAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            return EngineAudioClassifier.IsKnownSpeedAmbientCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownSpeedAmbientCue(emitter.Sound?.CueEnum)
                || EngineAudioClassifier.IsKnownSpeedAmbientCue(emitter.SecondarySound?.CueEnum);
        }
    }
}
