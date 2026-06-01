using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
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
                AudioDiagnostics.UpdateGlobal(insideShip);
                ApplySpeedAmbientWind(__instance, emitters);

                if (!insideShip)
                {
                    RestoreEmitters(emitters);
                    return;
                }

                Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (listenerPosition == Vector3D.Zero)
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
                    float transmission = SettingsManager.Current.SpatialAudioEnabled
                        ? 0f
                        : ExteriorSoundTransmission.Calculate(listenerPosition, emitter.SourcePosition);
                    float finalMultiplier = baseVolume * transmission;
                    emitter.VolumeMultiplier = finalMultiplier;
                    AudioDiagnostics.RecordEmitter(emitter, "interior", baseVolume, transmission, transmission, finalMultiplier, emitter.SourcePosition);
                    LastTransmissionByEmitter[emitter] = transmission;
                }

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Interior thruster transmission muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling interior muffling patch after error: " + ex);
            }
        }

        public static void ResetRuntimeState()
        {
            LastTransmissionByEmitter.Clear();
            SpeedAmbientBaseVolumeByEmitter.Clear();
            KnownThrusterEmitters.Clear();
            _disabled = false;
            _patchHits = 0;
            _speedAmbientPatchHits = 0;
        }

        private static void ApplySpeedAmbientWind(MyShipSoundComponent component, MyEntity3DSoundEmitter[] emitters)
        {
            MyCubeGrid grid = (MyCubeGrid)ShipGridField.GetValue(component);
            float windScale = CalculateAtmosphericSpeedScale(grid);
            bool inVacuum = IsGridInVacuum(grid);
            bool lowSpeed = IsGridBelowSpeedAmbientThreshold(grid);
            bool controlSpeedAmbient = SettingsManager.Current.AmbientMufflingEnabled || inVacuum;

            foreach (MyEntity3DSoundEmitter emitter in emitters)
            {
                if (!ThrusterFilterPatch.IsSpeedAmbientAudioEmitter(emitter))
                    continue;

                bool suppressStuckMotionLoop = lowSpeed && IsMovingSpeedAmbientEmitter(emitter);
                if (!controlSpeedAmbient && !suppressStuckMotionLoop)
                {
                    RestoreSpeedAmbientEmitter(emitter);
                    continue;
                }

                float baseVolume = RestoreSpeedAmbientEmitter(emitter);
                float finalMultiplier = baseVolume * windScale;
                emitter.VolumeMultiplier = finalMultiplier;
                SpeedAmbientBaseVolumeByEmitter[emitter] = baseVolume;
                AudioDiagnostics.RecordEmitter(emitter, "speedwind", baseVolume, windScale, windScale, finalMultiplier, emitter.SourcePosition);
            }

            if (++_speedAmbientPatchHits == 1)
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Speed ambient wind volume is controlled by ship speed and atmospheric density; vacuum and near-stationary movement speed loops are suppressed.");
        }

        private static bool IsMovingSpeedAmbientEmitter(MyEntity3DSoundEmitter emitter)
        {
            return IsMovingSpeedAmbientCue(emitter.SoundId.ToString())
                || IsMovingSpeedAmbientCue(emitter.Sound?.CueEnum.ToString())
                || IsMovingSpeedAmbientCue(emitter.SecondarySound?.CueEnum.ToString());
        }

        private static bool IsMovingSpeedAmbientCue(string cueName)
        {
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            return cueName.Equals("ArcShipWindSpeed", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeEngine", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallEngine", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeSpeedDown", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeSpeedUp", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunSlow", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunMedium", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunFast", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallSpeedDown", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallSpeedUp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGridBelowSpeedAmbientThreshold(MyCubeGrid grid)
        {
            if (grid == null || grid.Physics == null)
                return true;

            return grid.Physics.LinearVelocity.LengthSquared() < 4.0;
        }

        private static bool IsGridInVacuum(MyCubeGrid grid)
        {
            if (grid == null)
                return true;

            return ExteriorSoundTransmission.GetAtmosphericPressure(grid.WorldMatrix.Translation) < 0.01f;
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
                    float maxSpeed = IsSmallGrid(grid)
                        ? environment.SmallShipMaxSpeed
                        : environment.LargeShipMaxSpeed;

                    return Math.Max(maxSpeed, 1f);
                }
            }
            catch
            {
            }

            return IsSmallGrid(grid) ? 100f : 150f;
        }

        private static bool IsSmallGrid(MyCubeGrid grid)
        {
            return grid != null && string.Equals(grid.GridSizeEnum.ToString(), "Small", StringComparison.OrdinalIgnoreCase);
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