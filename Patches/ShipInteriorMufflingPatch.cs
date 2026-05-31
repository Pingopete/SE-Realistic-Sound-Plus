using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateVolumes")]
    internal static class ShipInteriorMufflingPatch
    {
        private static readonly int[] ThrusterEmitterIndexes =
        {
            2, 3, 4,
            5, 6, 7,
            12, 13,
            14, 15, 16
        };

        private static readonly FieldInfo EmittersField = AccessTools.Field(typeof(MyShipSoundComponent), "m_emitters");
        private static readonly FieldInfo InsideShipField = AccessTools.Field(typeof(MyShipSoundComponent), "m_insideShip");
        private static readonly FieldInfo ShipGridField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shipGrid");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> LastTransmissionByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> SpeedAmbientBaseVolumeByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownThrusterEmitters = new HashSet<MyEntity3DSoundEmitter>();

        private static bool _disabled;
        private static int _patchHits;
        private static int _speedAmbientPatchHits;

        private static void Postfix(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return;

            try
            {
                var emitters = (MyEntity3DSoundEmitter[])EmittersField.GetValue(__instance);
                if (emitters == null)
                    return;

                bool insideShip = (bool)InsideShipField.GetValue(__instance);
                ExteriorSoundTransmission.ReportListenerInsideShip(insideShip);
                ApplySpeedAmbientWind(__instance, emitters);

                bool suppressVanillaThrusterLayer = SettingsManager.Current.SpatialAudioEnabled;
                if (!insideShip && !suppressVanillaThrusterLayer)
                {
                    RestoreEmitters(emitters);
                    return;
                }

                Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (listenerPosition == Vector3D.Zero && !suppressVanillaThrusterLayer)
                    return;

                foreach (int index in ThrusterEmitterIndexes)
                {
                    if (index < 0 || index >= emitters.Length)
                        continue;

                    MyEntity3DSoundEmitter emitter = emitters[index];
                    if (emitter == null)
                        continue;

                    KnownThrusterEmitters.Add(emitter);
                    float baseVolume = RestoreEmitter(emitter);
                    float transmission = suppressVanillaThrusterLayer
                        ? 0f
                        : ExteriorSoundTransmission.Calculate(listenerPosition, emitter.SourcePosition);

                    emitter.VolumeMultiplier = baseVolume * transmission;
                    LastTransmissionByEmitter[emitter] = transmission;
                }

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Vanilla ship thruster layer suppression/transmission muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling interior muffling patch after error: " + ex);
            }
        }

        private static void ApplySpeedAmbientWind(MyShipSoundComponent component, MyEntity3DSoundEmitter[] emitters)
        {
            if (!SettingsManager.Current.AmbientMufflingEnabled)
            {
                RestoreSpeedAmbientEmitters(emitters);
                return;
            }

            MyCubeGrid grid = (MyCubeGrid)ShipGridField.GetValue(component);
            float windScale = CalculateAtmosphericSpeedScale(grid);

            foreach (MyEntity3DSoundEmitter emitter in emitters)
            {
                if (!ThrusterFilterPatch.IsSpeedAmbientAudioEmitter(emitter))
                    continue;

                float baseVolume = RestoreSpeedAmbientEmitter(emitter);
                emitter.VolumeMultiplier = baseVolume * windScale;
                SpeedAmbientBaseVolumeByEmitter[emitter] = baseVolume;
            }

            if (++_speedAmbientPatchHits == 1)
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Speed ambient wind volume is controlled by ship speed and atmospheric density.");
        }

        private static float CalculateAtmosphericSpeedScale(MyCubeGrid grid)
        {
            if (grid == null || grid.Physics == null)
                return 0f;

            float maxSpeed = GetWorldMaxShipSpeed(grid);
            float speed = (float)grid.Physics.LinearVelocity.Length();
            float speedScale = Clamp01(speed / maxSpeed);
            float pressure = ExteriorSoundTransmission.GetAtmosphericPressure(grid.WorldMatrix.Translation);
            return Clamp01(speedScale * pressure);
        }

        private static float GetWorldMaxShipSpeed(MyCubeGrid grid)
        {
            try
            {
                var environment = MyDefinitionManager.Static?.EnvironmentDefinition;
                if (environment != null)
                {
                    float maxSpeed = grid.GridSizeEnum == MyCubeSize.Small
                        ? environment.SmallShipMaxSpeed
                        : environment.LargeShipMaxSpeed;

                    return Math.Max(maxSpeed, 1f);
                }
            }
            catch
            {
            }

            return grid.GridSizeEnum == MyCubeSize.Small ? 100f : 150f;
        }

        private static void RestoreEmitters(MyEntity3DSoundEmitter[] emitters)
        {
            foreach (int index in ThrusterEmitterIndexes)
            {
                if (index < 0 || index >= emitters.Length)
                    continue;

                MyEntity3DSoundEmitter emitter = emitters[index];
                if (emitter != null)
                    RestoreEmitter(emitter);
            }
        }

        private static void RestoreSpeedAmbientEmitters(MyEntity3DSoundEmitter[] emitters)
        {
            foreach (MyEntity3DSoundEmitter emitter in emitters)
            {
                if (emitter != null)
                    RestoreSpeedAmbientEmitter(emitter);
            }
        }

        public static bool IsKnownThrusterEmitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && KnownThrusterEmitters.Contains(emitter);
        }

        private static float RestoreEmitter(MyEntity3DSoundEmitter emitter)
        {
            float volume = emitter.VolumeMultiplier;
            if (!LastTransmissionByEmitter.TryGetValue(emitter, out float previousTransmission))
                return volume;

            LastTransmissionByEmitter.Remove(emitter);
            if (previousTransmission <= 0f)
                return volume;

            float restored = volume / previousTransmission;
            emitter.VolumeMultiplier = restored;
            return restored;
        }

        private static float RestoreSpeedAmbientEmitter(MyEntity3DSoundEmitter emitter)
        {
            float volume = emitter.VolumeMultiplier;
            if (!SpeedAmbientBaseVolumeByEmitter.TryGetValue(emitter, out float baseVolume))
                return volume;

            SpeedAmbientBaseVolumeByEmitter.Remove(emitter);
            emitter.VolumeMultiplier = baseVolume;
            return baseVolume;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}