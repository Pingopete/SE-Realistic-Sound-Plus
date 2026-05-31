using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class SpatialThrusterAudioPatch
    {
        private const float SilentThrustRatio = 0.01f;
        private static readonly FieldInfo SoundEmitterField = AccessTools.Field(typeof(MyThrust), "m_soundEmitter");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> BaseVolumeByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();

        private static bool _disabled;
        private static int _patchHits;

        [HarmonyPatch(typeof(MyThrust), "UpdateAfterSimulation")]
        [HarmonyPostfix]
        private static void AfterSimulation(MyThrust __instance)
        {
            Apply(__instance);
        }

        [HarmonyPatch(typeof(MyThrust), "UpdateSoundState")]
        [HarmonyPostfix]
        private static void AfterSoundState(MyThrust __instance)
        {
            Apply(__instance);
        }

        private static void Apply(MyThrust thruster)
        {
            if (_disabled || thruster == null)
                return;

            try
            {
                MyEntity3DSoundEmitter emitter = (MyEntity3DSoundEmitter)SoundEmitterField.GetValue(thruster);
                if (emitter == null)
                    return;

                float baseVolume = RestoreEmitter(emitter);

                if (!SettingsManager.Current.SpatialAudioEnabled)
                    return;

                Vector3D sourcePosition = thruster.WorldMatrix.Translation;
                emitter.Force2D = false;
                emitter.Force3D = true;
                emitter.SetPosition(sourcePosition);

                float scale = CalculateSpatialScale(thruster);
                if (scale <= 0f)
                {
                    emitter.VolumeMultiplier = 0f;
                    BaseVolumeByEmitter[emitter] = baseVolume;
                    return;
                }

                float transmission = CalculateTransmission(sourcePosition);
                emitter.VolumeMultiplier = baseVolume * scale * transmission;
                BaseVolumeByEmitter[emitter] = baseVolume;

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Per-thruster spatial audio is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling spatial thruster audio patch after error: " + ex);
            }
        }

        private static float CalculateSpatialScale(MyThrust thruster)
        {
            if (!thruster.IsWorking)
                return 0f;

            float maxForce = thruster.BlockDefinition != null ? thruster.BlockDefinition.ForceMagnitude : 0f;
            if (maxForce <= 0f)
                maxForce = Math.Max(thruster.ThrustForceLength, 1f);

            float thrustRatio = Clamp01(thruster.ThrustForceLength / maxForce);
            if (thrustRatio < SilentThrustRatio)
                return 0f;

            var settings = SettingsManager.Current;
            float shaped = Clamp01((float)Math.Pow(thrustRatio, settings.AudioCurveExponent));
            float thrusterPresence = CalculateThrusterPresence(maxForce, settings);
            return shaped * thrusterPresence * settings.EngineGain * settings.SpatialEmitterGain;
        }

        private static float CalculateThrusterPresence(float maxForce, RealisticSoundPlusSettings settings)
        {
            float forceLog = (float)Math.Log10(Math.Max(maxForce, 1f));
            float normalized = Clamp01((forceLog - settings.QuietShipForceLog10) / (settings.LoudShipForceLog10 - settings.QuietShipForceLog10));
            return settings.MinimumShipPresence + (1f - settings.MinimumShipPresence) * normalized;
        }

        private static float CalculateTransmission(Vector3D sourcePosition)
        {
            var settings = SettingsManager.Current;
            if (settings.MufflingStrength <= 0f)
                return 1f;

            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (listenerPosition == Vector3D.Zero)
                return 1f;

            double distance = Vector3D.Distance(listenerPosition, sourcePosition);
            float distanceBlend = Clamp01((float)((distance - settings.NearDistance) / (settings.FarDistance - settings.NearDistance)));
            float distanceTransmission = Lerp(1f, settings.FarDistanceTransmission, distanceBlend);
            float fullTransmission = Clamp01(settings.InteriorBaseTransmission * distanceTransmission);
            return Clamp01(1f - (1f - fullTransmission) * settings.MufflingStrength);
        }

        private static float RestoreEmitter(MyEntity3DSoundEmitter emitter)
        {
            float current = emitter.VolumeMultiplier;
            if (!BaseVolumeByEmitter.TryGetValue(emitter, out float baseVolume))
                return current;

            BaseVolumeByEmitter.Remove(emitter);
            emitter.VolumeMultiplier = baseVolume;
            return baseVolume;
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * amount;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
