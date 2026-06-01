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
        private static readonly FieldInfo ShipCurrentPowerField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shipCurrentPower");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> BaseVolumeByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
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
                MyCubeGrid grid = (MyCubeGrid)ShipGridField.GetValue(__instance);
                ExteriorSoundTransmission.ReportListenerInsideShip(insideShip);
                ApplySpeedAmbientWind(__instance, grid, emitters);

                bool suppressVanillaThrusterLayer = SpatialThrusterAudioPatch.CanOwnVanillaShipLayer();
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
                    ApplyVanillaEngineLayerTransmission(emitter, suppressVanillaThrusterLayer, listenerPosition, grid);
                }

                if (suppressVanillaThrusterLayer)
                    SuppressKnownVanillaEngineCues(emitters, grid);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Vanilla ship thruster layer suppression/transmission muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling interior muffling patch after error: " + ex);
            }
        }


        public static void ResetRuntimeState()
        {
            BaseVolumeByEmitter.Clear();
            SpeedAmbientBaseVolumeByEmitter.Clear();
            KnownThrusterEmitters.Clear();
            _disabled = false;
            _patchHits = 0;
            _speedAmbientPatchHits = 0;
        }
        private static void SuppressKnownVanillaEngineCues(MyEntity3DSoundEmitter[] emitters, MyCubeGrid grid)
        {
            foreach (MyEntity3DSoundEmitter emitter in emitters)
            {
                if (emitter == null || !IsKnownVanillaEngineCue(emitter))
                    continue;

                KnownThrusterEmitters.Add(emitter);
                ApplyVanillaEngineLayerTransmission(emitter, true, Vector3D.Zero, grid);
            }
        }

        private static bool IsKnownVanillaEngineCue(MyEntity3DSoundEmitter emitter)
        {
            return EngineAudioClassifier.IsKnownEngineCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownEngineCue(emitter.Sound?.CueEnum)
                || EngineAudioClassifier.IsKnownEngineCue(emitter.SecondarySound?.CueEnum);
        }

        private static void ApplyVanillaEngineLayerTransmission(MyEntity3DSoundEmitter emitter, bool suppress, Vector3D listenerPosition, MyCubeGrid grid)
        {
            float baseVolume = RestoreEmitter(emitter);
            float transmission = suppress
                ? CalculateHullVacuumStructuralTransmission(grid)
                : ExteriorSoundTransmission.Calculate(listenerPosition, emitter.SourcePosition);            float finalMultiplier = baseVolume * transmission;
            emitter.VolumeMultiplier = finalMultiplier;
            AudioDiagnostics.RecordEmitter(emitter, suppress ? "vanSup" : "vanTr", baseVolume, transmission, 1f, transmission, finalMultiplier, emitter.SourcePosition, 0f);
            BaseVolumeByEmitter[emitter] = baseVolume;
        }

        private static float CalculateHullVacuumStructuralTransmission(MyCubeGrid grid)
        {
            if (ExteriorSoundTransmission.IsListenerInsideShip() || !IsGridInVacuum(grid) || !IsListenerNearGrid(grid))
                return 0f;

            return SettingsManager.Current.HullVacuumTransmission;
        }

        private static bool IsListenerNearGrid(MyCubeGrid grid)
        {
            if (grid == null)
                return false;

            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (listenerPosition == Vector3D.Zero)
                return false;

            BoundingBoxD box = grid.PositionComp.WorldAABB;
            box.Inflate(8.0);
            return box.Contains(listenerPosition) != ContainmentType.Disjoint;
        }

        private static void ApplySpeedAmbientWind(MyShipSoundComponent component, MyCubeGrid grid, MyEntity3DSoundEmitter[] emitters)
        {
            float windScale = CalculateAtmosphericSpeedScale(grid);
            bool inVacuum = IsGridInVacuum(grid);
            bool lowSpeed = IsGridBelowSpeedAmbientThreshold(grid);
            bool hasEnginePower = GetShipCurrentPower(component) > 0.02f;
            bool controlSpeedAmbient = SettingsManager.Current.AmbientMufflingEnabled || inVacuum;
            AudioDiagnostics.UpdateGlobal(grid, windScale, hasEnginePower, inVacuum, lowSpeed);

            foreach (MyEntity3DSoundEmitter emitter in emitters)
            {
                if (!ThrusterFilterPatch.IsSpeedAmbientAudioEmitter(emitter))
                    continue;

                bool movingSpeedLoop = IsMovingSpeedAmbientEmitter(emitter);
                bool engineAdjacentSpeedLoop = IsEngineAdjacentSpeedAmbientEmitter(emitter);
                bool suppressStuckMotionLoop = movingSpeedLoop && !hasEnginePower && (lowSpeed || engineAdjacentSpeedLoop);
                bool routeAsPoweredEngineLoop = hasEnginePower && engineAdjacentSpeedLoop;
                if (routeAsPoweredEngineLoop)
                {
                    float engineLoopBaseVolume = RestoreSpeedAmbientEmitter(emitter);
                    float engineTransmission = ExteriorSoundTransmission.Calculate(emitter.SourcePosition);
                    float atmosphereGainScale = ExteriorSoundTransmission.CalculateAtmosphericEngineGainScale(emitter.SourcePosition);
                    float poweredLoopFloor = Math.Max(engineLoopBaseVolume, SettingsManager.Current.MinimumShipPresence);                    float engineSpeedFinalMultiplier = poweredLoopFloor * engineTransmission * atmosphereGainScale;
                    emitter.VolumeMultiplier = engineSpeedFinalMultiplier;
                    AudioDiagnostics.RecordEmitter(emitter, "engSpd", engineLoopBaseVolume, engineTransmission, atmosphereGainScale, poweredLoopFloor, engineSpeedFinalMultiplier, emitter.SourcePosition, windScale);
                    SpeedAmbientBaseVolumeByEmitter[emitter] = engineLoopBaseVolume;
                    continue;
                }
                if (!controlSpeedAmbient && !suppressStuckMotionLoop)
                {
                    RestoreSpeedAmbientEmitter(emitter);
                    continue;
                }

                float baseVolume = RestoreSpeedAmbientEmitter(emitter);                float windFinalMultiplier = baseVolume * windScale;
                emitter.VolumeMultiplier = windFinalMultiplier;
                AudioDiagnostics.RecordEmitter(emitter, suppressStuckMotionLoop ? "supSpd" : "wind", baseVolume, windScale, 1f, windScale, windFinalMultiplier, emitter.SourcePosition, windScale);
                SpeedAmbientBaseVolumeByEmitter[emitter] = baseVolume;
            }

            if (++_speedAmbientPatchHits == 1)
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Speed ambient wind volume is controlled by ship speed and atmospheric density; vacuum and near-stationary movement speed loops are suppressed.");
        }

        private static float GetShipCurrentPower(MyShipSoundComponent component)
        {
            try
            {
                return component != null ? (float)ShipCurrentPowerField.GetValue(component) : 0f;
            }
            catch
            {
                return 0f;
            }
        }
        private static bool IsEngineAdjacentSpeedAmbientEmitter(MyEntity3DSoundEmitter emitter)
        {
            return EngineAudioClassifier.IsKnownEngineAdjacentSpeedCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownEngineAdjacentSpeedCue(emitter.Sound?.CueEnum)
                || EngineAudioClassifier.IsKnownEngineAdjacentSpeedCue(emitter.SecondarySound?.CueEnum);
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
            if (!BaseVolumeByEmitter.TryGetValue(emitter, out float baseVolume))
                return volume;

            BaseVolumeByEmitter.Remove(emitter);
            emitter.VolumeMultiplier = baseVolume;
            return baseVolume;
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