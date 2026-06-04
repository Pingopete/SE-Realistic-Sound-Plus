using System;
using System.Collections.Generic;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal sealed class V2GridAudioState
    {
        private static readonly TimeSpan ContributionLifetime = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan DirectionUpdateInterval = TimeSpan.FromMilliseconds(50);
        private const float StartThreshold = 0.01f;
        private const float MaxLayerVolume = 1.0f;

        private readonly DirectionState[] _directions = new DirectionState[6];
        private DateTime _lastUpdateUtc = DateTime.MinValue;

        public V2GridAudioState(MyCubeGrid grid)
        {
            GridEntityId = grid != null ? grid.EntityId : 0L;
            for (int i = 0; i < _directions.Length; i++)
                _directions[i] = new DirectionState((V2ThrustDirectionGroup)i);
        }

        public long GridEntityId { get; }

        public void ReportThruster(MyThrust thruster, V2AudioListenerState listener)
        {
            if (thruster == null || thruster.CubeGrid == null)
                return;

            V2ThrustDirectionGroup direction = DirectionFromVector(thruster.GridThrustDirection);
            string detailCue = V2CueCatalog.SelectDetailCue(thruster);
            float load = CalculateLoad(thruster);
            if (load <= 0f)
            {
                _directions[(int)direction].Report(thruster, thruster.WorldMatrix.Translation, 0f, detailCue);
                return;
            }

            float maxForce = GetMaxForce(thruster);
            float presence = CalculateThrusterPresence(maxForce, SettingsManager.Current);
            float target = Clamp01(load * presence);
            _directions[(int)direction].Report(thruster, thruster.WorldMatrix.Translation, target, detailCue);
            Update(thruster.CubeGrid, listener);
        }

        public void Update(MyCubeGrid grid, V2AudioListenerState listener)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastUpdateUtc < DirectionUpdateInterval)
                return;

            _lastUpdateUtc = now;
            string stateCue = V2CueCatalog.SelectStateLoopCue(grid);
            for (int i = 0; i < _directions.Length; i++)
                _directions[i].Update(grid, listener, stateCue, now);
        }

        public void Stop()
        {
            for (int i = 0; i < _directions.Length; i++)
                _directions[i].Stop();
        }

        public bool IsEmpty(DateTime now)
        {
            for (int i = 0; i < _directions.Length; i++)
            {
                if (!_directions[i].IsStale(now))
                    return false;
            }

            return true;
        }

        public int CountActiveDetailSources()
        {
            int count = 0;
            for (int i = 0; i < _directions.Length; i++)
            {
                if (_directions[i].DetailActive)
                    count++;
            }

            return count;
        }

        public int CountActiveStateSources()
        {
            int count = 0;
            for (int i = 0; i < _directions.Length; i++)
            {
                if (_directions[i].StateActive)
                    count++;
            }

            return count;
        }

        public void DrawDebugMarkers()
        {
            for (int i = 0; i < _directions.Length; i++)
                _directions[i].DrawDebugMarkers();
        }

        private static float CalculateLoad(MyThrust thruster)
        {
            if (thruster == null || !thruster.IsWorking)
                return 0f;

            float currentStrength = Clamp01(thruster.CurrentStrength);
            if (currentStrength > 0.001f)
                return currentStrength;

            float maxForce = GetMaxForce(thruster);
            if (maxForce <= 0f)
                return 0f;

            return Clamp01(thruster.ThrustForceLength / maxForce);
        }

        private static float GetMaxForce(MyThrust thruster)
        {
            float maxForce = thruster?.BlockDefinition != null ? thruster.BlockDefinition.ForceMagnitude : 0f;
            if (maxForce <= 0f && thruster != null)
                maxForce = Math.Max(thruster.ThrustForceLength, 1f);

            return Math.Max(maxForce, 1f);
        }

        private static float CalculateThrusterPresence(float maxForce, RealisticSoundPlusSettings settings)
        {
            float forceLog = (float)Math.Log10(Math.Max(maxForce, 1f));
            float normalized = Clamp01((forceLog - settings.QuietShipForceLog10) / (settings.LoudShipForceLog10 - settings.QuietShipForceLog10));
            return settings.MinimumShipPresence + (1f - settings.MinimumShipPresence) * normalized;
        }

        private static V2ThrustDirectionGroup DirectionFromVector(Vector3I direction)
        {
            if (Math.Abs(direction.X) >= Math.Abs(direction.Y) && Math.Abs(direction.X) >= Math.Abs(direction.Z))
                return direction.X >= 0 ? V2ThrustDirectionGroup.Right : V2ThrustDirectionGroup.Left;

            if (Math.Abs(direction.Y) >= Math.Abs(direction.Z))
                return direction.Y >= 0 ? V2ThrustDirectionGroup.Up : V2ThrustDirectionGroup.Down;

            return direction.Z >= 0 ? V2ThrustDirectionGroup.Backward : V2ThrustDirectionGroup.Forward;
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

        private static float Clamp(float value, float min, float max)
        {
            if (value <= min)
                return min;

            return value >= max ? max : value;
        }

        private sealed class DirectionState
        {
            private readonly Dictionary<MyThrust, Contribution> _contributors = new Dictionary<MyThrust, Contribution>();
            private readonly V2ThrustDirectionGroup _direction;
            private LayerEmitter _detail;
            private LayerEmitter _state;
            private float _detailValue;
            private float _stateValue;
            private DateTime _lastDetailUpdateUtc = DateTime.UtcNow;
            private DateTime _lastStateUpdateUtc = DateTime.UtcNow;
            private Vector3D _lastPosition;

            public DirectionState(V2ThrustDirectionGroup direction)
            {
                _direction = direction;
            }

            public bool DetailActive => _detail != null && _detail.IsPlaying;

            public bool StateActive => _state != null && _state.IsPlaying;

            public void Report(MyThrust thruster, Vector3D position, float target, string detailCue)
            {
                _contributors[thruster] = new Contribution
                {
                    Anchor = thruster,
                    Position = position,
                    Target = target,
                    DetailCue = detailCue,
                    UpdatedUtc = DateTime.UtcNow
                };
            }

            public void Update(MyCubeGrid grid, V2AudioListenerState listener, string stateCue, DateTime now)
            {
                DirectionSnapshot snapshot = BuildSnapshot(grid, now);
                _lastPosition = snapshot.Position;

                RealisticSoundPlusSettings settings = SettingsManager.Current;
                float distanceGain = CalculateDistanceGain(listener, snapshot.Position, settings);
                float detailTarget = settings.V2DetailEnabled
                    ? Clamp(snapshot.Target * settings.EngineGain * settings.V2DetailGain * distanceGain, 0f, MaxLayerVolume)
                    : 0f;
                float stateTarget = settings.V2StateEnabled
                    ? Clamp((float)Math.Sqrt(snapshot.Target) * settings.V2StateGain * distanceGain, 0f, MaxLayerVolume)
                    : 0f;

                detailTarget *= SmoothStep(snapshot.Target / settings.V2SoftFadeRatio);
                stateTarget *= SmoothStep(snapshot.Target / settings.V2SoftFadeRatio);

                UpdateLayer(ref _detail, ref _detailValue, ref _lastDetailUpdateUtc, snapshot.Anchor, snapshot.Position, snapshot.DetailCue, detailTarget, V2AudioLayer.Detail, false, false);
                bool state2D = listener.InsideShip;
                bool state2DPositional = state2D && settings.V2State2DPositionalTest;
                UpdateLayer(ref _state, ref _stateValue, ref _lastStateUpdateUtc, snapshot.Anchor, snapshot.Position, stateCue, stateTarget, V2AudioLayer.State, state2D, state2DPositional);
            }

            public void Stop()
            {
                _detailValue = 0f;
                _stateValue = 0f;
                _detail?.Stop();
                _state?.Stop();
            }

            public bool IsStale(DateTime now)
            {
                foreach (Contribution contribution in _contributors.Values)
                {
                    if (now - contribution.UpdatedUtc <= ContributionLifetime)
                        return false;
                }

                return true;
            }

            public void DrawDebugMarkers()
            {
                if (_detail != null && _detail.IsPlaying)
                    DrawMarker(_detail.Position, new Color(90, 220, 255, 220), 0.7f);

                if (_state != null && _state.IsPlaying)
                    DrawMarker(_state.Position, new Color(255, 180, 80, 230), 1.05f);
            }

            private DirectionSnapshot BuildSnapshot(MyCubeGrid grid, DateTime now)
            {
                Vector3D weightedPosition = Vector3D.Zero;
                float totalWeight = 0f;
                float strongestTarget = 0f;
                MyThrust strongestThruster = null;
                string strongestDetailCue = null;
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
                        strongestThruster = contribution.Anchor;
                        strongestDetailCue = contribution.DetailCue;
                    }
                }

                if (stale != null)
                {
                    foreach (MyThrust thruster in stale)
                        _contributors.Remove(thruster);
                }

                return new DirectionSnapshot
                {
                    Anchor = strongestThruster,
                    Position = totalWeight > 0f ? weightedPosition / totalWeight : (grid?.WorldMatrix.Translation ?? _lastPosition),
                    Target = strongestTarget,
                    DetailCue = strongestDetailCue
                };
            }

            private void UpdateLayer(ref LayerEmitter emitter, ref float value, ref DateTime lastUpdateUtc, MyThrust anchor, Vector3D position, string cueName, float target, V2AudioLayer layer, bool force2D, bool force2DPositional)
            {
                value = Smooth(value, target, ref lastUpdateUtc, SettingsManager.Current.V2SmoothingMs);
                if (value <= StartThreshold || anchor == null || string.IsNullOrWhiteSpace(cueName))
                {
                    emitter?.SetVolume(0f);
                    emitter?.Stop();
                    return;
                }

                if (emitter == null || emitter.Anchor != anchor)
                {
                    emitter?.Stop();
                    emitter = new LayerEmitter(anchor, layer, _direction);
                }

                bool force3D = !force2D || force2DPositional;
                bool skipFilter = force2D;
                emitter.Update(position, cueName, value, force2D, force3D, skipFilter);
                AudioDiagnostics.RecordEmitter(emitter.Emitter, emitter.RouteName, value, ExteriorSoundTransmission.Calculate(position), target, value, position);
            }

            private static float CalculateDistanceGain(V2AudioListenerState listener, Vector3D position, RealisticSoundPlusSettings settings)
            {
                if (listener.Position == Vector3D.Zero || position == Vector3D.Zero)
                    return 1f;

                float distance = (float)Vector3D.Distance(listener.Position, position);
                return SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.V2EmitterDistance, settings.V2DistanceCurve);
            }

            private static float Smooth(float current, float target, ref DateTime lastUpdateUtc, float smoothingMs)
            {
                if (smoothingMs <= 0f)
                {
                    lastUpdateUtc = DateTime.UtcNow;
                    return target;
                }

                DateTime now = DateTime.UtcNow;
                double elapsedMs = Math.Max(0.0, (now - lastUpdateUtc).TotalMilliseconds);
                lastUpdateUtc = now;
                float factor = elapsedMs <= 0.0 ? 0f : Clamp01((float)(1.0 - Math.Exp(-elapsedMs / smoothingMs)));
                current += (target - current) * factor;

                if (target <= 0f && current < 0.0001f)
                    current = 0f;

                return current;
            }

            private static void DrawMarker(Vector3D position, Color color, float radius)
            {
                if (position == Vector3D.Zero)
                    return;

                MyRenderProxy.DebugDrawSphere(position, radius, color, 0.85f, false, false, false, false);
            }
        }

        private sealed class LayerEmitter
        {
            private string _cueName;
            private bool _force2D;
            private bool _force3D;

            public LayerEmitter(MyThrust anchor, V2AudioLayer layer, V2ThrustDirectionGroup direction)
            {
                Anchor = anchor;
                Layer = layer;
                Direction = direction;
                Emitter = new MyEntity3DSoundEmitter(anchor, false);
                Emitter.VolumeMultiplier = 0f;
                AudioEngineV2Runtime.RegisterEmitter(Emitter, false);
            }

            public MyThrust Anchor { get; }

            public V2AudioLayer Layer { get; }

            public V2ThrustDirectionGroup Direction { get; }

            public MyEntity3DSoundEmitter Emitter { get; }

            public bool IsPlaying { get; private set; }

            public Vector3D Position { get; private set; }

            public string RouteName
            {
                get
                {
                    return Layer == V2AudioLayer.Detail
                        ? "v2-detail-" + Direction
                        : "v2-state-" + Direction;
                }
            }

            public void Update(Vector3D position, string cueName, float volume, bool force2D, bool force3D, bool skipFilter)
            {
                Position = position;
                Emitter.SetPosition(position);
                Emitter.Force2D = force2D;
                Emitter.Force3D = force3D;
                AudioEngineV2Runtime.RegisterEmitter(Emitter, skipFilter);

                if (!IsPlaying
                    || !string.Equals(_cueName, cueName, StringComparison.OrdinalIgnoreCase)
                    || _force2D != force2D
                    || _force3D != force3D)
                {
                    _cueName = cueName;
                    _force2D = force2D;
                    _force3D = force3D;
                    Emitter.PlaySound(new MySoundPair(cueName, false), true, false, force2D, true, false, force3D, true);
                    IsPlaying = true;
                }

                SetVolume(volume);
            }

            public void SetVolume(float volume)
            {
                Emitter.VolumeMultiplier = Clamp(volume, 0f, MaxLayerVolume);
                Emitter.Update();
                Emitter.FastUpdate(false);
            }

            public void Stop()
            {
                if (!IsPlaying)
                    return;

                try
                {
                    Emitter.VolumeMultiplier = 0f;
                    Emitter.StopSound(false, false, false);
                    AudioEngineV2Runtime.UnregisterEmitter(Emitter);
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[RealisticSoundPlus] V2 emitter stop failed: " + ex.Message);
                }
                finally
                {
                    IsPlaying = false;
                    _cueName = null;
                }
            }
        }

        private enum V2AudioLayer
        {
            Detail,
            State
        }

        private struct DirectionSnapshot
        {
            public MyThrust Anchor;
            public Vector3D Position;
            public float Target;
            public string DetailCue;
        }

        private struct Contribution
        {
            public MyThrust Anchor;
            public Vector3D Position;
            public float Target;
            public string DetailCue;
            public DateTime UpdatedUtc;
        }
    }
}
