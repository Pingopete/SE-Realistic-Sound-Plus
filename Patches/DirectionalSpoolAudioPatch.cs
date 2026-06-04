using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class DirectionalSpoolAudioPatch
    {
        private const float StartThreshold = 0.02f;
        private const float TransitionUpDelta = 0.12f;
        private const float TransitionDownDelta = 0.10f;
        private const float MaxSpoolVolumeMultiplier = 1f;
        private const float LoopVolumeScale = 0.5f;
        private const float SpeedUpVolumeScale = 0.35f;
        private const float SpeedDownVolumeScale = 0.03f;
        private const float MaxTransitionVolumeMultiplier = 0.35f;
        private static readonly TimeSpan ContributionLifetime = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan DirectionUpdateInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan TransitionCooldown = TimeSpan.FromMilliseconds(650);
        private static readonly FieldInfo ThrustComponentField = AccessTools.Field(typeof(MyThrust), "m_thrustComponent");
        private static readonly FieldInfo MaxPositiveThrustField = AccessTools.Field(typeof(MyEntityThrustComponent), "m_totalMaxPositiveThrust");
        private static readonly FieldInfo MaxNegativeThrustField = AccessTools.Field(typeof(MyEntityThrustComponent), "m_totalMaxNegativeThrust");
        private static readonly Dictionary<long, GridSpoolState> StatesByGrid = new Dictionary<long, GridSpoolState>();
        private static readonly HashSet<MyEntity3DSoundEmitter> DirectionalSpoolEmitters = new HashSet<MyEntity3DSoundEmitter>();

        private static bool _disabled;
        private static bool _preloaded;
        private static int _patchHits;

        [HarmonyPatch(typeof(MyThrust), "UpdateAfterSimulation")]
        [HarmonyPostfix]
        private static void AfterSimulation(MyThrust __instance)
        {
            Apply(__instance);
        }

        public static void ResetRuntimeState()
        {
            StopAll();
            StatesByGrid.Clear();
            DirectionalSpoolEmitters.Clear();
            _disabled = false;
            _preloaded = false;
            _patchHits = 0;
        }

        public static bool IsDirectionalSpoolEmitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && DirectionalSpoolEmitters.Contains(emitter);
        }

        private static void Apply(MyThrust thruster)
        {
            if (_disabled || thruster == null || thruster.CubeGrid == null)
                return;

            try
            {
                if (!SettingsManager.Current.SpatialAudioEnabled || !SettingsManager.Current.DirectionalSpoolEnabled)
                {
                    StopAll();
                    return;
                }

                PreloadCuesOnce();

                MyCubeGrid grid = thruster.CubeGrid;
                GridSpoolState gridState = GetOrCreateState(grid);
                int directionIndex = DirectionIndex(thruster.GridThrustDirection);
                Vector3D sourcePosition = thruster.WorldMatrix.Translation;
                float target = CalculateSpoolTarget(thruster, directionIndex, sourcePosition);

                gridState.UpdateContribution(directionIndex, thruster, sourcePosition, target);
                gridState.UpdateAll(grid, SelectCueSet(grid));

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Directional ship spool audio is active with one loop plus transition cues for each thrust direction.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                StopAll();
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling directional spool audio patch after error: " + ex);
            }
        }

        private static GridSpoolState GetOrCreateState(MyCubeGrid grid)
        {
            long id = grid.EntityId;
            if (StatesByGrid.TryGetValue(id, out GridSpoolState state))
                return state;

            state = new GridSpoolState(grid);
            StatesByGrid[id] = state;
            return state;
        }

        private static void StopAll()
        {
            foreach (GridSpoolState state in StatesByGrid.Values)
                state.Stop();
        }

        private static void PreloadCuesOnce()
        {
            if (_preloaded)
                return;

            _preloaded = true;
            Preload("ShipLargeRunLoop");
            Preload("ShipLargeSpeedUp");
            Preload("ShipLargeSpeedDown");
            Preload("ShipSmallRunLoop");
            Preload("ShipSmallSpeedUp");
            Preload("ShipSmallSpeedDown");
        }

        private static void Preload(string cueName)
        {
            try
            {
                MyEntity3DSoundEmitter.PreloadSound(new MySoundPair(cueName, false));
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[RealisticSoundPlus] Failed to preload directional spool cue " + cueName + ": " + ex.Message);
            }
        }

        private static float CalculateSpoolTarget(MyThrust thruster, int directionIndex, Vector3D sourcePosition)
        {
            if (!thruster.IsWorking)
                return 0f;

            float maxForce = thruster.BlockDefinition != null ? thruster.BlockDefinition.ForceMagnitude : 0f;
            if (maxForce <= 0f)
                maxForce = Math.Max(thruster.ThrustForceLength, 1f);

            float thrustRatio = Clamp01(thruster.CurrentStrength);
            if (thrustRatio <= 0.001f)
                thrustRatio = CalculateDirectionalGridLoad(thruster, directionIndex);
            if (thrustRatio <= 0f)
                return 0f;

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float softSpool = (float)Math.Sqrt(thrustRatio);
            float forcePresence = CalculateThrusterPresence(maxForce, settings);
            float transmission = ExteriorSoundTransmission.Calculate(sourcePosition);
            return Clamp(softSpool * forcePresence * settings.DirectionalSpoolGain * transmission * LoopVolumeScale, 0f, MaxSpoolVolumeMultiplier);
        }

        private static float CalculateDirectionalGridLoad(MyThrust thruster, int directionIndex)
        {
            var component = (MyEntityThrustComponent)ThrustComponentField.GetValue(thruster);
            if (component == null)
                return 0f;

            Vector3 finalThrust = component.FinalThrust;
            Vector3 maxPositive = (Vector3)MaxPositiveThrustField.GetValue(component);
            Vector3 maxNegative = (Vector3)MaxNegativeThrustField.GetValue(component);

            float active;
            float maximum;
            switch (directionIndex)
            {
                case 0:
                    active = Math.Max(0f, finalThrust.X);
                    maximum = Math.Abs(maxPositive.X);
                    break;
                case 1:
                    active = Math.Max(0f, -finalThrust.X);
                    maximum = Math.Abs(maxNegative.X);
                    break;
                case 2:
                    active = Math.Max(0f, finalThrust.Y);
                    maximum = Math.Abs(maxPositive.Y);
                    break;
                case 3:
                    active = Math.Max(0f, -finalThrust.Y);
                    maximum = Math.Abs(maxNegative.Y);
                    break;
                case 4:
                    active = Math.Max(0f, finalThrust.Z);
                    maximum = Math.Abs(maxPositive.Z);
                    break;
                default:
                    active = Math.Max(0f, -finalThrust.Z);
                    maximum = Math.Abs(maxNegative.Z);
                    break;
            }

            return maximum > 0f ? Clamp01(active / maximum) : 0f;
        }

        private static float CalculateThrusterPresence(float maxForce, RealisticSoundPlusSettings settings)
        {
            float forceLog = (float)Math.Log10(Math.Max(maxForce, 1f));
            float normalized = Clamp01((forceLog - settings.QuietShipForceLog10) / (settings.LoudShipForceLog10 - settings.QuietShipForceLog10));
            return settings.MinimumShipPresence + (1f - settings.MinimumShipPresence) * normalized;
        }

        private static CueSet SelectCueSet(MyCubeGrid grid)
        {
            bool smallGrid = grid != null && string.Equals(grid.GridSizeEnum.ToString(), "Small", StringComparison.OrdinalIgnoreCase);
            return smallGrid
                ? new CueSet("ShipSmallRunLoop", "ShipSmallSpeedUp", "ShipSmallSpeedDown")
                : new CueSet("ShipLargeRunLoop", "ShipLargeSpeedUp", "ShipLargeSpeedDown");
        }

        private static int DirectionIndex(Vector3I direction)
        {
            if (Math.Abs(direction.X) >= Math.Abs(direction.Y) && Math.Abs(direction.X) >= Math.Abs(direction.Z))
                return direction.X >= 0 ? 0 : 1;

            if (Math.Abs(direction.Y) >= Math.Abs(direction.Z))
                return direction.Y >= 0 ? 2 : 3;

            return direction.Z >= 0 ? 4 : 5;
        }

        private static float Clamp01(float value)
        {
            return Clamp(value, 0f, 1f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value <= min)
                return min;

            return value >= max ? max : value;
        }

        private struct CueSet
        {
            public readonly string Loop;
            public readonly string SpeedUp;
            public readonly string SpeedDown;

            public CueSet(string loop, string speedUp, string speedDown)
            {
                Loop = loop;
                SpeedUp = speedUp;
                SpeedDown = speedDown;
            }
        }

        private sealed class GridSpoolState
        {
            private readonly DirectionState[] _directions = new DirectionState[6];
            private DateTime _lastUpdateUtc = DateTime.MinValue;

            public GridSpoolState(MyCubeGrid grid)
            {
                for (int i = 0; i < _directions.Length; i++)
                    _directions[i] = new DirectionState(grid, i);
            }

            public void UpdateContribution(int directionIndex, MyThrust thruster, Vector3D position, float target)
            {
                _directions[directionIndex].UpdateContribution(thruster, position, target);
            }

            public void UpdateAll(MyCubeGrid grid, CueSet cues)
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastUpdateUtc < DirectionUpdateInterval)
                    return;

                _lastUpdateUtc = now;
                for (int i = 0; i < _directions.Length; i++)
                    _directions[i].Update(grid, cues, now);
            }

            public void Stop()
            {
                for (int i = 0; i < _directions.Length; i++)
                    _directions[i].Stop();
            }
        }

        private sealed class DirectionState
        {
            private readonly Dictionary<MyThrust, Contribution> _contributors = new Dictionary<MyThrust, Contribution>();
            private readonly int _directionIndex;
            private MyEntity3DSoundEmitter _loopEmitter;
            private MyEntity3DSoundEmitter _transitionEmitter;
            private MyThrust _anchorThruster;
            private MyThrust _lastActiveThruster;
            private Vector3D _lastActivePosition;
            private DateTime _lastUpdateUtc = DateTime.UtcNow;
            private DateTime _lastTransitionUtc = DateTime.MinValue;
            private string _loopCue;
            private float _value;
            private float _lastTarget;
            private bool _isPlaying;

            public DirectionState(MyCubeGrid grid, int directionIndex)
            {
                _directionIndex = directionIndex;
            }

            public void UpdateContribution(MyThrust thruster, Vector3D position, float target)
            {
                _contributors[thruster] = new Contribution
                {
                    Position = position,
                    Target = target,
                    UpdatedUtc = DateTime.UtcNow
                };
            }

            public void Update(MyCubeGrid grid, CueSet cues, DateTime now)
            {
                Vector3D weightedPosition = Vector3D.Zero;
                float totalWeight = 0f;
                MyThrust strongestThruster = null;
                float strongestTarget = 0f;
                List<MyThrust> stale = null;

                foreach (KeyValuePair<MyThrust, Contribution> pair in _contributors)
                {
                    Contribution contribution = pair.Value;
                    if (now - contribution.UpdatedUtc > ContributionLifetime)
                    {
                        if (stale == null)
                            stale = new List<MyThrust>();
                        stale.Add(pair.Key);
                        continue;
                    }

                    if (contribution.Target <= 0f)
                        continue;

                    weightedPosition += contribution.Position * contribution.Target;
                    totalWeight += contribution.Target;
                    if (contribution.Target > strongestTarget)
                    {
                        strongestTarget = contribution.Target;
                        strongestThruster = pair.Key;
                    }
                }

                if (stale != null)
                {
                    foreach (MyThrust thruster in stale)
                        _contributors.Remove(thruster);
                }

                // A direction group represents thrust intensity, not the sum of every nozzle.
                // Summing all contributors made large ships hit the volume cap immediately,
                // which hid distance attenuation and made spoolgain appear ineffective.
                float target = Clamp(strongestTarget, 0f, MaxSpoolVolumeMultiplier);
                Vector3D sourcePosition = totalWeight > 0f
                    ? weightedPosition / totalWeight
                    : grid.WorldMatrix.Translation;
                float previousTarget = _lastTarget;
                float smoothed = Smooth(target, SettingsManager.Current.SpatialSmoothingMs);

                if (strongestThruster != null)
                {
                    _lastActiveThruster = strongestThruster;
                    _lastActivePosition = sourcePosition;
                }

                if (smoothed > StartThreshold && strongestThruster != null)
                {
                    EnsureEmitter(ref _loopEmitter, strongestThruster, sourcePosition);
                    EnsureLoop(cues.Loop);
                    SetLoopVolume(smoothed);
                    PlayTransitionIfNeeded(cues, previousTarget, target, sourcePosition, strongestThruster, now);
                }
                else
                {
                    if (previousTarget > StartThreshold)
                        PlayTransition(cues.SpeedDown, previousTarget, SpeedDownVolumeScale, _lastActivePosition, _lastActiveThruster, now);
                    StopLoop();
                }

                _lastTarget = target;
                RecordDiagnostics(cues.Loop, sourcePosition, target, smoothed, _loopEmitter);
            }

            public void Stop()
            {
                _value = 0f;
                _lastTarget = 0f;
                StopLoop();
                StopEmitter(_transitionEmitter);
                _transitionEmitter = null;
            }

            private void PlayTransitionIfNeeded(CueSet cues, float previousTarget, float target, Vector3D sourcePosition, MyThrust strongestThruster, DateTime now)
            {
                if (now - _lastTransitionUtc < TransitionCooldown)
                    return;

                if (target > StartThreshold && previousTarget <= StartThreshold)
                {
                    PlayTransition(cues.SpeedUp, target, SpeedUpVolumeScale, sourcePosition, strongestThruster, now);
                    return;
                }

                if (target - previousTarget >= TransitionUpDelta && previousTarget > StartThreshold)
                    PlayTransition(cues.SpeedUp, target - previousTarget, SpeedUpVolumeScale, sourcePosition, strongestThruster, now);
            }

            private void PlayTransition(string cueName, float volume, float transitionScale, Vector3D sourcePosition, MyThrust strongestThruster, DateTime now)
            {
                if (string.IsNullOrWhiteSpace(cueName) || strongestThruster == null || volume <= StartThreshold)
                    return;

                float transitionVolume = Clamp(volume * transitionScale, 0f, MaxTransitionVolumeMultiplier);
                if (transitionVolume <= 0.01f)
                    return;

                EnsureEmitter(ref _transitionEmitter, strongestThruster, sourcePosition);
                _transitionEmitter.VolumeMultiplier = transitionVolume;
                _transitionEmitter.PlaySoundWithDistance(new MyCueId(MyStringHash.GetOrCompute(cueName)), true, false, false, true, false, false, true);
                _transitionEmitter.Update();
                _transitionEmitter.FastUpdate(false);
                _lastTransitionUtc = now;
                Vector3D actualPosition = _transitionEmitter.SourcePosition;
                AudioDiagnostics.RecordVirtualCue("RSP-SpoolDir" + _directionIndex + " " + cueName, "dirspool-trans", _transitionEmitter.VolumeMultiplier, ExteriorSoundTransmission.Calculate(actualPosition), volume, transitionVolume, actualPosition);
            }

            private float Smooth(float target, float smoothingMs)
            {
                if (smoothingMs <= 0f)
                {
                    _value = target;
                    _lastUpdateUtc = DateTime.UtcNow;
                    return _value;
                }

                DateTime now = DateTime.UtcNow;
                double elapsedMs = Math.Max(0.0, (now - _lastUpdateUtc).TotalMilliseconds);
                _lastUpdateUtc = now;
                float factor = elapsedMs <= 0.0 ? 0f : Clamp01((float)(1.0 - Math.Exp(-elapsedMs / smoothingMs)));
                _value += (target - _value) * factor;

                if (target <= 0f && _value < 0.0001f)
                    _value = 0f;

                return _value;
            }

            private void EnsureEmitter(ref MyEntity3DSoundEmitter emitter, MyThrust anchorThruster, Vector3D position)
            {
                if (emitter == null || _anchorThruster != anchorThruster)
                {
                    StopEmitter(emitter);
                    emitter = new MyEntity3DSoundEmitter(anchorThruster, false);
                    emitter.Force2D = false;
                    emitter.Force3D = true;
                    emitter.VolumeMultiplier = 0f;
                    DirectionalSpoolEmitters.Add(emitter);
                    _anchorThruster = anchorThruster;
                }

                emitter.Force2D = false;
                emitter.Force3D = true;
                emitter.SetPosition(position);
                emitter.Update();
                emitter.FastUpdate(false);
            }

            private void EnsureLoop(string cueName)
            {
                if (_loopEmitter == null)
                    return;

                if (_isPlaying && string.Equals(_loopCue, cueName, StringComparison.OrdinalIgnoreCase))
                    return;

                _loopCue = cueName;
                _loopEmitter.PlaySoundWithDistance(new MyCueId(MyStringHash.GetOrCompute(cueName)), true, false, false, true, false, false, true);
                _isPlaying = true;
                _loopEmitter.Update();
                _loopEmitter.FastUpdate(false);
            }

            private void SetLoopVolume(float volume)
            {
                if (_loopEmitter == null)
                    return;

                _loopEmitter.VolumeMultiplier = Clamp(volume, 0f, MaxSpoolVolumeMultiplier);
                _loopEmitter.Update();
                _loopEmitter.FastUpdate(false);
            }

            private void StopLoop()
            {
                if (_loopEmitter != null)
                    _loopEmitter.VolumeMultiplier = 0f;

                if (!_isPlaying)
                    return;

                _isPlaying = false;
                try
                {
                    _loopEmitter?.StopSound(false, false, false);
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[RealisticSoundPlus] Directional spool loop stop failed: " + ex.Message);
                }
            }

            private static void StopEmitter(MyEntity3DSoundEmitter emitter)
            {
                if (emitter == null)
                    return;

                try
                {
                    emitter.VolumeMultiplier = 0f;
                    emitter.StopSound(false, false, false);
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[RealisticSoundPlus] Directional spool emitter stop failed: " + ex.Message);
                }
            }

            private void RecordDiagnostics(string loopCue, Vector3D sourcePosition, float target, float smoothed, MyEntity3DSoundEmitter emitter)
            {
                Vector3D actualPosition = emitter != null ? emitter.SourcePosition : sourcePosition;
                float volume = emitter != null ? emitter.VolumeMultiplier : 0f;
                float transmission = ExteriorSoundTransmission.Calculate(actualPosition);
                AudioDiagnostics.RecordVirtualCue("RSP-SpoolDir" + _directionIndex + " " + loopCue, "dirspool", volume, transmission, target, smoothed, actualPosition);
            }
        }

        private struct Contribution
        {
            public Vector3D Position;
            public float Target;
            public DateTime UpdatedUtc;
        }
    }
}