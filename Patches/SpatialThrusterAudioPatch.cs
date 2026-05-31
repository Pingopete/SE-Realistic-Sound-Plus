using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class SpatialThrusterAudioPatch
    {
        private static readonly FieldInfo SoundEmitterField = AccessTools.Field(typeof(MyThrust), "m_soundEmitter");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> BaseVolumeByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, SmoothState> SmoothStatesByEmitter = new Dictionary<MyEntity3DSoundEmitter, SmoothState>();

        private static bool _disabled;
        private static int _patchHits;

        [HarmonyPatch(typeof(MyThrust), "UpdateAfterSimulation")]
        [HarmonyPostfix]
        private static void AfterSimulation(MyThrust __instance)
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
                float transmission = CalculateTransmission(sourcePosition);
                float targetMultiplier = scale * transmission;
                float smoothedMultiplier = SmoothMultiplier(emitter, targetMultiplier);

                emitter.VolumeMultiplier = baseVolume * smoothedMultiplier;
                BaseVolumeByEmitter[emitter] = baseVolume;

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Per-thruster spatial audio is active with de-click smoothing.");
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
            var settings = SettingsManager.Current;
            float fade = SmoothStep(thrustRatio / settings.SpatialSoftFadeRatio);
            float shaped = Clamp01((float)Math.Pow(thrustRatio, settings.AudioCurveExponent));
            float thrusterPresence = CalculateThrusterPresence(maxForce, settings);
            return shaped * fade * thrusterPresence * settings.EngineGain * settings.SpatialEmitterGain;
        }

        private static float CalculateThrusterPresence(float maxForce, RealisticSoundPlusSettings settings)
        {
            float forceLog = (float)Math.Log10(Math.Max(maxForce, 1f));
            float normalized = Clamp01((forceLog - settings.QuietShipForceLog10) / (settings.LoudShipForceLog10 - settings.QuietShipForceLog10));
            return settings.MinimumShipPresence + (1f - settings.MinimumShipPresence) * normalized;
        }

        private static float CalculateTransmission(Vector3D sourcePosition)
        {
            return ExteriorSoundTransmission.Calculate(sourcePosition);
        }

        private static float SmoothMultiplier(MyEntity3DSoundEmitter emitter, float targetMultiplier)
        {
            var settings = SettingsManager.Current;
            if (settings.SpatialSmoothingMs <= 0f)
                return targetMultiplier;

            DateTime now = DateTime.UtcNow;
            if (!SmoothStatesByEmitter.TryGetValue(emitter, out SmoothState state))
            {
                SmoothStatesByEmitter[emitter] = new SmoothState(targetMultiplier, now);
                return targetMultiplier;
            }

            double elapsedMs = Math.Max(0.0, (now - state.LastUpdateUtc).TotalMilliseconds);
            state.LastUpdateUtc = now;

            if (elapsedMs <= 0.0)
            {
                SmoothStatesByEmitter[emitter] = state;
                return state.Value;
            }

            float factor = Clamp01((float)(1.0 - Math.Exp(-elapsedMs / settings.SpatialSmoothingMs)));
            state.Value += (targetMultiplier - state.Value) * factor;

            if (targetMultiplier <= 0f && state.Value < 0.0001f)
                state.Value = 0f;

            SmoothStatesByEmitter[emitter] = state;
            return state.Value;
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

        private static float SmoothStep(float value)
        {
            float x = Clamp01(value);
            return x * x * (3f - 2f * x);
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }

        private struct SmoothState
        {
            public float Value;
            public DateTime LastUpdateUtc;

            public SmoothState(float value, DateTime lastUpdateUtc)
            {
                Value = value;
                LastUpdateUtc = lastUpdateUtc;
            }
        }
    }
}
