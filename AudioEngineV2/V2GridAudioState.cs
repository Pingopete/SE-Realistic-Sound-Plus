using System;
using System.Collections.Generic;
using System.Reflection;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using VRage.Audio;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal sealed class V2GridAudioState
    {
        private static readonly TimeSpan ContributionLifetime = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan KnownSourceLifetime = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DirectionUpdateInterval = TimeSpan.FromMilliseconds(50);
        private const float StartThreshold = 0.01f;
        private const float MaxLayerVolume = 16.0f;
        private const float VanillaFullSpeed = 96f;
        private const float DetailIdleInput = 0.035f;
        private const float DetailIdleFadeOutRange = 0.45f;
        private const float DetailIdleUnderActive = 0f;
        private const float DetailActivePitchMin = 0.5f;
        private const float DetailActivePitchMax = 1.5f;
        private const float StateIdleInput = 0.03f;
        private const float DetailActiveCueOnThreshold = 0.12f;
        private const float CurrentOutputAudibleThreshold = 0.015f;
        private const float CurrentOutputNonSeatThreshold = 0.08f;
        private const double DetailSilentReleaseMs = 1500.0;

        private readonly DirectionState[] _directions = new DirectionState[6];
        private DateTime _lastUpdateUtc = DateTime.MinValue;

        public V2GridAudioState(MyCubeGrid grid)
        {
            GridEntityId = grid != null ? grid.EntityId : 0L;
            for (int i = 0; i < _directions.Length; i++)
                _directions[i] = new DirectionState((V2ThrustDirectionGroup)i);
        }

        public long GridEntityId { get; }

        public void ReportThruster(MyThrust thruster, V2AudioListenerState listener, bool updateAudio)
        {
            if (thruster == null || thruster.CubeGrid == null)
                return;

            V2ThrustDirectionGroup direction = DirectionFromVector(thruster.GridThrustDirection);
            string activeDetailCue = V2CueCatalog.SelectDetailActiveCue(thruster);
            string idleDetailCue = V2CueCatalog.SelectDetailIdleCue(thruster);
            float maxForce = GetMaxForce(thruster);
            float presence = CalculateThrusterPresence(maxForce, SettingsManager.Current);
            DetailLoadSample load = CalculateCommandLoad(thruster, listener);
            if (load.Value <= 0f)
            {
                _directions[(int)direction].Report(thruster, thruster.WorldMatrix.Translation, 0f, 0f, presence, activeDetailCue, idleDetailCue, load.Source);
                if (updateAudio)
                    Update(thruster.CubeGrid, listener);
                return;
            }

            float command = Clamp01(load.Value);
            float target = Clamp01(command * presence);
            _directions[(int)direction].Report(thruster, thruster.WorldMatrix.Translation, command, target, presence, activeDetailCue, idleDetailCue, load.Source);
            if (updateAudio)
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
            {
                try
                {
                    _directions[i].Update(grid, listener, stateCue, now);
                }
                catch (Exception ex)
                {
                    V2DebugLog.WriteEvent("direction-update-failed", string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "grid={0} dir={1} {2}",
                        grid != null ? grid.EntityId : 0L,
                        (V2ThrustDirectionGroup)i,
                        ex.Message));
                }
            }
        }

        public void Stop()
        {
            for (int i = 0; i < _directions.Length; i++)
                _directions[i].Stop();
        }

        public void Silence()
        {
            for (int i = 0; i < _directions.Length; i++)
                _directions[i].Silence();
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

        public int CountKnownSourceGroups(DateTime now)
        {
            int count = 0;
            for (int i = 0; i < _directions.Length; i++)
            {
                if (_directions[i].HasKnownContribution)
                    count++;
            }

            return count;
        }

        public void DrawDebugMarkers()
        {
            for (int i = 0; i < _directions.Length; i++)
                _directions[i].DrawDebugMarkers();
        }

        private static DetailLoadSample CalculateCommandLoad(MyThrust thruster, V2AudioListenerState listener)
        {
            if (thruster == null)
                return new DetailLoadSample(0f, "off");

            if (!IsThrusterEnabledForAudio(thruster))
                return new DetailLoadSample(0f, "off");

            if (TryReadThrustOverridePercentage(thruster, out float overridePercentage))
            {
                float overrideValue = Clamp01(overridePercentage);
                if (overrideValue > 0.001f)
                    return new DetailLoadSample(overrideValue, "ovr");
            }

            bool hasCurrentOutput = TryReadCurrentThrustPercentage(thruster, out float currentPercentage);
            float currentOutput = hasCurrentOutput ? Clamp01(currentPercentage) : 0f;
            bool hasForceOutput = TryReadPhysicalThrustPercentage(thruster, out float forcePercentage);
            float forceOutput = hasForceOutput ? Clamp01(forcePercentage) : 0f;

            if (listener.HasMoveInput)
            {
                Vector3I direction = thruster.GridThrustDirection;
                float value = CalculateDirectionalMoveLoad(direction, listener.MoveInput);

                if (value > 0.001f)
                    return new DetailLoadSample(value, "move");
            }

            float outputThreshold = listener.SeatedInShip ? CurrentOutputAudibleThreshold : CurrentOutputNonSeatThreshold;
            float actualOutput = Math.Max(currentOutput, forceOutput);
            if (actualOutput > outputThreshold)
            {
                string source = forceOutput > currentOutput + 0.01f ? "force" : (listener.SeatedInShip ? "dmp" : "out");
                return new DetailLoadSample(actualOutput, source);
            }

            return new DetailLoadSample(0f, thruster.IsWorking ? "idle" : "off");
        }

        private static bool IsThrusterEnabledForAudio(MyThrust thruster)
        {
            if (thruster == null)
                return false;

            try
            {
                if (!thruster.Enabled)
                    return false;
            }
            catch
            {
            }

            try
            {
                if (!thruster.IsFunctional)
                    return false;
            }
            catch
            {
            }

            try
            {
                return thruster.IsWorking;
            }
            catch
            {
                return true;
            }
        }

        private static float CalculateDirectionalMoveLoad(Vector3I direction, Vector3 input)
        {
            if (Math.Abs(direction.X) >= Math.Abs(direction.Y) && Math.Abs(direction.X) >= Math.Abs(direction.Z))
                return direction.X >= 0 ? Clamp01(-input.X) : Clamp01(input.X);

            if (Math.Abs(direction.Y) >= Math.Abs(direction.Z))
                return direction.Y >= 0 ? Clamp01(-input.Y) : Clamp01(input.Y);

            return direction.Z >= 0 ? Clamp01(-input.Z) : Clamp01(input.Z);
        }

        private static bool TryReadThrustOverridePercentage(MyThrust thruster, out float percentage)
        {
            percentage = 0f;
            try
            {
                if (thruster is Sandbox.ModAPI.Ingame.IMyThrust ingameThrust)
                {
                    percentage = ingameThrust.ThrustOverridePercentage;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadCurrentThrustPercentage(MyThrust thruster, out float percentage)
        {
            percentage = 0f;
            try
            {
                if (thruster is Sandbox.ModAPI.Ingame.IMyThrust ingameThrust)
                {
                    percentage = ingameThrust.CurrentThrustPercentage;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadPhysicalThrustPercentage(MyThrust thruster, out float percentage)
        {
            percentage = 0f;
            try
            {
                if (thruster == null)
                    return false;

                float maxForce = GetMaxForce(thruster);
                if (maxForce <= 0f)
                    return false;

                percentage = thruster.ThrustForceLength / maxForce;
                return true;
            }
            catch
            {
                return false;
            }
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
                return direction.X >= 0 ? V2ThrustDirectionGroup.Left : V2ThrustDirectionGroup.Right;

            if (Math.Abs(direction.Y) >= Math.Abs(direction.Z))
                return direction.Y >= 0 ? V2ThrustDirectionGroup.Down : V2ThrustDirectionGroup.Up;

            return direction.Z >= 0 ? V2ThrustDirectionGroup.Forward : V2ThrustDirectionGroup.Backward;
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
            private LayerEmitter _detailIdle;
            private LayerEmitter _detailActive;
            private LayerEmitter _state;
            private float _detailIdleValue;
            private float _detailActiveValue;
            private float _stateValue;
            private float _detailCommandValue;
            private DateTime _lastDetailIdleUpdateUtc = DateTime.UtcNow;
            private DateTime _lastDetailActiveUpdateUtc = DateTime.UtcNow;
            private DateTime _lastStateUpdateUtc = DateTime.UtcNow;
            private DateTime _lastDetailCommandUpdateUtc = DateTime.UtcNow;
            private DateTime _lastDetailDiagnosticUtc = DateTime.MinValue;
            private Vector3D _lastPosition;
            private bool _hasKnownSource;
            private Vector3D _knownPosition;
            private float _knownGeometryWeight;
            private MyThrust _knownAnchor;
            private string _knownActiveDetailCue;
            private string _knownIdleDetailCue;
            private DateTime _lastKnownUtc = DateTime.MinValue;

            public DirectionState(V2ThrustDirectionGroup direction)
            {
                _direction = direction;
            }

            public bool DetailActive => (_detailIdle != null && _detailIdle.IsPlaying) || (_detailActive != null && _detailActive.IsPlaying);

            public bool StateActive => _state != null && _state.IsPlaying;

            public bool HasKnownContribution => _hasKnownSource || _contributors.Count > 0;

            public bool HasFreshContribution(DateTime now)
            {
                foreach (Contribution contribution in _contributors.Values)
                {
                    if (now - contribution.UpdatedUtc <= ContributionLifetime)
                        return true;
                }

                return false;
            }

            public void Report(MyThrust thruster, Vector3D position, float command, float target, float geometryWeight, string activeDetailCue, string idleDetailCue, string loadSource)
            {
                DateTime now = DateTime.UtcNow;
                _contributors[thruster] = new Contribution
                {
                    Anchor = thruster,
                    Position = position,
                    Command = command,
                    Target = target,
                    GeometryWeight = geometryWeight,
                    ActiveDetailCue = activeDetailCue,
                    IdleDetailCue = idleDetailCue,
                    LoadSource = loadSource,
                    UpdatedUtc = now
                };

                RememberKnownSource(thruster, position, geometryWeight, activeDetailCue, idleDetailCue, now);
            }

            public void Update(MyCubeGrid grid, V2AudioListenerState listener, string stateCue, DateTime now)
            {
                DirectionSnapshot snapshot = BuildSnapshot(grid, now);
                _lastPosition = snapshot.Position;

                RealisticSoundPlusSettings settings = SettingsManager.Current;
                float distanceGain = CalculateDistanceGain(listener, snapshot.Position, settings);
                float rawDetailCommand = Clamp01(snapshot.Command);
                float detailCommand = MoveToward(_detailCommandValue, rawDetailCommand, ref _lastDetailCommandUpdateUtc, settings.V2DetailCommandSmoothingMs);
                _detailCommandValue = detailCommand;
                float activeInput = Clamp01(detailCommand * snapshot.Presence);
                float shapedTarget = Clamp01((float)Math.Pow(activeInput, settings.AudioCurveExponent));
                float activeBlend = SmoothStep(detailCommand / DetailIdleFadeOutRange);
                bool idleEligible = snapshot.HasGeometry && IsDetailSourceAudibleWhenIdle(snapshot.DetailLoadSource);
                float idleInput = idleEligible ? DetailIdleInput * (1f - activeBlend * (1f - DetailIdleUnderActive)) : 0f;
                float activePitch = Lerp(DetailActivePitchMin, DetailActivePitchMax, detailCommand);
                float detailOutputGain = CalculateDetailOutputGain(settings);
                float stateInput = Math.Max(Math.Max(shapedTarget, CalculateSpeedStateInput(grid)), snapshot.HasGeometry ? StateIdleInput : 0f);
                bool requireDetailLocalVariant = listener.InsideShip && settings.V2Detail2DPositionalTest;
                bool idleHasLocalVariant = V2CueCatalog.HasDetailLocalVariant(snapshot.IdleDetailCue);
                bool activeHasLocalVariant = V2CueCatalog.HasDetailLocalVariant(snapshot.ActiveDetailCue);
                bool idleDetailPlayable = !requireDetailLocalVariant || idleHasLocalVariant;
                bool activeDetailPlayable = !requireDetailLocalVariant || activeHasLocalVariant;
                bool idleDetail2DPositional = requireDetailLocalVariant && idleHasLocalVariant;
                bool activeDetail2DPositional = requireDetailLocalVariant && activeHasLocalVariant;
                string idleVariant = idleDetail2DPositional ? "d2pos" : (requireDetailLocalVariant ? "missing-d2" : "d3");
                string activeVariant = activeDetail2DPositional ? "d2pos" : (requireDetailLocalVariant ? "missing-d2" : "d3");
                float idleDetailTarget = settings.V2DetailEnabled && settings.V2DetailIdleEnabled && idleDetailPlayable
                    ? Clamp(idleInput * detailOutputGain * settings.V2DetailIdleGain * distanceGain, 0f, MaxLayerVolume)
                    : 0f;
                float activeDetailTarget = settings.V2DetailEnabled && activeDetailPlayable
                    ? Clamp(activeInput * detailOutputGain * distanceGain, 0f, MaxLayerVolume)
                    : 0f;
                float stateTarget = settings.V2StateEnabled
                    ? Clamp(stateInput * settings.EngineGain * settings.V2StateGain * distanceGain, 0f, MaxLayerVolume)
                    : 0f;

                activeDetailTarget *= SmoothStep(activeInput / settings.V2SoftFadeRatio);
                stateTarget *= SmoothStep(stateInput / settings.V2SoftFadeRatio);
                LogDetailDiagnostics(settings, listener, snapshot, rawDetailCommand, detailCommand, activeInput, idleInput, idleDetailTarget, activeDetailTarget, distanceGain, activePitch, idleVariant, activeVariant, now);

                string idleRoute = string.Format(System.Globalization.CultureInfo.InvariantCulture, "v2-detail-{0}-idle/{1}/{2} raw={3:0.00} cmd={4:0.00}", _direction, idleVariant, snapshot.DetailLoadSource ?? "?", rawDetailCommand, detailCommand);
                string activeRoute = string.Format(System.Globalization.CultureInfo.InvariantCulture, "v2-detail-{0}-active/{1}/{2} raw={3:0.00} cmd={4:0.00} out={5:0.00} pitch={6:0.00}", _direction, activeVariant, snapshot.DetailLoadSource ?? "?", rawDetailCommand, detailCommand, activeInput, activePitch);
                V2FilterRoute detailFilterRoute = listener.InsideShip ? V2FilterRoute.Internal : V2FilterRoute.External;
                UpdateLayer(ref _detailIdle, ref _detailIdleValue, ref _lastDetailIdleUpdateUtc, snapshot.Anchor, snapshot.Position, idleDetailPlayable ? snapshot.IdleDetailCue : null, idleDetailTarget, V2AudioLayer.Detail, idleDetail2DPositional, idleDetail2DPositional, detailFilterRoute, idleRoute, holdSilent: true);
                UpdateLayer(ref _detailActive, ref _detailActiveValue, ref _lastDetailActiveUpdateUtc, snapshot.Anchor, snapshot.Position, activeDetailPlayable ? snapshot.ActiveDetailCue : null, activeDetailTarget, V2AudioLayer.Detail, activeDetail2DPositional, activeDetail2DPositional, detailFilterRoute, activeRoute, keepAliveAtZero: true, pitch: activePitch);
                if (settings.V2StateEnabled)
                {
                    bool state2D = listener.InsideShip;
                    bool state2DPositional = state2D && settings.V2State2DPositionalTest;
                    UpdateLayer(ref _state, ref _stateValue, ref _lastStateUpdateUtc, snapshot.Anchor, snapshot.Position, stateCue, stateTarget, V2AudioLayer.State, state2D, state2DPositional, state2D ? V2FilterRoute.Internal : V2FilterRoute.External);
                }
                else
                {
                    StopLayer(ref _state, ref _stateValue, ref _lastStateUpdateUtc);
                }
            }

            public void Stop()
            {
                _detailIdleValue = 0f;
                _detailActiveValue = 0f;
                _stateValue = 0f;
                _detailIdle?.Stop();
                _detailActive?.Stop();
                _state?.Stop();
            }

            public void Silence()
            {
                _detailIdleValue = 0f;
                _detailActiveValue = 0f;
                _stateValue = 0f;
                _detailIdle?.SetVolume(0f);
                _detailActive?.SetVolume(0f);
                _state?.SetVolume(0f);
            }

            private static void StopLayer(ref LayerEmitter emitter, ref float value, ref DateTime lastUpdateUtc)
            {
                value = 0f;
                lastUpdateUtc = DateTime.MinValue;
                if (emitter == null)
                    return;

                emitter.Stop();
                emitter = null;
            }

            public bool IsStale(DateTime now)
            {
                foreach (Contribution contribution in _contributors.Values)
                {
                    if (now - contribution.UpdatedUtc <= ContributionLifetime)
                        return false;
                }

                return !_hasKnownSource || now - _lastKnownUtc > KnownSourceLifetime;
            }

            public void DrawDebugMarkers()
            {
                if (_detailActive != null && _detailActive.IsPlaying)
                    DrawMarker(_detailActive.Position, new Color(90, 220, 255, 220), 0.7f);
                else if (_detailIdle != null && _detailIdle.IsPlaying)
                    DrawMarker(_detailIdle.Position, new Color(60, 150, 180, 180), 0.55f);
                else if (HasKnownContribution)
                    DrawMarker(GetBestDebugPosition(), new Color(60, 150, 180, 120), 0.45f);

                if (_state != null && _state.IsPlaying)
                    DrawMarker(_state.Position, new Color(255, 180, 80, 230), 1.05f);
                else if (HasKnownContribution)
                    DrawMarker(GetBestDebugPosition(), new Color(180, 120, 60, 120), 0.65f);
            }

            private DirectionSnapshot BuildSnapshot(MyCubeGrid grid, DateTime now)
            {
                Vector3D weightedPosition = Vector3D.Zero;
                float totalWeight = 0f;
                Vector3D geometryWeightedPosition = Vector3D.Zero;
                float totalGeometryWeight = 0f;
                float strongestTarget = 0f;
                float strongestCommand = 0f;
                float strongestPresence = 0f;
                float strongestGeometryWeight = 0f;
                MyThrust strongestThruster = null;
                MyThrust strongestGeometryThruster = null;
                string strongestActiveDetailCue = null;
                string strongestGeometryActiveDetailCue = null;
                string strongestGeometryIdleDetailCue = null;
                string strongestLoadSource = null;
                string strongestGeometryLoadSource = null;
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

                    Vector3D contributionPosition = contribution.Anchor != null
                        ? contribution.Anchor.WorldMatrix.Translation
                        : contribution.Position;

                    if (contribution.GeometryWeight > 0f)
                    {
                        geometryWeightedPosition += contributionPosition * contribution.GeometryWeight;
                        totalGeometryWeight += contribution.GeometryWeight;
                        if (contribution.GeometryWeight > strongestGeometryWeight)
                        {
                            strongestGeometryWeight = contribution.GeometryWeight;
                            strongestGeometryThruster = contribution.Anchor;
                            strongestGeometryActiveDetailCue = contribution.ActiveDetailCue;
                            strongestGeometryIdleDetailCue = contribution.IdleDetailCue;
                            strongestGeometryLoadSource = contribution.LoadSource;
                        }
                    }

                    if (contribution.Target <= 0f)
                    {
                        if (contribution.Command > strongestCommand)
                            strongestCommand = contribution.Command;
                        continue;
                    }

                    weightedPosition += contributionPosition * contribution.Target;
                    totalWeight += contribution.Target;
                    if (contribution.Target > strongestTarget)
                    {
                        strongestTarget = contribution.Target;
                        strongestCommand = contribution.Command;
                        strongestPresence = contribution.GeometryWeight;
                        strongestThruster = contribution.Anchor;
                        strongestActiveDetailCue = contribution.ActiveDetailCue;
                        strongestLoadSource = contribution.LoadSource;
                    }
                }

                if (stale != null)
                {
                    foreach (MyThrust thruster in stale)
                        _contributors.Remove(thruster);
                }

                MyThrust anchor = strongestGeometryThruster ?? strongestThruster ?? _knownAnchor;
                Vector3D knownPosition = TryGetKnownLivePosition(out Vector3D liveKnownPosition)
                    ? liveKnownPosition
                    : _knownPosition;
                Vector3D position = totalWeight > 0f
                    ? weightedPosition / totalWeight
                    : (totalGeometryWeight > 0f
                        ? geometryWeightedPosition / totalGeometryWeight
                        : (_hasKnownSource ? knownPosition : (grid?.WorldMatrix.Translation ?? _lastPosition)));
                string activeDetailCue = strongestActiveDetailCue ?? strongestGeometryActiveDetailCue ?? _knownActiveDetailCue ?? _knownIdleDetailCue;
                string idleDetailCue = strongestGeometryIdleDetailCue ?? _knownIdleDetailCue ?? strongestGeometryActiveDetailCue ?? _knownActiveDetailCue;
                string loadSource = strongestLoadSource ?? strongestGeometryLoadSource ?? "none";

                return new DirectionSnapshot
                {
                    Anchor = anchor,
                    Position = position,
                    Target = strongestTarget,
                    Command = strongestCommand,
                    Presence = strongestPresence > 0f ? strongestPresence : strongestGeometryWeight,
                    ActiveDetailCue = activeDetailCue,
                    IdleDetailCue = idleDetailCue,
                    DetailLoadSource = loadSource,
                    HasGeometry = totalGeometryWeight > 0f || _hasKnownSource
                };
            }

            private void RememberKnownSource(MyThrust thruster, Vector3D position, float geometryWeight, string activeDetailCue, string idleDetailCue, DateTime now)
            {
                if (position == Vector3D.Zero)
                    return;

                bool firstKnownSource = !_hasKnownSource || _knownAnchor == null || _knownPosition == Vector3D.Zero;
                bool sameKnownAnchor = _knownAnchor != null && ReferenceEquals(_knownAnchor, thruster);
                _hasKnownSource = true;
                _lastKnownUtc = now;
                _lastPosition = position;

                if (sameKnownAnchor)
                    _knownPosition = position;

                if (firstKnownSource || geometryWeight > _knownGeometryWeight + 0.0001f)
                {
                    _knownPosition = position;
                    _knownGeometryWeight = geometryWeight;
                    _knownAnchor = thruster;
                    _knownActiveDetailCue = activeDetailCue;
                    _knownIdleDetailCue = idleDetailCue;
                }
            }

            private bool TryGetKnownLivePosition(out Vector3D position)
            {
                position = Vector3D.Zero;
                try
                {
                    if (_knownAnchor == null || _knownAnchor.CubeGrid == null)
                        return false;

                    position = _knownAnchor.WorldMatrix.Translation;
                    if (position == Vector3D.Zero)
                        return false;

                    _knownPosition = position;
                    _lastPosition = position;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private Vector3D GetBestDebugPosition()
            {
                if (TryGetKnownLivePosition(out Vector3D liveKnownPosition))
                    return liveKnownPosition;

                if (_lastPosition != Vector3D.Zero)
                    return _lastPosition;

                return _knownPosition;
            }

            private void UpdateLayer(ref LayerEmitter emitter, ref float value, ref DateTime lastUpdateUtc, MyThrust anchor, Vector3D position, string cueName, float target, V2AudioLayer layer, bool force2D, bool force2DPositional, V2FilterRoute filterRoute, string diagnosticRoute = null, bool startMuted = false, bool keepAliveAtZero = false, float pitch = 1f, bool holdSilent = false)
            {
                value = Smooth(value, target, ref lastUpdateUtc, SettingsManager.Current.V2SmoothingMs);
                bool canPlay = anchor != null && !string.IsNullOrWhiteSpace(cueName);
                if (!canPlay)
                {
                    emitter?.SetVolume(0f);
                    emitter?.Stop();
                    return;
                }

                bool force3D = !force2D || force2DPositional;
                if (value <= StartThreshold)
                {
                    if (emitter == null)
                    {
                        if (!keepAliveAtZero)
                            return;

                        emitter = new LayerEmitter(anchor, layer, _direction);
                    }

                    DateTime quietNow = DateTime.UtcNow;
                    if (emitter.IsRetryBlocked(quietNow))
                    {
                        value = 0f;
                        return;
                    }

                    try
                    {
                        emitter.Update(position, cueName, 0f, force2D, force3D, filterRoute, pitch);
                    }
                    catch (Exception ex)
                    {
                        if (emitter.ShouldLogUpdateFailure(quietNow))
                        {
                            V2DebugLog.WriteEvent("emitter-update-failed", string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "{0} cue={1} route={2} filter={3} force2d={4} force3d={5} vol={6:0.000} pos={7:0.0},{8:0.0},{9:0.0} {10}",
                                layer,
                                cueName ?? "?",
                                (diagnosticRoute ?? "?") + " stage=" + (emitter.LastStage ?? "?"),
                                filterRoute,
                                force2D ? "Y" : "N",
                                force3D ? "Y" : "N",
                                0f,
                                position.X,
                                position.Y,
                                position.Z,
                                ex.Message));
                        }

                        emitter.RecoverAfterFailedUpdate(TimeSpan.FromSeconds(1));
                        return;
                    }

                    if (keepAliveAtZero || holdSilent)
                        return;

                    if (emitter.ShouldStopAfterSilence(quietNow, DetailSilentReleaseMs))
                        emitter.Stop();
                    return;
                }

                bool firstStart = emitter == null;
                if (firstStart)
                {
                    emitter = new LayerEmitter(anchor, layer, _direction);
                }

                if (firstStart && startMuted)
                {
                    value = 0f;
                    lastUpdateUtc = DateTime.UtcNow;
                }

                DateTime now = DateTime.UtcNow;
                if (emitter.IsRetryBlocked(now))
                {
                    value = 0f;
                    return;
                }

                float emitterVolume = value;
                try
                {
                    emitter.Update(position, cueName, emitterVolume, force2D, force3D, filterRoute, pitch);
                }
                catch (Exception ex)
                {
                    if (emitter.ShouldLogUpdateFailure(now))
                    {
                        V2DebugLog.WriteEvent("emitter-update-failed", string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0} cue={1} route={2} filter={3} force2d={4} force3d={5} vol={6:0.000} pos={7:0.0},{8:0.0},{9:0.0} {10}",
                            layer,
                            cueName ?? "?",
                            (diagnosticRoute ?? "?") + " stage=" + (emitter.LastStage ?? "?"),
                            filterRoute,
                            force2D ? "Y" : "N",
                            force3D ? "Y" : "N",
                            emitterVolume,
                            position.X,
                            position.Y,
                            position.Z,
                            ex.Message));
                    }

                    emitter.RecoverAfterFailedUpdate(TimeSpan.FromSeconds(1));
                    return;
                }

                float finalEmitterVolume = emitter.VolumeMultiplier;
                string route = diagnosticRoute ?? emitter.RouteName;
                AudioDiagnostics.RecordEmitter(emitter.Emitter, route, value, ExteriorSoundTransmission.Calculate(position), target, finalEmitterVolume, position);
                AudioDiagnostics.RecordCueName(cueName, route, value, ExteriorSoundTransmission.Calculate(position), target, finalEmitterVolume, position);
            }

            private static float CalculateDistanceGain(V2AudioListenerState listener, Vector3D position, RealisticSoundPlusSettings settings)
            {
                if (listener.Position == Vector3D.Zero || position == Vector3D.Zero)
                    return 1f;

                return V2EngineFilterModel.CalculateDistanceGain(listener, position, settings);
            }

            private void LogDetailDiagnostics(RealisticSoundPlusSettings settings, V2AudioListenerState listener, DirectionSnapshot snapshot, float rawDetailCommand, float detailCommand, float activeInput, float idleInput, float idleTarget, float activeTarget, float distanceGain, float pitch, string idleVariant, string activeVariant, DateTime now)
            {
                if (!settings.V2DebugLogEnabled || !settings.V2DetailEnabled || !snapshot.HasGeometry)
                    return;

                if (now - _lastDetailDiagnosticUtc < TimeSpan.FromSeconds(1))
                    return;

                _lastDetailDiagnosticUtc = now;
                float distance = listener.Position == Vector3D.Zero || snapshot.Position == Vector3D.Zero
                    ? 0f
                    : (float)Vector3D.Distance(listener.Position, snapshot.Position);

                V2DebugLog.WriteEvent("detail", string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0} src={1} raw={2:0.00} cmd={3:0.00} cmdMs={4:0} activeIn={5:0.00} idleIn={6:0.000} idleTarget={7:0.000} activeTarget={8:0.000} idle={9}/{10:0.00} dist={11:0.0}/{12:0} dgain={13:0.00} dcurve={14:0.00} pitch={15:0.00} idleCue={16}/{17} activeCue={18}/{19}",
                    _direction,
                    snapshot.DetailLoadSource ?? "?",
                    rawDetailCommand,
                    detailCommand,
                    settings.V2DetailCommandSmoothingMs,
                    activeInput,
                    idleInput,
                    idleTarget,
                    activeTarget,
                    settings.V2DetailIdleEnabled ? "on" : "off",
                    settings.V2DetailIdleGain,
                    distance,
                    settings.V2EmitterDistance,
                    distanceGain,
                    settings.V2DistanceCurve,
                    pitch,
                    idleVariant,
                    snapshot.IdleDetailCue ?? "?",
                    activeVariant,
                    snapshot.ActiveDetailCue ?? "?"));
            }

            private static float CalculateDetailOutputGain(RealisticSoundPlusSettings settings)
            {
                float gainProduct = Math.Max(0f, settings.EngineGain * settings.V2DetailGain);
                if (gainProduct <= 0f)
                    return 0f;

                return Clamp(0.25f + gainProduct * 0.1875f, 0f, MaxLayerVolume);
            }

            private static bool IsDetailSourceAudibleWhenIdle(string source)
            {
                return !string.IsNullOrWhiteSpace(source)
                    && !string.Equals(source, "off", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(source, "none", StringComparison.OrdinalIgnoreCase);
            }

            private static float Lerp(float min, float max, float amount)
            {
                float t = Clamp01(amount);
                return min + (max - min) * t;
            }

            private static float CalculateSpeedStateInput(MyCubeGrid grid)
            {
                try
                {
                    if (grid?.Physics == null)
                        return 0f;

                    float speed = (float)grid.Physics.LinearVelocity.Length();
                    return Clamp01(speed / VanillaFullSpeed);
                }
                catch
                {
                    return 0f;
                }
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

            private static float MoveToward(float current, float target, ref DateTime lastUpdateUtc, float fullRangeMs)
            {
                if (fullRangeMs <= 0f)
                {
                    lastUpdateUtc = DateTime.UtcNow;
                    return target;
                }

                DateTime now = DateTime.UtcNow;
                double elapsedMs = Math.Max(0.0, (now - lastUpdateUtc).TotalMilliseconds);
                lastUpdateUtc = now;
                float maxDelta = Clamp01((float)(elapsedMs / fullRangeMs));

                if (target > current)
                    return Math.Min(target, current + maxDelta);

                return Math.Max(target, current - maxDelta);
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
            private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static readonly string[] PitchMemberNames = { "FrequencyRatio", "PitchMultiplier", "Pitch" };
            private static readonly Dictionary<Type, MemberInfo> PitchMembers = new Dictionary<Type, MemberInfo>();
            private static readonly HashSet<Type> NoPitchMembers = new HashSet<Type>();
            private static readonly HashSet<Type> PitchMemberLogs = new HashSet<Type>();
            private static readonly HashSet<Type> PitchFailureLogs = new HashSet<Type>();

            private string _cueName;
            private bool _force2D;
            private bool _force3D;
            private V2FilterRoute _filterRoute = V2FilterRoute.None;
            private string _filterEffectSubtype;
            private string _filterEffectSignature;
            private DateTime _rebindFadeStartUtc = DateTime.MinValue;
            private DateTime _silentSinceUtc = DateTime.MinValue;
            private DateTime _retryBlockedUntilUtc = DateTime.MinValue;
            private DateTime _lastUpdateFailureLogUtc = DateTime.MinValue;
            private int _bindingGeneration = -1;
            private float _pitch = 1f;

            public LayerEmitter(MyThrust anchor, V2AudioLayer layer, V2ThrustDirectionGroup direction)
            {
                Anchor = anchor;
                Layer = layer;
                Direction = direction;
                Emitter = new MyEntity3DSoundEmitter(anchor, false);
                Emitter.VolumeMultiplier = 0f;
                AudioEngineV2Runtime.RegisterEmitter(Emitter, V2FilterRoute.External);
            }

            public MyThrust Anchor { get; }

            public V2AudioLayer Layer { get; }

            public V2ThrustDirectionGroup Direction { get; }

            public MyEntity3DSoundEmitter Emitter { get; }

            public bool IsPlaying { get; private set; }

            public Vector3D Position { get; private set; }

            public float VolumeMultiplier => Emitter.VolumeMultiplier;

            public string LastStage { get; private set; }

            public string RouteName
            {
                get
                {
                    return Layer == V2AudioLayer.Detail
                        ? "v2-detail-" + Direction
                        : "v2-state-" + Direction;
                }
            }

            public void Update(Vector3D position, string cueName, float volume, bool force2D, bool force3D, V2FilterRoute filterRoute, float pitch = 1f)
            {
                LastStage = "set-position";
                Position = position;
                Emitter.SetPosition(position);
                AudioEngineV2Runtime.SetEmitterPosition(Emitter, position);
                LastStage = "force-flags";
                Emitter.Force2D = force2D;
                Emitter.Force3D = force3D;
                LastStage = "register";
                AudioEngineV2Runtime.RegisterEmitter(Emitter, filterRoute, RouteName);
                LastStage = "effect-subtype";
                string filterEffectSubtype = AudioEngineV2Runtime.GetEngineFilterEffectSubtype(Emitter) ?? string.Empty;
                LastStage = "effect-signature";
                string filterEffectSignature = AudioEngineV2Runtime.GetEngineFilterEffectSignature(Emitter) ?? filterEffectSubtype;
                int bindingGeneration = AudioEngineV2Runtime.EmitterBindingGeneration;
                bool filterChanged = _filterRoute != filterRoute || !string.Equals(_filterEffectSubtype, filterEffectSubtype, StringComparison.OrdinalIgnoreCase);
                bool bindingChanged = _bindingGeneration != bindingGeneration;
                bool needsRebind = !IsPlaying
                    || !string.Equals(_cueName, cueName, StringComparison.OrdinalIgnoreCase)
                    || _force2D != force2D
                    || _force3D != force3D
                    || filterChanged
                    || bindingChanged;

                if (needsRebind)
                {
                    LastStage = "mute-before-rebind";
                    MuteBeforeRebind();
                    if (IsPlaying)
                    {
                        LastStage = "stop-before-rebind";
                        Emitter.StopSound(false, false, false);
                    }

                    _cueName = cueName;
                    _force2D = force2D;
                    _force3D = force3D;
                    _filterRoute = filterRoute;
                    _filterEffectSubtype = filterEffectSubtype;
                    _bindingGeneration = bindingGeneration;
                    _rebindFadeStartUtc = DateTime.UtcNow;
                    LastStage = "sound-pair";
                    MySoundPair pair = new MySoundPair(cueName, false);
                    LastStage = "preload";
                    MyEntity3DSoundEmitter.PreloadSound(pair);
                    LastStage = "play";
                    bool started = Emitter.PlaySound(pair, true, false, force2D, false, false, force3D, false);
                    IsPlaying = started;
                    if (started)
                        MuteBeforeRebind();
                    V2DebugLog.WriteEvent("emitter-start", string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0} cue={1} started={2} requested={3:0.000} actual={4:0.000} force2d={5} force3d={6} filter={7} effect={8} rebind={9} gen={10} pos={11:0.0},{12:0.0},{13:0.0}",
                        RouteName,
                        cueName,
                        started ? "Y" : "N",
                        volume,
                        Emitter.VolumeMultiplier,
                        force2D ? "Y" : "N",
                        force3D ? "Y" : "N",
                        filterRoute,
                        string.IsNullOrEmpty(filterEffectSignature) ? "none" : filterEffectSignature,
                        bindingChanged ? "binding" : (filterChanged ? "filter" : "cue"),
                        bindingGeneration,
                        position.X,
                        position.Y,
                        position.Z));
                }

                _filterEffectSignature = filterEffectSignature;

                LastStage = "set-volume";
                SetVolume(volume);
                LastStage = "set-pitch";
                SetPitch(pitch);
                LastStage = "done";
            }

            public void SetVolume(float volume)
            {
                float clampedVolume = Clamp(volume, 0f, MaxLayerVolume);
                if (clampedVolume > StartThreshold)
                    _silentSinceUtc = DateTime.MinValue;

                LastStage = "volume-multiplier";
                Emitter.VolumeMultiplier = clampedVolume * CalculateRebindGain();
                LastStage = "emitter-update";
                Emitter.Update();
                LastStage = "fast-update";
                Emitter.FastUpdate(false);
            }

            public bool ShouldStopAfterSilence(DateTime now, double graceMs)
            {
                if (_silentSinceUtc == DateTime.MinValue)
                    _silentSinceUtc = now;

                return (now - _silentSinceUtc).TotalMilliseconds >= graceMs;
            }

            public bool IsRetryBlocked(DateTime now)
            {
                return _retryBlockedUntilUtc != DateTime.MinValue && now < _retryBlockedUntilUtc;
            }

            public bool ShouldLogUpdateFailure(DateTime now)
            {
                if (now - _lastUpdateFailureLogUtc < TimeSpan.FromSeconds(1))
                    return false;

                _lastUpdateFailureLogUtc = now;
                return true;
            }

            public void RecoverAfterFailedUpdate(TimeSpan retryDelay)
            {
                try
                {
                    Emitter.VolumeMultiplier = 0f;
                    Emitter.StopSound(false, false, false);
                }
                catch
                {
                }

                AudioEngineV2Runtime.UnregisterEmitter(Emitter);
                IsPlaying = false;
                _cueName = null;
                _filterEffectSubtype = null;
                _filterEffectSignature = null;
                _bindingGeneration = -1;
                LastStage = "retry-blocked";
                _silentSinceUtc = DateTime.UtcNow;
                _retryBlockedUntilUtc = DateTime.UtcNow + retryDelay;
            }

            private void MuteBeforeRebind()
            {
                try
                {
                    Emitter.VolumeMultiplier = 0f;
                    Emitter.Update();
                    Emitter.FastUpdate(false);
                }
                catch
                {
                }
            }

            private float CalculateRebindGain()
            {
                float fadeMs = SettingsManager.Current.V2EmitterFadeInMs;
                if (fadeMs <= 0f || _rebindFadeStartUtc == DateTime.MinValue)
                    return 1f;

                float t = Clamp01((float)((DateTime.UtcNow - _rebindFadeStartUtc).TotalMilliseconds / fadeMs));
                if (t >= 1f)
                    return 1f;

                return t * t * (3f - 2f * t);
            }

            private void SetPitch(float pitch)
            {
                _pitch = Clamp(pitch, 0.1f, 4f);
                TrySetPitch(Emitter, _pitch);

                try
                {
                    object sound = Emitter.Sound;
                    TrySetPitch(sound, _pitch);
                }
                catch
                {
                }

                try
                {
                    object secondarySound = Emitter.SecondarySound;
                    TrySetPitch(secondarySound, _pitch);
                }
                catch
                {
                }
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
                    V2DebugLog.WriteEvent("emitter-stop", RouteName + " cue=" + (_cueName ?? "?"));
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[RealisticSoundPlus] V2 emitter stop failed: " + ex.Message);
                }
                finally
                {
                    IsPlaying = false;
                    _cueName = null;
                    _silentSinceUtc = DateTime.MinValue;
                }
            }

            private static bool TrySetPitch(object instance, float pitch)
            {
                if (instance == null)
                    return false;

                Type type = instance.GetType();
                if (NoPitchMembers.Contains(type))
                    return false;

                if (!PitchMembers.TryGetValue(type, out MemberInfo member))
                {
                    member = ResolvePitchMember(type);
                    if (member == null)
                    {
                        NoPitchMembers.Add(type);
                        LogPitchMember(type, null);
                        return false;
                    }

                    PitchMembers[type] = member;
                    LogPitchMember(type, member);
                }

                try
                {
                    if (member is PropertyInfo property)
                    {
                        object converted = ConvertPitchValue(pitch, property.PropertyType);
                        if (converted == null)
                            return false;

                        property.SetValue(instance, converted, null);
                        return true;
                    }

                    if (member is FieldInfo field)
                    {
                        object converted = ConvertPitchValue(pitch, field.FieldType);
                        if (converted == null)
                            return false;

                        field.SetValue(instance, converted);
                        return true;
                    }
                }
                catch
                {
                    NoPitchMembers.Add(type);
                    LogPitchFailure(type, member);
                }

                return false;
            }

            private static void LogPitchMember(Type type, MemberInfo member)
            {
                if (type == null || !PitchMemberLogs.Add(type))
                    return;

                string memberName = member != null ? member.MemberType + ":" + member.Name : "missing";
                V2DebugLog.WriteEvent("pitch-member", type.FullName + " member=" + memberName);
            }

            private static void LogPitchFailure(Type type, MemberInfo member)
            {
                if (type == null || !PitchFailureLogs.Add(type))
                    return;

                string memberName = member != null ? member.MemberType + ":" + member.Name : "unknown";
                V2DebugLog.WriteEvent("pitch-set-failed", type.FullName + " member=" + memberName);
            }

            private static MemberInfo ResolvePitchMember(Type type)
            {
                for (int i = 0; i < PitchMemberNames.Length; i++)
                {
                    PropertyInfo property = type.GetProperty(PitchMemberNames[i], InstanceMembers);
                    if (property != null && property.CanWrite)
                        return property;

                    FieldInfo field = type.GetField(PitchMemberNames[i], InstanceMembers);
                    if (field != null)
                        return field;
                }

                return null;
            }

            private static object ConvertPitchValue(float pitch, Type targetType)
            {
                try
                {
                    if (targetType == typeof(float))
                        return pitch;
                    if (targetType == typeof(double))
                        return (double)pitch;
                    if (targetType == typeof(int))
                        return (int)Math.Round(pitch);

                    return Convert.ChangeType(pitch, targetType, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
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
            public float Command;
            public float Presence;
            public string ActiveDetailCue;
            public string IdleDetailCue;
            public string DetailLoadSource;
            public bool HasGeometry;
        }

        private struct Contribution
        {
            public MyThrust Anchor;
            public Vector3D Position;
            public float Command;
            public float Target;
            public float GeometryWeight;
            public string ActiveDetailCue;
            public string IdleDetailCue;
            public string LoadSource;
            public DateTime UpdatedUtc;
        }

        private struct DetailLoadSample
        {
            public readonly float Value;
            public readonly string Source;

            public DetailLoadSample(float value, string source)
            {
                Value = value;
                Source = source;
            }
        }
    }
}
