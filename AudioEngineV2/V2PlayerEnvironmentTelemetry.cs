using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2PlayerEnvironmentTelemetry
    {
        private static readonly TimeSpan ActiveUpdateInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan StableUpdateInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan StableVanillaFallbackUpdateInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PressureRefreshInterval = TimeSpan.FromMilliseconds(100);
        private const int SphereRingSegments = 6;
        private const float SphereRayWeight = 1.00f;
        private const double StableListenerMoveMetersSquared = 0.25;
        private const double HighRingDegrees = 30.0;
        private const double UpperRingDegrees = 60.0;
        private const double EquatorRingDegrees = 90.0;
        private const double LowerRingDegrees = 120.0;
        private const double LowRingDegrees = 150.0;
        private const double OxygenGridSearchRange = 6.0;
        private const float VoxelOcclusionEpsilon = 0.001f;
        private const float VoxelOcclusionMinUsefulWeight = 0.05f;
        // Lowest direction-vs-sky-up dot that still counts terrain occlusion: 0 = the full UPPER HEMISPHERE
        // (horizontal "out" through straight "up"), never the ground below.
        private const double EnvironmentVoxelMinSkyDot = 0.0;
        private const float EnvironmentVoxelMeterScale = 0.25f;
        private const float EnvironmentVoxelMinBlockedMeters = 0.35f;
        private const float SpeedOfSoundMetersPerSecond = 343.0f;
        private const float MinStructuralHitThicknessMeters = 0.08f;
        private const float HitDistanceMergeMeters = 0.035f;
        private const float MaxPairedStructuralThicknessMeters = 4.0f;

        private static readonly BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<IMyEntity> GridSearchScratch = new HashSet<IMyEntity>();
        private static readonly List<VRage.Game.ModAPI.IHitInfo> DirectRayHits = new List<VRage.Game.ModAPI.IHitInfo>(16);
        private static readonly List<VRage.Game.ModAPI.IHitInfo> ThicknessRayHits = new List<VRage.Game.ModAPI.IHitInfo>(32);
        private static readonly List<float> ThicknessHitDistances = new List<float>(32);
        private static readonly List<ThicknessInterval> _thicknessStructureMeters = new List<ThicknessInterval>(16);
        private static DateTime _lastUpdateUtc = DateTime.MinValue;
        private static DateTime _lastPressureRefreshUtc = DateTime.MinValue;
        private static Vector3D _lastSampleListenerPosition = Vector3D.Zero;
        private static long _lastSampleGridId;
        private static long _lastSampleContactGridId;
        private static bool _lastSampleInsideShip;
        private static bool _lastSampleVanillaFallback;
        private static string _lastSampleModeName;
        private static V2PlayerEnvironmentSample _latest;
        private static MethodInfo _castRayMethod;
        private static CastRayMode _castRayMode;
        private static bool _castRayResolved;
        private static bool _castRayDisabled;
        private static string _castRayModeName = "unresolved";
        private static int _castRayErrors;
        private static bool _loggedCastRayMissing;
        private static bool _loggedCastRayException;
        private static bool _loggedCastRayResolved;
        private static bool _typedCastRayDisabled;
        private static bool _loggedTypedCastRayException;
        private static bool _loggedVoxelProbeException;
        private static string _lastOxygenDebugKey = string.Empty;
        private static DateTime _lastOxygenDebugUtc = DateTime.MinValue;
        private static List<ReverbRayDebugSample> _reverbRayDebugSamples = new List<ReverbRayDebugSample>(64);
        private static DateTime _lastReverbRayDebugUtc = DateTime.MinValue;

        public static void Reset()
        {
            _lastUpdateUtc = DateTime.MinValue;
            _lastPressureRefreshUtc = DateTime.MinValue;
            _lastSampleListenerPosition = Vector3D.Zero;
            _lastSampleGridId = 0L;
            _lastSampleContactGridId = 0L;
            _lastSampleInsideShip = false;
            _lastSampleVanillaFallback = false;
            _lastSampleModeName = null;
            _latest = default(V2PlayerEnvironmentSample);
            _castRayDisabled = false;
            _castRayErrors = 0;
            _loggedCastRayMissing = false;
            _loggedCastRayException = false;
            _loggedCastRayResolved = false;
            _typedCastRayDisabled = false;
            _loggedTypedCastRayException = false;
            _loggedVoxelProbeException = false;
            _lastOxygenDebugKey = string.Empty;
            _lastOxygenDebugUtc = DateTime.MinValue;
            if (_reverbRayDebugSamples == null)
                _reverbRayDebugSamples = new List<ReverbRayDebugSample>(64);
            else
                _reverbRayDebugSamples.Clear();
            _lastReverbRayDebugUtc = DateTime.MinValue;
            ResetEnvMapAccumulator();
        }

        public static void Update(V2AudioListenerState listener)
        {
            DateTime now = DateTime.UtcNow;
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            TimeSpan updateInterval = ResolveUpdateInterval(listener, settings);
            if (now - _lastUpdateUtc < updateInterval)
            {
                RefreshPressureOnly(listener, now);
                return;
            }

            _lastUpdateUtc = now;
            _latest = Calculate(listener, settings, now);
            _lastPressureRefreshUtc = now;
            RememberSampleListener(listener);
        }

        public static bool TryGetLatest(out V2PlayerEnvironmentSample sample)
        {
            sample = _latest;
            return sample.Valid && DateTime.UtcNow - sample.UpdatedUtc <= TimeSpan.FromSeconds(2);
        }

        public static void DrawReverbRayDebug()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.ReverbRayDebugEnabled)
                return;

            if (DateTime.UtcNow - _lastReverbRayDebugUtc > TimeSpan.FromSeconds(1))
                return;

            List<ReverbRayDebugSample> rays = _reverbRayDebugSamples;
            if (rays == null || rays.Count == 0)
                return;

            for (int i = 0; i < rays.Count; i++)
            {
                ReverbRayDebugSample ray = rays[i];
                Color color = ray.Available
                    ? (ray.Hit ? new Color(60, 230, 255, 220) : new Color(255, 170, 50, 210))
                    : new Color(120, 120, 120, 140);

                MyRenderProxy.DebugDrawLine3D(ray.From, ray.To, color, color, false, false);
                if (ray.Available && ray.Hit)
                    MyRenderProxy.DebugDrawSphere(ray.To, 0.11f, color, 0.7f, false, false, false, false);
            }
        }

        public static bool TryCompareOxygenRooms(Vector3D first, Vector3D second, out bool sameRoom, out string reason)
        {
            sameRoom = false;
            reason = "unavailable";

            V2AudioListenerState listener = AudioEngineV2Runtime.Listener;
            OxygenProbe firstProbe = ProbeOxygen(first, listener);
            OxygenProbe secondProbe = ProbeOxygen(second, listener);

            if (!firstProbe.Available || !secondProbe.Available)
            {
                reason = "probe-missing";
                return false;
            }

            if (firstProbe.GridEntityId == 0L || secondProbe.GridEntityId == 0L)
            {
                reason = "grid-missing";
                return false;
            }

            if (firstProbe.GridEntityId != secondProbe.GridEntityId)
            {
                reason = "different-grid";
                return true;
            }

            if (!firstProbe.RoomPresent || !secondProbe.RoomPresent || firstProbe.RoomKey == null || secondProbe.RoomKey == null)
            {
                reason = firstProbe.RoomPresent ? "source-no-room" : "listener-no-room";
                return true;
            }

            sameRoom = ReferenceEquals(firstProbe.RoomKey, secondProbe.RoomKey) || firstProbe.RoomKey.Equals(secondProbe.RoomKey);
            reason = sameRoom ? "same-room" : "different-room";
            return true;
        }

        private static TimeSpan ResolveUpdateInterval(V2AudioListenerState listener, RealisticSoundPlusSettings settings)
        {
            if (!_latest.Valid)
                return ActiveUpdateInterval;

            if (settings != null && (settings.PlayerFilterPathDebugEnabled || settings.ReverbRayDebugEnabled))
                return ActiveUpdateInterval;

            if (!IsStableListener(listener))
                return ActiveUpdateInterval;

            return listener.VanillaFallback
                ? StableVanillaFallbackUpdateInterval
                : StableUpdateInterval;
        }

        private static bool IsStableListener(V2AudioListenerState listener)
        {
            if (listener.Position == Vector3D.Zero || _lastSampleListenerPosition == Vector3D.Zero)
                return false;

            if (Vector3D.DistanceSquared(listener.Position, _lastSampleListenerPosition) > StableListenerMoveMetersSquared)
                return false;

            if (listener.GridEntityId != _lastSampleGridId || listener.ContactGridEntityId != _lastSampleContactGridId)
                return false;

            if (listener.InsideShip != _lastSampleInsideShip || listener.VanillaFallback != _lastSampleVanillaFallback)
                return false;

            return string.Equals(listener.ModeName ?? string.Empty, _lastSampleModeName ?? string.Empty, StringComparison.Ordinal);
        }

        private static void RememberSampleListener(V2AudioListenerState listener)
        {
            _lastSampleListenerPosition = listener.Position;
            _lastSampleGridId = listener.GridEntityId;
            _lastSampleContactGridId = listener.ContactGridEntityId;
            _lastSampleInsideShip = listener.InsideShip;
            _lastSampleVanillaFallback = listener.VanillaFallback;
            _lastSampleModeName = listener.ModeName;
        }

        public static string FormatSummary()
        {
            if (!TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "No player environment sample yet.";

            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "windMuffle={0:0.00} exposure={1:0.00} audible={2:0.00} open={3:0.00} aperture={12:0.00} coverage={4:0.00} thick={11:0.0}m vox={13:0.0}m sealed={5} sealEnv={6:0.00} atm={7:0.00} planetEnv={14} grav={15:0.00} rays={8}/{9} mode={10}",
                sample.FinalMuffling,
                sample.WindExposure,
                sample.WindAudibility,
                sample.OpenFraction,
                sample.StructuralOcclusion,
                (sample.SealedSource ?? "none") + ":" + (sample.SealedEstimate ? "Y" : "N"),
                sample.SealedEstimate ? sample.SealedExtraMuffling : 0f,
                sample.LocalAtmosphere,
                sample.OpenRays,
                sample.RaysCast,
                sample.ListenerMode ?? "?",
                sample.AverageBlockedMeters,
                sample.ApertureFraction,
                sample.VoxelBlockedMeters,
                sample.PlanetEnvironmentAvailable ? "Y" : "N",
                sample.NaturalGravityStrength);
            return summary + string.Format(
                CultureInfo.InvariantCulture,
                " room={0} r={1:0.0}m med={2:0.0}m hit={3}/{4} decay={5:0.0}s pre={6:0}ms"
                + " envmap={7}/{8}/{9} min={10} fb={11} thinSeal={12}",
                sample.ReverbRoomAvailable ? sample.ReverbRoomSource ?? "ray" : "none",
                sample.ReverbRoomEquivalentRadius,
                sample.ReverbRoomMedianDistance,
                sample.ReverbRoomHits,
                sample.ReverbRoomRays,
                sample.ReverbAutoDecaySeconds,
                sample.ReverbAutoPredelayMs,
                _envMapDiagIncluded,
                _envMapDiagSampled,
                _envMapCellCount,
                _envMapDiagMinCoverage,
                _envMapDiagFallback ? "Y" : "N",
                _envThinSealHits);
        }

        public static string FormatDetails()
        {
            if (!TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "No live sample. Open a world and wait for the V2 listener update.";

            string details = string.Format(
                CultureInfo.InvariantCulture,
                "room={0}\nplanetEnv={23} naturalGravity={24:0.00} atm={25:0.00}\noxygen={10}/{11} level={12:0.00} room={13:0.00} present={18} airtight={14} dirty={15} probes={19}/{20} grid={16}\nraycast={1}/{2} length={3:0}m blocked={4}/{5} weightedOpen={6:0.00}/{7:0.00} aperture={21:0.00} avgThick={17:0.0}m vox={22:0.0}m continuous={8:0.00} final={9:0.00}",
                Trim(sample.RoomName, 52),
                sample.RaycastAvailable ? "ok" : "fallback",
                sample.RaycastMode ?? "?",
                sample.RayLength,
                sample.BlockedRays,
                sample.RaysCast,
                sample.OpenRayWeight,
                sample.TotalRayWeight,
                sample.ContinuousMuffling,
                sample.FinalMuffling,
                sample.OxygenProbeAvailable ? "ok" : "none",
                sample.OxygenProbeSource ?? "?",
                sample.OxygenLevel,
                sample.OxygenRoomLevel,
                sample.OxygenRoomAirtight ? "Y" : "N",
                sample.OxygenRoomDirty ? "Y" : "N",
                sample.OxygenGridEntityId,
                sample.AverageBlockedMeters,
                sample.OxygenRoomPresent ? "Y" : "N",
                sample.OxygenRoomProbeCount,
                sample.OxygenAirtightProbeCount,
                sample.ApertureFraction,
                sample.VoxelBlockedMeters,
                sample.PlanetEnvironmentAvailable ? "Y" : "N",
                sample.NaturalGravityStrength,
                sample.LocalAtmosphere);
            return details + string.Format(
                CultureInfo.InvariantCulture,
                "\nreverbRoom={0} rays={1} hits={2} open={3} near/med/p75/p90={4:0.0}/{5:0.0}/{6:0.0}/{7:0.0}m radius={8:0.0}m auto room={9:0.00} decay={10:0.0}s pre={11:0}ms late={12:0}ms diff={13:0.00} dens={14:0}% tone={15:0}Hz hf={16:0.0}dB",
                sample.ReverbRoomAvailable ? sample.ReverbRoomSource ?? "ray" : "none",
                sample.ReverbRoomRays,
                sample.ReverbRoomHits,
                sample.ReverbRoomOpenRays,
                sample.ReverbRoomNearDistance,
                sample.ReverbRoomMedianDistance,
                sample.ReverbRoomP75Distance,
                sample.ReverbRoomP90Distance,
                sample.ReverbRoomEquivalentRadius,
                sample.ReverbAutoRoomSize,
                sample.ReverbAutoDecaySeconds,
                sample.ReverbAutoPredelayMs,
                sample.ReverbAutoLateDelayMs,
                sample.ReverbAutoDiffusion,
                sample.ReverbAutoDensity,
                sample.ReverbAutoToneHz,
                sample.ReverbAutoHighFrequencyDb);
        }

        public static string FormatReverbDebugSummary()
        {
            if (!TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "reverbRoom: no live sample";

            return string.Format(
                CultureInfo.InvariantCulture,
                "reverbRoom={0} rays={1} hit/open={2}/{3} near/med/p75/p90={4:0.0}/{5:0.0}/{6:0.0}/{7:0.0}m radius={8:0.0}m room={9:0.00} decay={10:0.0}s pre/late={11:0}/{12:0}ms",
                sample.ReverbRoomAvailable ? sample.ReverbRoomSource ?? "ray" : "none",
                sample.ReverbRoomRays,
                sample.ReverbRoomHits,
                sample.ReverbRoomOpenRays,
                sample.ReverbRoomNearDistance,
                sample.ReverbRoomMedianDistance,
                sample.ReverbRoomP75Distance,
                sample.ReverbRoomP90Distance,
                sample.ReverbRoomEquivalentRadius,
                sample.ReverbAutoRoomSize,
                sample.ReverbAutoDecaySeconds,
                sample.ReverbAutoPredelayMs,
                sample.ReverbAutoLateDelayMs);
        }

        private static V2PlayerEnvironmentSample Calculate(V2AudioListenerState listener, RealisticSoundPlusSettings settings, DateTime now)
        {
            Vector3D position = listener.Position;
            if (position == Vector3D.Zero)
                position = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            float externalAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(position);
            OxygenProbe oxygenProbe = ProbeOxygen(position, listener);
            float localAtmosphere = ResolveLocalAtmosphere(externalAtmosphere, oxygenProbe, listener);
            float rayLength = settings.PlayerEnvRayLength;
            int open = 0;
            int blocked = 0;
            float openWeight = 0f;
            float totalWeight = 0f;
            float weightedBlockedMeters = 0f;
            float weightedVoxelBlockedMeters = 0f;
            bool raycastAvailable = !_castRayDisabled && position != Vector3D.Zero && MyAPIGateway.Session?.Camera != null;
            float thicknessScale = Math.Max(0.1f, settings.PlayerEnvStructureThicknessScale);
            float voxelWeight = NormalizeVoxelWeight(settings.PlayerFilterVoxelOcclusionWeight);
            Vector3D probePosition = ResolveProbeOrigin(position);
            Vector3D naturalGravity;
            bool naturalGravityAvailable = TryGetNaturalGravity(probePosition, out naturalGravity) && naturalGravity.LengthSquared() > 0.0001;
            float naturalGravityStrength = naturalGravityAvailable ? (float)naturalGravity.Length() : 0f;
            bool planetEnvironmentAvailable = naturalGravityStrength > 0.05f;
            RoomRayAccumulator roomProbe = new RoomRayAccumulator(rayLength);
            List<ReverbRayDebugSample> reverbRayDebug = settings.ReverbRayDebugEnabled
                ? new List<ReverbRayDebugSample>(32)
                : null;

            _voxelSkyFromGravity = naturalGravityAvailable;
            if (raycastAvailable)
            {
                Vector3D up = GetProbeUp(probePosition, naturalGravity, naturalGravityAvailable);
                BuildStableBasis(up, out Vector3D right, out Vector3D forward);
                ProbeEnvMapDirections(
                    listener,
                    settings,
                    now,
                    probePosition,
                    up,
                    right,
                    forward,
                    rayLength,
                    thicknessScale,
                    voxelWeight,
                    roomProbe,
                    reverbRayDebug,
                    ref open,
                    ref blocked,
                    ref openWeight,
                    ref totalWeight,
                    ref weightedBlockedMeters,
                    ref weightedVoxelBlockedMeters);
            }
            if (reverbRayDebug != null)
            {
                _reverbRayDebugSamples = reverbRayDebug;
                _lastReverbRayDebugUtc = now;
            }
            else
            {
                _reverbRayDebugSamples = null;
            }

            int rays = open + blocked;
            if (rays == 0)
            {
                raycastAvailable = false;
                if (listener.InsideShip)
                {
                    blocked = 3;
                    open = 1;
                }
                else
                {
                    blocked = 0;
                    open = 4;
                }

                rays = open + blocked;
                totalWeight = rays;
                openWeight = open;
                weightedBlockedMeters = blocked * thicknessScale;
            }

            float openFraction = totalWeight <= 0.001f ? (rays <= 0 ? 1f : Clamp01(open / (float)rays)) : Clamp01(openWeight / totalWeight);
            float averageBlockedMeters = totalWeight <= 0.001f ? 0f : Math.Max(0f, weightedBlockedMeters / totalWeight);
            float averageVoxelBlockedMeters = totalWeight <= 0.001f ? 0f : Math.Max(0f, weightedVoxelBlockedMeters / totalWeight);
            float apertureFraction = Clamp01((float)Math.Pow(openFraction, Math.Max(0.1f, settings.PlayerEnvApertureCurve)));
            float structuralOcclusion = Clamp01(1f - apertureFraction);
            float continuousMuffling = structuralOcclusion;
            bool oxygenSealed = oxygenProbe.RoomPresent && oxygenProbe.RoomAirtight;
            bool sealedEstimate = oxygenSealed;
            string sealedSource = oxygenSealed ? "oxygen-room" : "none";
            float sealedExtra = sealedEstimate ? settings.PlayerFilterEnvironmentSealedFactor : 0f;
            float finalMuffling = ApplyOcclusionStrength(Clamp01(continuousMuffling + (1f - continuousMuffling) * sealedExtra), settings.PlayerFilterOcclusionStrength);
            float windExposure = Clamp01(1f - finalMuffling);
            RoomAcousticEstimate roomAcoustics = CalculateRoomAcoustics(roomProbe, oxygenProbe, rayLength, settings);

            V2PlayerEnvironmentSample sample = new V2PlayerEnvironmentSample
            {
                UpdatedUtc = now,
                Valid = true,
                RaycastAvailable = raycastAvailable && rays > 0,
                RaycastMode = _castRayModeName,
                RayLength = rayLength,
                RaysCast = rays,
                OpenRays = open,
                BlockedRays = blocked,
                OpenRayWeight = openWeight,
                TotalRayWeight = totalWeight,
                AverageBlockedMeters = averageBlockedMeters,
                WeightedBlockedMeters = weightedBlockedMeters,
                VoxelBlockedMeters = averageVoxelBlockedMeters,
                OpenFraction = openFraction,
                ApertureFraction = apertureFraction,
                StructuralOcclusion = structuralOcclusion,
                ContinuousMuffling = continuousMuffling,
                VanillaInside = listener.InsideShip,
                SealedEstimate = sealedEstimate,
                SealedSource = sealedSource,
                SealedExtraMuffling = settings.PlayerFilterEnvironmentSealedFactor,
                FinalMuffling = finalMuffling,
                WindExposure = windExposure,
                WindAudibility = Clamp01(localAtmosphere * windExposure),
                LocalAtmosphere = localAtmosphere,
                PlanetEnvironmentAvailable = planetEnvironmentAvailable,
                NaturalGravityStrength = naturalGravityStrength,
                OxygenProbeAvailable = oxygenProbe.Available,
                OxygenRoomPresent = oxygenProbe.RoomPresent,
                OxygenRoomAirtight = oxygenProbe.RoomAirtight,
                OxygenRoomDirty = oxygenProbe.RoomDirty,
                OxygenRoomProbeCount = oxygenProbe.RoomProbeCount,
                OxygenAirtightProbeCount = oxygenProbe.AirtightProbeCount,
                OxygenLevel = oxygenProbe.LocalOxygen,
                OxygenRoomLevel = oxygenProbe.RoomOxygen,
                OxygenProbeSource = oxygenProbe.Source,
                OxygenGridEntityId = oxygenProbe.GridEntityId,
                RoomName = listener.RoomName,
                ListenerMode = listener.ModeName,
                ReverbRoomAvailable = roomAcoustics.Available,
                ReverbRoomSource = roomAcoustics.Source,
                ReverbRoomRays = roomAcoustics.Rays,
                ReverbRoomHits = roomAcoustics.Hits,
                ReverbRoomOpenRays = roomAcoustics.OpenRays,
                ReverbRoomNearDistance = roomAcoustics.NearDistance,
                ReverbRoomMedianDistance = roomAcoustics.MedianDistance,
                ReverbRoomP75Distance = roomAcoustics.P75Distance,
                ReverbRoomP90Distance = roomAcoustics.P90Distance,
                ReverbRoomMeanDistance = roomAcoustics.MeanDistance,
                ReverbRoomClosedFraction = roomAcoustics.ClosedFraction,
                ReverbRoomEquivalentRadius = roomAcoustics.EquivalentRadius,
                ReverbAutoRoomSize = roomAcoustics.RoomSize,
                ReverbAutoDiffusion = roomAcoustics.Diffusion,
                ReverbAutoDecaySeconds = roomAcoustics.DecaySeconds,
                ReverbAutoEarlyGainDb = roomAcoustics.EarlyGainDb,
                ReverbAutoTailGainDb = roomAcoustics.TailGainDb,
                ReverbAutoPredelayMs = roomAcoustics.PredelayMs,
                ReverbAutoLateDelayMs = roomAcoustics.LateDelayMs,
                ReverbAutoDensity = roomAcoustics.Density,
                ReverbAutoToneHz = roomAcoustics.ToneHz,
                ReverbAutoHighFrequencyDb = roomAcoustics.HighFrequencyDb
            };

            SmoothReverbRoomSample(ref sample, settings, now);
            LogOxygenSealState(sample, oxygenProbe, settings, listener, now);
            return sample;
        }

        private static void RefreshPressureOnly(V2AudioListenerState listener, DateTime now)
        {
            if (!_latest.Valid || now - _lastPressureRefreshUtc < PressureRefreshInterval)
                return;

            Vector3D position = listener.Position;
            if (position == Vector3D.Zero)
                position = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (position == Vector3D.Zero)
                return;

            float externalAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(position);
            OxygenProbe oxygenProbe = ProbeOxygen(position, listener);
            float localAtmosphere = ResolveLocalAtmosphere(externalAtmosphere, oxygenProbe, listener);

            _latest.UpdatedUtc = now;
            _latest.LocalAtmosphere = localAtmosphere;
            _latest.WindAudibility = Clamp01(localAtmosphere * _latest.WindExposure);
            _latest.OxygenProbeAvailable = oxygenProbe.Available;
            _latest.OxygenRoomPresent = oxygenProbe.RoomPresent;
            _latest.OxygenRoomAirtight = oxygenProbe.RoomAirtight;
            _latest.OxygenRoomDirty = oxygenProbe.RoomDirty;
            _latest.OxygenRoomProbeCount = oxygenProbe.RoomProbeCount;
            _latest.OxygenAirtightProbeCount = oxygenProbe.AirtightProbeCount;
            _latest.OxygenLevel = oxygenProbe.LocalOxygen;
            _latest.OxygenRoomLevel = oxygenProbe.RoomOxygen;
            _latest.OxygenProbeSource = oxygenProbe.Source;
            _latest.OxygenGridEntityId = oxygenProbe.GridEntityId;
            _lastPressureRefreshUtc = now;
        }

        private static void SmoothReverbRoomSample(ref V2PlayerEnvironmentSample sample, RealisticSoundPlusSettings settings, DateTime now)
        {
            if (!sample.ReverbRoomAvailable || !_latest.Valid || !_latest.ReverbRoomAvailable)
                return;

            float smoothingMs = settings?.PlayerFilterSmoothingMs ?? 1000f;
            float elapsedSeconds = (float)Math.Max(0.0, (now - _latest.UpdatedUtc).TotalSeconds);
            float alpha = smoothingMs <= 0.001f
                ? 1f
                : (float)Math.Max(0.0, Math.Min(1.0, elapsedSeconds / (smoothingMs / 1000.0)));
            if (alpha >= 0.999f)
                return;

            sample.ReverbRoomNearDistance = Lerp(_latest.ReverbRoomNearDistance, sample.ReverbRoomNearDistance, alpha);
            sample.ReverbRoomMedianDistance = Lerp(_latest.ReverbRoomMedianDistance, sample.ReverbRoomMedianDistance, alpha);
            sample.ReverbRoomP75Distance = Lerp(_latest.ReverbRoomP75Distance, sample.ReverbRoomP75Distance, alpha);
            sample.ReverbRoomP90Distance = Lerp(_latest.ReverbRoomP90Distance, sample.ReverbRoomP90Distance, alpha);
            sample.ReverbRoomMeanDistance = Lerp(_latest.ReverbRoomMeanDistance, sample.ReverbRoomMeanDistance, alpha);
            sample.ReverbRoomClosedFraction = Lerp(_latest.ReverbRoomClosedFraction, sample.ReverbRoomClosedFraction, alpha);
            sample.ReverbRoomEquivalentRadius = Lerp(_latest.ReverbRoomEquivalentRadius, sample.ReverbRoomEquivalentRadius, alpha);
            sample.ReverbAutoRoomSize = Lerp(_latest.ReverbAutoRoomSize, sample.ReverbAutoRoomSize, alpha);
            sample.ReverbAutoDiffusion = Lerp(_latest.ReverbAutoDiffusion, sample.ReverbAutoDiffusion, alpha);
            sample.ReverbAutoDecaySeconds = Lerp(_latest.ReverbAutoDecaySeconds, sample.ReverbAutoDecaySeconds, alpha);
            sample.ReverbAutoEarlyGainDb = Lerp(_latest.ReverbAutoEarlyGainDb, sample.ReverbAutoEarlyGainDb, alpha);
            sample.ReverbAutoTailGainDb = Lerp(_latest.ReverbAutoTailGainDb, sample.ReverbAutoTailGainDb, alpha);
            sample.ReverbAutoPredelayMs = Lerp(_latest.ReverbAutoPredelayMs, sample.ReverbAutoPredelayMs, alpha);
            sample.ReverbAutoLateDelayMs = Lerp(_latest.ReverbAutoLateDelayMs, sample.ReverbAutoLateDelayMs, alpha);
            sample.ReverbAutoDensity = Lerp(_latest.ReverbAutoDensity, sample.ReverbAutoDensity, alpha);
            sample.ReverbAutoToneHz = Lerp(_latest.ReverbAutoToneHz, sample.ReverbAutoToneHz, alpha);
            sample.ReverbAutoHighFrequencyDb = Lerp(_latest.ReverbAutoHighFrequencyDb, sample.ReverbAutoHighFrequencyDb, alpha);
        }

        private static OxygenProbe ProbeOxygen(Vector3D position, V2AudioListenerState listener)
        {
            OxygenProbe seed = new OxygenProbe
            {
                Source = "none",
                LocalOxygen = 0f,
                RoomOxygen = 0f,
                ExactLocalOxygenAvailable = false
            };

            if (TryGetOxygenInPoint(position, out float pointOxygen))
            {
                seed.Available = true;
                seed.LocalOxygen = Clamp01(pointOxygen);
                seed.ExactLocalOxygenAvailable = true;
                seed.Source = "point";
            }

            long preferredGridId = listener.GridEntityId != 0L ? listener.GridEntityId : listener.ContactGridEntityId;
            List<MyCubeGrid> candidates = new List<MyCubeGrid>();
            CollectOxygenGridCandidates(position, preferredGridId, candidates);
            if (candidates.Count == 0)
                return seed;

            OxygenProbe best = seed;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                MyCubeGrid candidate = candidates[i];
                OxygenProbe candidateProbe = ProbeOxygenOnGrid(position, candidate, seed);
                candidateProbe.CandidateGridCount = candidates.Count;
                double candidateDistance = DistanceToBox(position, candidate.PositionComp.WorldAABB);
                if (IsBetterOxygenProbe(candidateProbe, candidateDistance, best, bestDistance))
                {
                    best = candidateProbe;
                    bestDistance = candidateDistance;
                }
            }

            return best;
        }

        private static OxygenProbe ProbeOxygenOnGrid(Vector3D position, MyCubeGrid grid, OxygenProbe seed)
        {
            OxygenProbe probe = seed;
            if (grid == null)
                return probe;

            probe.Available = true;
            probe.GridEntityId = grid.EntityId;
            probe.Source = AppendProbeSource(probe.Source, "grid");

            if (!TryWorldToGridInteger(grid, position, out Vector3I cell))
                return probe;

            VRage.Game.ModAPI.IMyCubeGrid modGrid = grid as VRage.Game.ModAPI.IMyCubeGrid;
            if (modGrid == null)
                return probe;

            // Carry the winning grid + cell so the reverb geometry override can read this exact room.
            probe.Grid = modGrid;
            probe.Cell = cell;
            probe.CellResolved = true;

            bool roomAtPositionAirtight = TryIsRoomAtPositionAirtight(modGrid, cell);
            probe.RoomAtPositionAirtight = roomAtPositionAirtight;
            if (roomAtPositionAirtight)
                probe.AirtightProbeCount++;

            VRage.Game.ModAPI.IMyGridGasSystem gasSystem = null;
            try
            {
                gasSystem = modGrid.GasSystem;
            }
            catch
            {
            }

            if (gasSystem == null)
            {
                probe.RoomPresent = roomAtPositionAirtight;
                probe.RoomAirtight = roomAtPositionAirtight;
                probe.RoomKey = roomAtPositionAirtight ? BuildCellRoomKey(grid.EntityId, cell) : null;
                return probe;
            }

            probe.Source = AppendProbeSource(probe.Source, "gas");
            VRage.Game.ModAPI.IMyOxygenRoom room = null;
            try
            {
                room = gasSystem.GetOxygenRoomForCubeGridPosition(ref cell);
                if (room != null)
                    probe.RoomProbeCount++;
            }
            catch
            {
            }

            if (room == null)
            {
                try
                {
                    VRage.Game.ModAPI.IMyOxygenBlock block = gasSystem.GetOxygenBlock(position);
                    room = block?.Room;
                    if (block != null)
                    {
                        float blockOxygen = Clamp01(block.OxygenLevel(grid.GridSize));
                        probe.LocalOxygen = Math.Max(probe.LocalOxygen, blockOxygen);
                        probe.RoomOxygen = Math.Max(probe.RoomOxygen, blockOxygen);
                        probe.ExactLocalOxygenAvailable = true;
                    }
                }
                catch
                {
                }
            }

            ProbeOxygenCellNeighborhood(modGrid, gasSystem, grid.EntityId, grid.GridSize, cell, ref probe, ref room, ref roomAtPositionAirtight);

            if (room == null)
            {
                probe.RoomPresent = roomAtPositionAirtight;
                probe.RoomAirtight = roomAtPositionAirtight;
                if (roomAtPositionAirtight && probe.RoomKey == null)
                    probe.RoomKey = BuildCellRoomKey(grid.EntityId, cell);
                return probe;
            }

            probe.RoomPresent = true;
            probe.RoomAirtight = probe.RoomAirtight || roomAtPositionAirtight || room.IsAirtight;
            probe.RoomDirty = probe.RoomDirty || room.IsDirty;
            probe.RoomKey = probe.RoomKey ?? room;

            float roomOxygen = 0f;
            try
            {
                roomOxygen = Clamp01(Math.Max(room.EnvironmentOxygen, room.OxygenLevel(grid.GridSize)));
            }
            catch
            {
            }

            probe.RoomOxygen = Math.Max(probe.RoomOxygen, roomOxygen);
            return probe;
        }

        private static void CollectOxygenGridCandidates(Vector3D position, long preferredGridId, List<MyCubeGrid> candidates)
        {
            if (preferredGridId != 0L && TryGetGridById(preferredGridId, out MyCubeGrid preferred))
                AddOxygenGridCandidate(position, preferred, candidates);

            if (MyAPIGateway.Entities == null)
                return;

            GridSearchScratch.Clear();
            try
            {
                MyAPIGateway.Entities.GetEntities(GridSearchScratch, entity =>
                {
                    MyCubeGrid candidate = entity as MyCubeGrid;
                    if (candidate == null || candidate.MarkedForClose || candidate.Closed || candidate.PositionComp == null)
                        return false;

                    return DistanceToBox(position, candidate.PositionComp.WorldAABB) <= OxygenGridSearchRange;
                });

                foreach (IMyEntity entity in GridSearchScratch)
                    AddOxygenGridCandidate(position, entity as MyCubeGrid, candidates);
            }
            catch
            {
            }
            finally
            {
                GridSearchScratch.Clear();
            }
        }

        private static void AddOxygenGridCandidate(Vector3D position, MyCubeGrid candidate, List<MyCubeGrid> candidates)
        {
            if (candidate == null || candidate.MarkedForClose || candidate.Closed || candidate.PositionComp == null)
                return;

            if (DistanceToBox(position, candidate.PositionComp.WorldAABB) > OxygenGridSearchRange)
                return;

            if (!candidates.Contains(candidate))
                candidates.Add(candidate);
        }

        private static bool IsBetterOxygenProbe(OxygenProbe candidate, double candidateDistance, OxygenProbe current, double currentDistance)
        {
            float candidateScore = ScoreOxygenProbe(candidate);
            float currentScore = ScoreOxygenProbe(current);
            if (Math.Abs(candidateScore - currentScore) > 0.001f)
                return candidateScore > currentScore;

            if (Math.Abs(candidateDistance - currentDistance) > 0.001)
                return candidateDistance < currentDistance;

            return candidate.GridEntityId != 0L && current.GridEntityId == 0L;
        }

        private static float ScoreOxygenProbe(OxygenProbe probe)
        {
            float score = 0f;
            if (probe.Available)
                score += 10f;
            if (probe.GridEntityId != 0L)
                score += 20f;
            if (probe.RoomPresent)
                score += 1000f;
            if (probe.RoomAirtight)
                score += 5000f;
            if (probe.RoomOxygen > 0.05f)
                score += 100f + probe.RoomOxygen * 25f;
            if (probe.LocalOxygen > 0.05f)
                score += 50f + probe.LocalOxygen * 10f;

            score += Math.Min(20, probe.RoomProbeCount) * 2f;
            score += Math.Min(20, probe.AirtightProbeCount) * 5f;
            return score;
        }

        private static string AppendProbeSource(string source, string token)
        {
            if (string.IsNullOrWhiteSpace(source) || source == "none")
                return token;

            return source + "+" + token;
        }

        private static void ProbeOxygenCellNeighborhood(
            VRage.Game.ModAPI.IMyCubeGrid grid,
            VRage.Game.ModAPI.IMyGridGasSystem gasSystem,
            long gridEntityId,
            float gridSize,
            Vector3I baseCell,
            ref OxygenProbe probe,
            ref VRage.Game.ModAPI.IMyOxygenRoom room,
            ref bool airtight)
        {
            if (grid == null || gasSystem == null)
                return;

            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0)
                            continue;

                        Vector3I cell = baseCell + new Vector3I(x, y, z);
                        bool cellAirtight = TryIsRoomAtPositionAirtight(grid, cell);
                        if (cellAirtight)
                        {
                            airtight = true;
                            probe.AirtightProbeCount++;
                        }

                        VRage.Game.ModAPI.IMyOxygenRoom candidate = null;
                        try
                        {
                            candidate = gasSystem.GetOxygenRoomForCubeGridPosition(ref cell);
                        }
                        catch
                        {
                        }

                        if (candidate == null)
                            continue;

                        probe.RoomProbeCount++;
                        probe.RoomPresent = true;
                        probe.RoomDirty |= candidate.IsDirty;
                        probe.RoomKey = probe.RoomKey ?? candidate;
                        room = room ?? candidate;

                        bool candidateAirtight = cellAirtight || candidate.IsAirtight;
                        probe.RoomAirtight |= candidateAirtight;
                        airtight |= candidateAirtight;

                        try
                        {
                            float candidateOxygen = Clamp01(Math.Max(candidate.EnvironmentOxygen, candidate.OxygenLevel(gridSize)));
                            probe.RoomOxygen = Math.Max(probe.RoomOxygen, candidateOxygen);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (airtight && probe.RoomKey == null)
                probe.RoomKey = BuildCellRoomKey(gridEntityId, baseCell);
        }

        private static bool TryIsRoomAtPositionAirtight(VRage.Game.ModAPI.IMyCubeGrid grid, Vector3I cell)
        {
            if (grid == null)
                return false;

            try
            {
                return grid.IsRoomAtPositionAirtight(cell);
            }
            catch
            {
                return false;
            }
        }

        private static string BuildCellRoomKey(long gridEntityId, Vector3I cell)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "grid:{0}:cell:{1}:{2}:{3}",
                gridEntityId,
                cell.X,
                cell.Y,
                cell.Z);
        }

        private static float ResolveLocalAtmosphere(float externalAtmosphere, OxygenProbe oxygenProbe, V2AudioListenerState listener)
        {
            externalAtmosphere = Clamp01(externalAtmosphere);
            float localOxygen = Clamp01(oxygenProbe.LocalOxygen);
            float roomOxygen = Clamp01(oxygenProbe.RoomOxygen);

            if (oxygenProbe.RoomPresent && (listener.InsideShip || oxygenProbe.RoomAtPositionAirtight))
                return Math.Max(externalAtmosphere, Math.Max(localOxygen, roomOxygen));

            if (oxygenProbe.ExactLocalOxygenAvailable)
                return Math.Max(externalAtmosphere, localOxygen);

            return externalAtmosphere;
        }

        private static void LogOxygenSealState(
            V2PlayerEnvironmentSample sample,
            OxygenProbe probe,
            RealisticSoundPlusSettings settings,
            V2AudioListenerState listener,
            DateTime now)
        {
            if (!settings.V2DebugLogEnabled)
                return;

            string key = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7:0.00}|{8:0.00}|{9:0.00}|{10:0.00}|{11}",
                sample.SealedEstimate ? "Y" : "N",
                sample.SealedSource ?? "none",
                probe.RoomPresent ? "Y" : "N",
                probe.RoomAirtight ? "Y" : "N",
                probe.RoomDirty ? "Y" : "N",
                probe.GridEntityId,
                probe.ExactLocalOxygenAvailable ? "exact" : "room-only",
                probe.LocalOxygen,
                probe.RoomOxygen,
                settings.PlayerFilterEnvironmentSealedFactor,
                settings.PlayerFilterBlockSealedFactor,
                sample.ListenerMode ?? "?");

            if (string.Equals(key, _lastOxygenDebugKey, StringComparison.Ordinal) && now - _lastOxygenDebugUtc < TimeSpan.FromSeconds(5))
                return;

            _lastOxygenDebugKey = key;
            _lastOxygenDebugUtc = now;

            V2DebugLog.WriteEvent(
                "player-env-sealed",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "detected={0}/{1} envFactor={2:0.00} blockFactor={3:0.00} oxygen={4}/{5} local={6:0.00}/{17} room={7:0.00} present={8} airtight={9} dirty={10} probes={11}/{12} grid={13} candidates={14} listener={15}/inside={16}",
                    sample.SealedEstimate ? "Y" : "N",
                    sample.SealedSource ?? "none",
                    settings.PlayerFilterEnvironmentSealedFactor,
                    settings.PlayerFilterBlockSealedFactor,
                    probe.Available ? "ok" : "none",
                    probe.Source ?? "?",
                    probe.LocalOxygen,
                    probe.RoomOxygen,
                    probe.RoomPresent ? "Y" : "N",
                    probe.RoomAirtight ? "Y" : "N",
                    probe.RoomDirty ? "Y" : "N",
                    probe.RoomProbeCount,
                    probe.AirtightProbeCount,
                    probe.GridEntityId,
                    probe.CandidateGridCount,
                    sample.ListenerMode ?? "?",
                    listener.InsideShip ? "Y" : "N",
                    probe.ExactLocalOxygenAvailable ? "exact" : "room-only"));
        }

        private static bool TryGetOxygenInPoint(Vector3D position, out float oxygen)
        {
            oxygen = 0f;
            try
            {
                object oxygenSystem = MyAPIGateway.Session?.OxygenProviderSystem;
                if (oxygenSystem == null)
                    return false;

                object result = oxygenSystem.GetType().GetMethod("GetOxygenInPoint")?.Invoke(oxygenSystem, new object[] { position });
                if (result == null)
                    return false;

                oxygen = Clamp01(Convert.ToSingle(result, CultureInfo.InvariantCulture));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindGridForPosition(Vector3D position, long preferredGridId, out MyCubeGrid grid)
        {
            grid = null;
            if (preferredGridId != 0L && TryGetGridById(preferredGridId, out grid))
                return true;

            if (MyAPIGateway.Entities == null)
                return false;

            GridSearchScratch.Clear();
            try
            {
                MyAPIGateway.Entities.GetEntities(GridSearchScratch, entity =>
                {
                    MyCubeGrid candidate = entity as MyCubeGrid;
                    if (candidate == null || candidate.MarkedForClose || candidate.Closed || candidate.PositionComp == null)
                        return false;

                    return DistanceToBox(position, candidate.PositionComp.WorldAABB) <= OxygenGridSearchRange;
                });

                double bestDistance = double.MaxValue;
                foreach (IMyEntity entity in GridSearchScratch)
                {
                    MyCubeGrid candidate = entity as MyCubeGrid;
                    if (candidate == null)
                        continue;

                    double distance = DistanceToBox(position, candidate.PositionComp.WorldAABB);
                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    grid = candidate;
                }

                return grid != null;
            }
            catch
            {
                grid = null;
                return false;
            }
            finally
            {
                GridSearchScratch.Clear();
            }
        }

        private static bool TryGetGridById(long gridId, out MyCubeGrid grid)
        {
            grid = null;
            if (gridId == 0L)
                return false;

            try
            {
                MyEntity entity;
                if (!MyEntities.TryGetEntityById(gridId, out entity))
                    return false;

                grid = entity as MyCubeGrid;
                return grid != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWorldToGridInteger(MyCubeGrid grid, Vector3D position, out Vector3I cell)
        {
            cell = Vector3I.Zero;
            if (grid == null)
                return false;

            try
            {
                cell = grid.WorldToGridInteger(position);
                return true;
            }
            catch
            {
            }

            try
            {
                VRage.Game.ModAPI.Ingame.IMyCubeGrid ingameGrid = grid as VRage.Game.ModAPI.Ingame.IMyCubeGrid;
                if (ingameGrid == null)
                    return false;

                cell = ingameGrid.WorldToGridInteger(position);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double DistanceToBox(Vector3D point, BoundingBoxD box)
        {
            double dx = Math.Max(Math.Max(box.Min.X - point.X, 0.0), point.X - box.Max.X);
            double dy = Math.Max(Math.Max(box.Min.Y - point.Y, 0.0), point.Y - box.Max.Y);
            double dz = Math.Max(Math.Max(box.Min.Z - point.Z, 0.0), point.Z - box.Max.Z);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ===================== Persistent directional environment occlusion map =====================
        // Replaces the time-windowed random rolling-probe buffer (the methods above are now dead, kept until a
        // cleanup pass) with a persistent listener-relative Fibonacci-lattice cell map. Each cell remembers its
        // last openness/thickness; a deterministic golden-stride sweep refreshes raysPerUpdate cells per update
        // (SAME ray budget). Stationary -> converges to a constant (no sway); a door updates one cell smoothly.
        private static EnvMapCell[] _envMap;
        private static Vector3D[] _envMapLocalDirs;
        private static int _envMapCellCount;
        private static int _envMapStride = 1;
        private static int _envMapSweepIndex;
        private static Vector3D _envMapAnchor;
        private static long _envMapGridId;
        private static long _envMapContactGridId;
        private static bool _envMapInsideShip;
        private static bool _envMapVanillaFallback;
        private static string _envMapModeName;
        private static float _envMapRayLength;
        private static float _envMapThicknessScale;
        private static float _envMapVoxelWeight;
        // Env-map + thin-seal diagnostics (surfaced in FormatSummary for the V2 debug log).
        private static int _envMapDiagIncluded;
        private static int _envMapDiagSampled;
        private static int _envMapDiagMinCoverage;
        private static bool _envMapDiagFallback;
        private static long _envThinSealHits;

        private static void EnsureEnvMap(int cellCount)
        {
            cellCount = (int)Clamp(cellCount, 32f, 192f);
            if (_envMap != null && _envMapCellCount == cellCount)
                return;

            _envMapCellCount = cellCount;
            _envMap = new EnvMapCell[cellCount];
            _envMapLocalDirs = new Vector3D[cellCount];
            double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
            for (int i = 0; i < cellCount; i++)
            {
                double z = 1.0 - (2.0 * i + 1.0) / cellCount;
                double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
                double phi = i * golden;
                _envMapLocalDirs[i] = new Vector3D(Math.Cos(phi) * r, Math.Sin(phi) * r, z);
            }
            _envMapStride = ChooseStride(cellCount);
            _envMapSweepIndex = 0;
        }

        private static int ChooseStride(int n)
        {
            if (n <= 2)
                return 1;
            int stride = Math.Max(1, (int)Math.Round(n / 1.6180339887));
            while (Gcd(stride, n) != 1)
                stride++;
            return stride % n == 0 ? 1 : stride;
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = b;
                b = a % b;
                a = t;
            }
            return a == 0 ? 1 : a;
        }

        private static void ProbeEnvMapDirections(
            V2AudioListenerState listener,
            RealisticSoundPlusSettings settings,
            DateTime now,
            Vector3D position,
            Vector3D up,
            Vector3D right,
            Vector3D forward,
            float rayLength,
            float thicknessScale,
            float voxelWeight,
            RoomRayAccumulator roomProbe,
            List<ReverbRayDebugSample> reverbRayDebug,
            ref int open,
            ref int blocked,
            ref float openWeight,
            ref float totalWeight,
            ref float weightedBlockedMeters,
            ref float weightedVoxelBlockedMeters)
        {
            EnsureEnvMap(settings.PlayerEnvMapCellCount);
            PrepareEnvMapAccumulator(listener, position, rayLength, thicknessScale, voxelWeight, settings);

            // Capture the basis/origin so the debug overlay can re-project the (listener-relative) cells.
            _envMapDebugPosition = position;
            _envMapDebugUp = up;
            _envMapDebugRight = right;
            _envMapDebugForward = forward;

            int n = _envMapCellCount;
            int stride = _envMapStride;
            float alpha = Clamp(settings.PlayerEnvMapCellAlpha, 0.1f, 1.0f);
            int rays = Math.Min(Math.Max(1, settings.PlayerEnvMapRaysPerUpdate), n);

            for (int k = 0; k < rays; k++)
            {
                int idx = (int)(((long)(_envMapSweepIndex + k) * stride) % n);
                Vector3D lp = _envMapLocalDirs[idx];
                bool includeEnv = lp.Z >= 0.0;
                Vector3D dir = right * lp.X + forward * lp.Y + up * lp.Z;
                RollingRaySample s = TraceRollingDirection(position, dir, up, SphereRayWeight, rayLength, thicknessScale, voxelWeight, includeEnv, now);
                MergeEnvCell(ref _envMap[idx], s, alpha, includeEnv);

                // Reverb / debug feed from the freshly-traced rays only (raw distances, raysPerUpdate per
                // update) to match the old per-ray cardinality, so the reverb percentile distribution is not
                // skewed by feeding all N EMA-smoothed cells every update.
                roomProbe?.Add(s.RayAvailable, s.RayHit, s.RayDistance);
                if (reverbRayDebug != null)
                {
                    reverbRayDebug.Add(new ReverbRayDebugSample
                    {
                        From = s.DebugFrom,
                        To = s.DebugTo,
                        Available = s.RayAvailable,
                        Hit = s.RayHit
                    });
                }
            }
            _envMapSweepIndex += rays;

            AggregateEnvMap(ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
        }

        private static void MergeEnvCell(ref EnvMapCell cell, RollingRaySample s, float alpha, bool includeEnv)
        {
            float a = cell.Sampled ? alpha : 1f; // first touch snaps (no warm-up smear)
            float open01 = s.Weight > 0.0001f ? Clamp01(s.OpenWeight / s.Weight) : 1f;

            cell.OpenWeight01 = Lerp(cell.OpenWeight01, open01, a);
            cell.BlockedMeters = Lerp(cell.BlockedMeters, s.BlockedMeters, a);
            cell.VoxelMeters = Lerp(cell.VoxelMeters, s.VoxelMeters, a);
            cell.RayDistance = Lerp(cell.RayDistance, s.RayDistance, a);
            cell.RayAvailable = s.RayAvailable;
            cell.RayHit = s.RayHit;
            cell.IncludeEnvironment = includeEnv;
            cell.EnvironmentAvailable = s.EnvironmentAvailable;
            cell.EnvironmentBlocked = s.EnvironmentBlocked;
            cell.Confidence = 1f;
            cell.Sampled = true;
        }

        private static void AggregateEnvMap(ref int open, ref int blocked, ref float openWeight, ref float totalWeight, ref float weightedBlockedMeters, ref float weightedVoxelBlockedMeters)
        {
            const float confidenceThreshold = 0.3f;
            int n = _envMapCellCount;

            int envIncluded = 0;
            int sampledCells = 0;
            for (int i = 0; i < n; i++)
            {
                EnvMapCell c = _envMap[i];
                if (c.Sampled)
                    sampledCells++;
                if (c.Sampled && c.Confidence > confidenceThreshold && c.IncludeEnvironment && c.EnvironmentAvailable)
                    envIncluded++;
            }

            // Coverage guard: until enough fresh directional cells exist, leave the accumulators at zero so the
            // caller's empty-result fallback fires instead of trusting a partial hemisphere (no reset muffle sweep).
            int minCoverage = Math.Max(8, n / 4);
            _envMapDiagIncluded = envIncluded;
            _envMapDiagSampled = sampledCells;
            _envMapDiagMinCoverage = minCoverage;
            _envMapDiagFallback = envIncluded < minCoverage;
            if (envIncluded < minCoverage)
                return;

            for (int i = 0; i < n; i++)
            {
                EnvMapCell c = _envMap[i];
                if (!c.Sampled || c.Confidence <= confidenceThreshold)
                    continue;
                if (!c.IncludeEnvironment || !c.EnvironmentAvailable)
                    continue;

                // Binary inclusion gate: weight = 1 (NOT Confidence) so the denominator stays integral and the
                // OpenFraction ratio is stable while moving — a decayed cell drops out wholesale until re-sampled.
                totalWeight += 1f;
                if (c.EnvironmentBlocked)
                {
                    blocked++;
                    weightedBlockedMeters += c.BlockedMeters;
                    weightedVoxelBlockedMeters += c.VoxelMeters;
                    openWeight += c.OpenWeight01;
                }
                else
                {
                    open++;
                    openWeight += 1f;
                }
            }
        }

        private static void PrepareEnvMapAccumulator(V2AudioListenerState listener, Vector3D position, float rayLength, float thicknessScale, float voxelWeight, RealisticSoundPlusSettings settings)
        {
            float resetMeters = Math.Max(1f, settings.PlayerEnvMapResetMoveMeters);
            float resetSq = resetMeters * resetMeters;
            bool reset = !AnyCellSampled()
                || _envMapAnchor == Vector3D.Zero
                || Vector3D.DistanceSquared(position, _envMapAnchor) > resetSq
                || listener.GridEntityId != _envMapGridId
                || listener.ContactGridEntityId != _envMapContactGridId
                || listener.InsideShip != _envMapInsideShip
                || listener.VanillaFallback != _envMapVanillaFallback
                || !string.Equals(listener.ModeName ?? string.Empty, _envMapModeName ?? string.Empty, StringComparison.Ordinal)
                || Math.Abs(rayLength - _envMapRayLength) > 0.25f
                || Math.Abs(thicknessScale - _envMapThicknessScale) > 0.05f
                || Math.Abs(voxelWeight - _envMapVoxelWeight) > 0.01f;

            if (reset)
            {
                ClearEnvMapCells();
                _envMapAnchor = position;
                _envMapSweepIndex = 0;
            }
            else
            {
                double moved = Vector3D.Distance(position, _envMapAnchor);
                if (moved > 0.0001)
                {
                    float decay = (float)Math.Exp(-moved / Math.Max(0.01f, settings.PlayerEnvMapConfidenceDecayMeters));
                    for (int i = 0; i < _envMapCellCount; i++)
                        _envMap[i].Confidence *= decay;
                    _envMapAnchor = position;
                }
            }

            _envMapGridId = listener.GridEntityId;
            _envMapContactGridId = listener.ContactGridEntityId;
            _envMapInsideShip = listener.InsideShip;
            _envMapVanillaFallback = listener.VanillaFallback;
            _envMapModeName = listener.ModeName;
            _envMapRayLength = rayLength;
            _envMapThicknessScale = thicknessScale;
            _envMapVoxelWeight = voxelWeight;
        }

        private static bool AnyCellSampled()
        {
            if (_envMap == null)
                return false;
            for (int i = 0; i < _envMapCellCount; i++)
                if (_envMap[i].Sampled)
                    return true;
            return false;
        }

        private static void ClearEnvMapCells()
        {
            if (_envMap != null)
                Array.Clear(_envMap, 0, _envMapCellCount);
        }

        private static void ResetEnvMapAccumulator()
        {
            if (_envMap != null)
                Array.Clear(_envMap, 0, _envMap.Length);
            _envMapSweepIndex = 0;
            _envMapAnchor = Vector3D.Zero;
            _envMapGridId = 0L;
            _envMapContactGridId = 0L;
            _envMapInsideShip = false;
            _envMapVanillaFallback = false;
            _envMapModeName = null;
            _envMapRayLength = 0f;
            _envMapThicknessScale = 0f;
            _envMapVoxelWeight = 0f;
            _envMapDiagIncluded = 0;
            _envMapDiagSampled = 0;
            _envMapDiagMinCoverage = 0;
            _envMapDiagFallback = false;
            _envThinSealHits = 0L;
        }

        private struct EnvMapCell
        {
            public bool Sampled;
            public bool IncludeEnvironment;
            public bool EnvironmentAvailable;
            public bool EnvironmentBlocked;
            public float OpenWeight01;
            public float BlockedMeters;
            public float VoxelMeters;
            public float RayDistance;
            public bool RayAvailable;
            public bool RayHit;
            public float Confidence;
        }

        private static Vector3D _envMapDebugPosition;
        private static Vector3D _envMapDebugUp;
        private static Vector3D _envMapDebugRight;
        private static Vector3D _envMapDebugForward;
        // True only when the sky-up is derived from real gravity (a planet) - gates voxel terrain occlusion so it
        // never uses the view-tilting camera-up fallback (which would sweep the upper hemisphere into the ground).
        private static bool _voxelSkyFromGravity;

        // Debug overlay: draws each sampled env cell as a FLAT TILE on a dome around the listener, facing its
        // sample direction, green=open -> red=blocked (by stored openness), alpha by confidence. This reads as a
        // tessellated sky-dome of the structural-sealing map rather than a hedgehog of radial lines. The tiles
        // touch (size scales with cell count), so a sealed-over region shows as a solid red patch overhead.
        // Toggle: /rsp envmapdebug. Stationary -> fills and holds steady (the sway-fix made visible).
        public static void DrawEnvMapDebug()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.PlayerEnvMapDebugEnabled)
                return;
            if (_envMap == null || _envMapCellCount == 0 || _envMapDebugPosition == Vector3D.Zero)
                return;

            Vector3D origin = _envMapDebugPosition;
            Vector3D up = _envMapDebugUp;
            Vector3D right = _envMapDebugRight;
            Vector3D forward = _envMapDebugForward;
            const double domeRadius = 4.0;
            // Half-tile size so neighbouring Fibonacci cells just meet: each cell ~ 4pi/N sr, angular radius
            // ~ 2/sqrt(N); project onto the dome and halve a touch so they read as separate tiles.
            double tileHalf = domeRadius * (1.7 / Math.Sqrt(Math.Max(1, _envMapCellCount)));

            for (int i = 0; i < _envMapCellCount; i++)
            {
                EnvMapCell c = _envMap[i];
                if (!c.Sampled || !c.IncludeEnvironment || !c.EnvironmentAvailable)
                    continue;

                Vector3D lp = _envMapLocalDirs[i];
                Vector3D dir = right * lp.X + forward * lp.Y + up * lp.Z;
                if (!dir.IsValid() || dir.LengthSquared() < 1e-6)
                    continue;
                dir.Normalize();

                // Tangent basis for a flat tile facing the sample direction.
                Vector3D u = Vector3D.CalculatePerpendicularVector(dir);
                if (!u.IsValid() || u.LengthSquared() < 1e-9)
                    continue;
                u.Normalize();
                Vector3D w = Vector3D.Normalize(Vector3D.Cross(dir, u));

                Vector3D center = origin + dir * domeRadius;
                Vector3D eu = u * tileHalf;
                Vector3D ew = w * tileHalf;
                Vector3D a = center + eu + ew;
                Vector3D b = center + eu - ew;
                Vector3D cc = center - eu - ew;
                Vector3D d = center - eu + ew;

                float open01 = Clamp01(c.OpenWeight01);
                byte alpha = (byte)(70f + 150f * Clamp01(c.Confidence));
                Color color = new Color((byte)(255f * (1f - open01)), (byte)(255f * open01), (byte)45, alpha);

                // Tile edges + diagonals so it reads as a filled flat cell (DebugDrawLine3D only draws lines).
                MyRenderProxy.DebugDrawLine3D(a, b, color, color, false, false);
                MyRenderProxy.DebugDrawLine3D(b, cc, color, color, false, false);
                MyRenderProxy.DebugDrawLine3D(cc, d, color, color, false, false);
                MyRenderProxy.DebugDrawLine3D(d, a, color, color, false, false);
                MyRenderProxy.DebugDrawLine3D(a, cc, color, color, false, false);
                MyRenderProxy.DebugDrawLine3D(b, d, color, color, false, false);
            }
        }

        private static RollingRaySample TraceRollingDirection(Vector3D position, Vector3D direction, Vector3D skyUp, float weight, float rayLength, float thicknessScale, float voxelWeight, bool includeEnvironment, DateTime now)
        {
            RollingRaySample sample = new RollingRaySample
            {
                UpdatedUtc = now,
                Weight = Math.Max(0.001f, weight),
                IncludeEnvironment = includeEnvironment
            };

            if (!direction.IsValid() || direction.LengthSquared() <= 0.0001)
                return sample;

            direction.Normalize();
            Vector3D from = position;
            Vector3D to = from + direction * rayLength;
            bool rayAvailable = TryRaycast(from, to, out bool hit, out float hitDistance);
            sample.RayAvailable = rayAvailable;
            sample.RayHit = hit;
            sample.RayDistance = hit ? Clamp(hitDistance, 0f, rayLength) : rayLength;
            sample.DebugFrom = from;
            sample.DebugTo = from + direction * (rayAvailable && hit ? sample.RayDistance : Math.Max(0f, rayLength));

            if (!includeEnvironment)
                return sample;

            float rawVoxelMeters = ShouldUseEnvironmentVoxelOcclusion(direction, skyUp, voxelWeight)
                ? EstimateVoxelBlockedLength(from, to, 1.0f, 48)
                : 0f;
            float voxelMeters = rawVoxelMeters * voxelWeight * EnvironmentVoxelMeterScale;
            float voxelThreshold = Math.Max(EnvironmentVoxelMinBlockedMeters, thicknessScale * 0.20f);
            bool voxelHit = voxelMeters > voxelThreshold;
            if (!rayAvailable && !voxelHit)
                return sample;

            sample.EnvironmentAvailable = true;
            sample.EnvironmentBlocked = hit || voxelHit;
            sample.VoxelMeters = voxelMeters;
            if (!sample.EnvironmentBlocked)
            {
                sample.OpenWeight = sample.Weight;
                return sample;
            }

            float structuralMeters = hit ? EstimateBlockedLength(from, to, 1.0f, 48) : 0f;
            float blockedMeters = structuralMeters;
            if (voxelHit)
                blockedMeters += voxelMeters;

            if (blockedMeters <= 0.001f)
                blockedMeters = hit ? Math.Max(MinStructuralHitThicknessMeters, thicknessScale) : voxelMeters;

            sample.BlockedMeters = blockedMeters;
            float transRoll = CalculateThicknessTransmission(blockedMeters, thicknessScale);
            RealisticSoundPlusSettings rollSettings = SettingsManager.Current;
            if (rollSettings != null && rollSettings.PlayerEnvSealedBarrierLoss > 0f && hit
                && blockedMeters < thicknessScale * Math.Max(0.05f, rollSettings.PlayerFilterSealedBarrierThinFactor)
                && TryGetFirstGridHitFace(from, to, out VRage.Game.ModAPI.IMyCubeGrid sealRollGrid, out Vector3I sealRollCell)
                && V2GridStructureProbe.IsCellAirtight(sealRollGrid, sealRollCell))
            {
                transRoll = Math.Min(transRoll, 1f - Clamp01(rollSettings.PlayerEnvSealedBarrierLoss));
                if (_envThinSealHits < long.MaxValue) _envThinSealHits++;
            }
            sample.OpenWeight = sample.Weight * transRoll;
            return sample;
        }

        private static void ProbeRing(Vector3D position, Vector3D up, Vector3D right, Vector3D forward, double degreesFromUp, float weight, float rayLength, float thicknessScale, float voxelWeight, bool includeEnvironment, RoomRayAccumulator roomProbe, List<ReverbRayDebugSample> reverbRayDebug, ref int open, ref int blocked, ref float openWeight, ref float totalWeight, ref float weightedBlockedMeters, ref float weightedVoxelBlockedMeters)
        {
            double radians = degreesFromUp * Math.PI / 180.0;
            double vertical = Math.Cos(radians);
            double horizontal = Math.Sin(radians);

            for (int i = 0; i < SphereRingSegments; i++)
            {
                double angle = (Math.PI * 2.0 * i) / SphereRingSegments;
                Vector3D tangent = right * Math.Cos(angle) + forward * Math.Sin(angle);
                Vector3D direction = up * vertical + tangent * horizontal;
                ProbeDirection(position, direction, up, weight, rayLength, thicknessScale, voxelWeight, includeEnvironment, roomProbe, reverbRayDebug, ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
            }
        }

        private static void ProbeDirection(Vector3D position, Vector3D direction, Vector3D skyUp, float weight, float rayLength, float thicknessScale, float voxelWeight, bool includeEnvironment, RoomRayAccumulator roomProbe, List<ReverbRayDebugSample> reverbRayDebug, ref int open, ref int blocked, ref float openWeight, ref float totalWeight, ref float weightedBlockedMeters, ref float weightedVoxelBlockedMeters)
        {
            if (!direction.IsValid() || direction.LengthSquared() <= 0.0001)
                return;

            direction.Normalize();
            Vector3D from = position;
            Vector3D to = from + direction * rayLength;
            bool rayAvailable = TryRaycast(from, to, out bool hit, out float hitDistance);
            roomProbe?.Add(rayAvailable, hit, hit ? hitDistance : rayLength);
            AddReverbRayDebug(reverbRayDebug, from, direction, rayLength, rayAvailable, hit, hitDistance);
            if (!includeEnvironment)
                return;

            float rawVoxelMeters = ShouldUseEnvironmentVoxelOcclusion(direction, skyUp, voxelWeight)
                ? EstimateVoxelBlockedLength(from, to, 1.0f, 64)
                : 0f;
            float voxelMeters = rawVoxelMeters * voxelWeight * EnvironmentVoxelMeterScale;
            float voxelThreshold = Math.Max(EnvironmentVoxelMinBlockedMeters, thicknessScale * 0.20f);
            bool voxelHit = voxelMeters > voxelThreshold;
            if (!rayAvailable && !voxelHit)
                return;

            float safeWeight = Math.Max(0.001f, weight);
            totalWeight += safeWeight;
            if (hit || voxelHit)
            {
                blocked++;
                float structuralMeters = hit ? EstimateBlockedLength(from, to, 1.0f, 64) : 0f;
                float blockedMeters = structuralMeters;
                if (voxelHit)
                    blockedMeters += voxelMeters;

                if (blockedMeters <= 0.001f)
                    blockedMeters = hit ? Math.Max(0.1f, thicknessScale) : voxelMeters;

                weightedBlockedMeters += safeWeight * blockedMeters;
                weightedVoxelBlockedMeters += safeWeight * voxelMeters;
                float transDir = CalculateThicknessTransmission(blockedMeters, thicknessScale);
                RealisticSoundPlusSettings dirSettings = SettingsManager.Current;
                if (dirSettings != null && dirSettings.PlayerEnvSealedBarrierLoss > 0f && hit
                    && blockedMeters < thicknessScale * Math.Max(0.05f, dirSettings.PlayerFilterSealedBarrierThinFactor)
                    && TryGetFirstGridHitFace(from, to, out VRage.Game.ModAPI.IMyCubeGrid sealDirGrid, out Vector3I sealDirCell)
                    && V2GridStructureProbe.IsCellAirtight(sealDirGrid, sealDirCell))
                {
                    transDir = Math.Min(transDir, 1f - Clamp01(dirSettings.PlayerEnvSealedBarrierLoss));
                    if (_envThinSealHits < long.MaxValue) _envThinSealHits++;
                }
                openWeight += safeWeight * transDir;
                return;
            }

            open++;
            openWeight += safeWeight;
        }

        private static bool ShouldUseEnvironmentVoxelOcclusion(Vector3D direction, Vector3D skyUp, float voxelWeight)
        {
            if (voxelWeight <= VoxelOcclusionEpsilon)
                return false;

            // Voxel (terrain) occlusion of the ambient bed only makes sense relative to a real planetary SKY:
            // the wind/ambience comes from above, so only terrain in the UPPER hemisphere occludes it - never the
            // ground you stand on. Requires a gravity-derived up (set in CalculateRoomAcoustics); off otherwise,
            // because the camera-up fallback tilts with the view and would sweep the cone into the ground below.
            if (!_voxelSkyFromGravity || !skyUp.IsValid() || skyUp.LengthSquared() <= 0.0001)
                return false;

            skyUp.Normalize();
            return Vector3D.Dot(direction, skyUp) >= EnvironmentVoxelMinSkyDot;
        }

        private static float NormalizeVoxelWeight(float value)
        {
            value = Math.Max(0f, value);
            return value < VoxelOcclusionMinUsefulWeight ? 0f : value;
        }

        private static Vector3D ResolveProbeOrigin(Vector3D position)
        {
            return position;
        }

        private static void AddReverbRayDebug(List<ReverbRayDebugSample> rays, Vector3D from, Vector3D direction, float rayLength, bool available, bool hit, float hitDistance)
        {
            if (rays == null || !from.IsValid() || !direction.IsValid())
                return;

            float drawDistance = available && hit
                ? Clamp(hitDistance, 0f, rayLength)
                : Math.Max(0f, rayLength);

            rays.Add(new ReverbRayDebugSample
            {
                From = from,
                To = from + direction * drawDistance,
                Available = available,
                Hit = hit
            });
        }

        private static Vector3D GetProbeUp(Vector3D position, Vector3D naturalGravity, bool naturalGravityAvailable)
        {
            if (naturalGravityAvailable && naturalGravity.LengthSquared() > 0.0001)
            {
                naturalGravity.Normalize();
                return -naturalGravity;
            }

            Vector3D cameraUp = MyAPIGateway.Session?.Camera?.WorldMatrix.Up ?? Vector3D.Up;
            if (!cameraUp.IsValid() || cameraUp.LengthSquared() <= 0.0001)
                return Vector3D.Up;

            cameraUp.Normalize();
            return cameraUp;
        }

        private static bool TryGetNaturalGravity(Vector3D position, out Vector3D gravity)
        {
            gravity = Vector3D.Zero;
            try
            {
                object physics = MyAPIGateway.Physics;
                if (physics == null)
                    return false;

                MethodInfo[] methods = physics.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!string.Equals(method.Name, "CalculateNaturalGravityAt", StringComparison.Ordinal))
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 1 || parameters[0].ParameterType != typeof(Vector3D))
                        continue;

                    object[] args = new object[parameters.Length];
                    args[0] = position;
                    for (int p = 1; p < parameters.Length; p++)
                    {
                        Type parameterType = parameters[p].ParameterType;
                        Type elementType = parameterType.IsByRef ? parameterType.GetElementType() : parameterType;
                        if (elementType == typeof(float))
                            args[p] = 0f;
                        else if (elementType == typeof(double))
                            args[p] = 0.0;
                        else if (elementType == typeof(Vector3D))
                            args[p] = Vector3D.Zero;
                        else if (parameters[p].HasDefaultValue)
                            args[p] = parameters[p].DefaultValue;
                        else
                            args[p] = null;
                    }

                    object result = method.Invoke(physics, args);
                    if (result is Vector3D resultGravity)
                    {
                        gravity = resultGravity;
                        return gravity.IsValid();
                    }

                    for (int p = 1; p < args.Length; p++)
                    {
                        if (args[p] is Vector3D outGravity)
                        {
                            gravity = outGravity;
                            return gravity.IsValid();
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public static float EstimateBlockedLength(Vector3D from, Vector3D to, float segmentLength, int maxSegments)
        {
            Vector3D path = to - from;
            double length = path.Length();
            if (length <= 0.05)
                return 0f;

            if (TryEstimateBlockedLengthFromHitList(from, to, Math.Max(1, maxSegments), out float hitListBlockedLength))
                return hitListBlockedLength;

            segmentLength = Math.Max(0.1f, segmentLength);
            maxSegments = Math.Max(1, maxSegments);
            int segments = Math.Max(1, Math.Min(maxSegments, (int)Math.Ceiling(length / segmentLength)));
            float blockedLength = 0f;
            Vector3D previous = from;
            for (int i = 1; i <= segments; i++)
            {
                Vector3D current = Vector3D.Lerp(from, to, i / (double)segments);
                if (TryRayBlocked(previous, current, out bool blocked) && blocked)
                    blockedLength += (float)Vector3D.Distance(previous, current);

                previous = current;
            }

            return blockedLength;
        }

        private static bool TryEstimateBlockedLengthFromHitList(Vector3D from, Vector3D to, int maxHits, out float blockedLength)
        {
            blockedLength = 0f;
            if (_typedCastRayDisabled)
                return false;

            Vector3D ray = to - from;
            double rayLength = ray.Length();
            double rayLengthSquared = ray.LengthSquared();
            if (rayLength <= 0.05 || rayLengthSquared <= 0.0001)
                return true;

            try
            {
                if (MyAPIGateway.Physics == null)
                    return false;

                ThicknessRayHits.Clear();
                ThicknessHitDistances.Clear();
                MyAPIGateway.Physics.CastRay(from, to, ThicknessRayHits, 0);
                RememberTypedRaycastMode();
                if (ThicknessRayHits.Count == 0)
                    return true;

                int limit = Math.Max(1, maxHits);
                for (int i = 0; i < ThicknessRayHits.Count && ThicknessHitDistances.Count < limit; i++)
                {
                    VRage.Game.ModAPI.IHitInfo hit = ThicknessRayHits[i];
                    if (hit == null || !hit.Position.IsValid())
                        continue;

                    float distance = Clamp((float)(Vector3D.Dot(hit.Position - from, ray) / rayLengthSquared * rayLength), 0f, (float)rayLength);
                    ThicknessHitDistances.Add(distance);
                }

                if (ThicknessHitDistances.Count == 0)
                    return true;

                ThicknessHitDistances.Sort();
                MergeCloseHitDistances(ThicknessHitDistances);
                blockedLength = SumPairedHitThickness(ThicknessHitDistances, (float)rayLength);
                return true;
            }
            catch (Exception ex)
            {
                DisableTypedRaycast(ex);
                return false;
            }
        }

        private static void MergeCloseHitDistances(List<float> distances)
        {
            if (distances == null || distances.Count <= 1)
                return;

            int write = 1;
            float previous = distances[0];
            for (int read = 1; read < distances.Count; read++)
            {
                float current = distances[read];
                if (current - previous <= HitDistanceMergeMeters)
                    continue;

                distances[write++] = current;
                previous = current;
            }

            if (write < distances.Count)
                distances.RemoveRange(write, distances.Count - write);
        }

        private static float SumPairedHitThickness(List<float> distances, float rayLength)
        {
            float total = 0f;
            int i = 0;
            while (i < distances.Count)
            {
                float start = Clamp(distances[i], 0f, rayLength);
                float span = MinStructuralHitThicknessMeters;
                if (i + 1 < distances.Count)
                {
                    float end = Clamp(distances[i + 1], 0f, rayLength);
                    float candidateSpan = Math.Max(0f, end - start);
                    if (candidateSpan <= MaxPairedStructuralThicknessMeters)
                    {
                        span = Math.Max(candidateSpan, MinStructuralHitThicknessMeters);
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }

                total += Math.Min(span, Math.Max(0f, rayLength - start));
                if (total >= rayLength)
                    return rayLength;
            }

            return Clamp(total, 0f, rayLength);
        }

        public static float EstimateVoxelBlockedLength(Vector3D from, Vector3D to, float segmentLength, int maxSegments)
        {
            Vector3D path = to - from;
            double length = path.Length();
            if (length <= 0.05)
                return 0f;

            try
            {
                VRage.Game.ModAPI.IMyVoxelMaps voxelMaps = MyAPIGateway.Session?.VoxelMaps;
                if (voxelMaps == null)
                    return 0f;

                segmentLength = Math.Max(0.25f, segmentLength);
                maxSegments = Math.Max(1, maxSegments);
                int segments = Math.Max(1, Math.Min(maxSegments, (int)Math.Ceiling(length / segmentLength)));
                float blockedLength = 0f;
                double previousT = 0.0;
                for (int i = 0; i < segments; i++)
                {
                    double currentT = (i + 1) / (double)segments;
                    Vector3D midpoint = Vector3D.Lerp(from, to, (previousT + currentT) * 0.5);
                    BoundingSphereD sphere = new BoundingSphereD(midpoint, 0.65);
                    IMyVoxelBase voxel = voxelMaps.GetOverlappingWithSphere(ref sphere);
                    if (VoxelSegmentIntersectsContent(voxel, Vector3D.Lerp(from, to, previousT), Vector3D.Lerp(from, to, currentT)))
                        blockedLength += (float)(length * (currentT - previousT));

                    previousT = currentT;
                }

                return blockedLength;
            }
            catch (Exception ex)
            {
                if (!_loggedVoxelProbeException)
                {
                    _loggedVoxelProbeException = true;
                    V2DebugLog.WriteEvent("player-env-voxel-probe-error", ex.GetType().Name + ": " + ex.Message);
                }

                return 0f;
            }
        }

        private static bool VoxelSegmentIntersectsContent(IMyVoxelBase voxel, Vector3D from, Vector3D to)
        {
            if (voxel?.Storage == null)
                return false;

            try
            {
                MyVoxelBase voxelBase = voxel as MyVoxelBase;
                if (voxelBase != null)
                {
                    Vector3D center = (from + to) * 0.5;
                    BoundingBoxD localBox = BoundingBoxD.CreateFromPoints(new[] { from - center, to - center });
                    localBox.Inflate(0.35);
                    VRage.MyTuple<float, float> content = voxelBase.GetVoxelContentInBoundingBox_Fast(localBox, MatrixD.CreateTranslation(center), true, 0.01f);
                    return content.Item1 > 0f || content.Item2 > 0f;
                }

                Vector3D origin = voxel.PositionLeftBottomCorner;
                LineD localLine = new LineD(from - origin, to - origin);
                return voxel.Storage.Intersect(ref localLine);
            }
            catch
            {
                return false;
            }
        }

        public static float CalculateThicknessTransmission(float blockedMeters, float thicknessScale)
        {
            blockedMeters = Math.Max(0f, blockedMeters);
            thicknessScale = Math.Max(0.1f, thicknessScale);
            return Clamp01((float)Math.Exp(-blockedMeters / thicknessScale));
        }

        // Builds the raw blocked intervals along a debug ray, split by what contributes the thickness:
        // structure (grid blocks, via the physics hit list) and voxel terrain (per-segment content probe).
        // Intervals are returned as fractions of from->to so the overlay can re-project them onto the live
        // endpoints each frame without re-casting. Mirrors the primitives the real block occlusion probe
        // uses (EstimateBlockedLength / EstimateVoxelBlockedLength) so the colours match the filter input.
        public static bool TryProbeThicknessIntervals(Vector3D from, Vector3D to, bool includeVoxels, List<ThicknessInterval> structureFractions, List<ThicknessInterval> voxelFractions)
        {
            structureFractions?.Clear();
            voxelFractions?.Clear();

            Vector3D path = to - from;
            float rayLength = (float)path.Length();
            if (rayLength <= 0.1f)
                return false;

            if (structureFractions != null)
            {
                _thicknessStructureMeters.Clear();
                CollectStructureIntervals(from, to, 24, rayLength, _thicknessStructureMeters);
                AppendAsFractions(_thicknessStructureMeters, rayLength, structureFractions);
            }

            if (includeVoxels && voxelFractions != null)
                CollectVoxelIntervalFractions(from, to, 0.75f, 48, voxelFractions);

            return true;
        }

        private static void CollectStructureIntervals(Vector3D from, Vector3D to, int maxHits, float rayLength, List<ThicknessInterval> intervals)
        {
            if (!_typedCastRayDisabled && TryCollectStructureIntervalsFromHitList(from, to, maxHits, rayLength, intervals))
                return;

            CollectStructureIntervalsBySampling(from, to, rayLength, intervals);
        }

        private static bool TryCollectStructureIntervalsFromHitList(Vector3D from, Vector3D to, int maxHits, float rayLength, List<ThicknessInterval> intervals)
        {
            if (_typedCastRayDisabled)
                return false;

            Vector3D ray = to - from;
            double rayLengthD = ray.Length();
            double rayLengthSquared = ray.LengthSquared();
            if (rayLengthD <= 0.05 || rayLengthSquared <= 0.0001)
                return true;

            try
            {
                if (MyAPIGateway.Physics == null)
                    return false;

                ThicknessRayHits.Clear();
                ThicknessHitDistances.Clear();
                MyAPIGateway.Physics.CastRay(from, to, ThicknessRayHits, 0);
                RememberTypedRaycastMode();
                if (ThicknessRayHits.Count == 0)
                    return true;

                int limit = Math.Max(1, maxHits);
                for (int i = 0; i < ThicknessRayHits.Count && ThicknessHitDistances.Count < limit; i++)
                {
                    VRage.Game.ModAPI.IHitInfo hit = ThicknessRayHits[i];
                    if (hit == null || !hit.Position.IsValid())
                        continue;

                    float distance = Clamp((float)(Vector3D.Dot(hit.Position - from, ray) / rayLengthSquared * rayLengthD), 0f, rayLength);
                    ThicknessHitDistances.Add(distance);
                }

                if (ThicknessHitDistances.Count == 0)
                    return true;

                ThicknessHitDistances.Sort();
                MergeCloseHitDistances(ThicknessHitDistances);
                CollectPairedHitIntervals(ThicknessHitDistances, rayLength, intervals);
                return true;
            }
            catch (Exception ex)
            {
                DisableTypedRaycast(ex);
                return false;
            }
        }

        // Mirror of SumPairedHitThickness, but emits the [start, start+span] ranges instead of summing them
        // so the overlay can draw exactly the stretches the thickness estimate counts.
        private static void CollectPairedHitIntervals(List<float> distances, float rayLength, List<ThicknessInterval> intervals)
        {
            int i = 0;
            while (i < distances.Count)
            {
                float start = Clamp(distances[i], 0f, rayLength);
                float span = MinStructuralHitThicknessMeters;
                if (i + 1 < distances.Count)
                {
                    float end = Clamp(distances[i + 1], 0f, rayLength);
                    float candidateSpan = Math.Max(0f, end - start);
                    if (candidateSpan <= MaxPairedStructuralThicknessMeters)
                    {
                        span = Math.Max(candidateSpan, MinStructuralHitThicknessMeters);
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }

                float segEnd = Math.Min(rayLength, start + span);
                if (segEnd > start)
                    intervals.Add(new ThicknessInterval(start, segEnd));
            }
        }

        private static void CollectStructureIntervalsBySampling(Vector3D from, Vector3D to, float rayLength, List<ThicknessInterval> intervals)
        {
            int segments = Math.Max(1, Math.Min(24, (int)Math.Ceiling(rayLength / 0.75f)));
            Vector3D previous = from;
            float previousDistance = 0f;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                Vector3D current = Vector3D.Lerp(from, to, t);
                float distance = t * rayLength;
                if (TryRayBlocked(previous, current, out bool blocked) && blocked)
                    intervals.Add(new ThicknessInterval(previousDistance, distance));

                previous = current;
                previousDistance = distance;
            }
        }

        private static void CollectVoxelIntervalFractions(Vector3D from, Vector3D to, float segmentLength, int maxSegments, List<ThicknessInterval> fractions)
        {
            try
            {
                VRage.Game.ModAPI.IMyVoxelMaps voxelMaps = MyAPIGateway.Session?.VoxelMaps;
                if (voxelMaps == null)
                    return;

                double length = (to - from).Length();
                if (length <= 0.05)
                    return;

                segmentLength = Math.Max(0.25f, segmentLength);
                maxSegments = Math.Max(1, maxSegments);
                int segments = Math.Max(1, Math.Min(maxSegments, (int)Math.Ceiling(length / segmentLength)));
                double previousT = 0.0;
                for (int i = 0; i < segments; i++)
                {
                    double currentT = (i + 1) / (double)segments;
                    Vector3D midpoint = Vector3D.Lerp(from, to, (previousT + currentT) * 0.5);
                    BoundingSphereD sphere = new BoundingSphereD(midpoint, 0.65);
                    IMyVoxelBase voxel = voxelMaps.GetOverlappingWithSphere(ref sphere);
                    if (VoxelSegmentIntersectsContent(voxel, Vector3D.Lerp(from, to, previousT), Vector3D.Lerp(from, to, currentT)))
                        fractions.Add(new ThicknessInterval((float)previousT, (float)currentT));

                    previousT = currentT;
                }
            }
            catch
            {
            }
        }

        private static void AppendAsFractions(List<ThicknessInterval> meters, float rayLength, List<ThicknessInterval> fractions)
        {
            if (rayLength <= 0.0001f)
                return;

            for (int i = 0; i < meters.Count; i++)
                fractions.Add(new ThicknessInterval(Clamp01(meters[i].Start / rayLength), Clamp01(meters[i].End / rayLength)));
        }

        private static void BuildStableBasis(Vector3D up, out Vector3D right, out Vector3D forward)
        {
            if (!up.IsValid() || up.LengthSquared() <= 0.0001)
                up = Vector3D.Up;

            up.Normalize();
            Vector3D reference = Math.Abs(Vector3D.Dot(up, Vector3D.Forward)) < 0.85 ? Vector3D.Forward : Vector3D.Right;
            right = Vector3D.Cross(reference, up);
            if (!right.IsValid() || right.LengthSquared() <= 0.0001)
                right = Vector3D.Right;
            else
                right.Normalize();

            forward = Vector3D.Cross(up, right);
            if (!forward.IsValid() || forward.LengthSquared() <= 0.0001)
                forward = Vector3D.Forward;
            else
                forward.Normalize();
        }

        public static bool TryRayBlocked(Vector3D from, Vector3D to, out bool blocked)
        {
            return TryRaycast(from, to, out blocked, out _);
        }

        public static bool TryRaycast(Vector3D from, Vector3D to, out bool blocked, out float hitDistance)
        {
            blocked = false;
            hitDistance = 0f;
            if (_castRayDisabled)
                return false;

            if (TryRaycastTypedHitList(from, to, out blocked, out hitDistance))
                return true;

            try
            {
                object physics = MyAPIGateway.Physics;
                if (physics == null)
                    return false;

                if (!ResolveCastRay(physics.GetType()))
                    return false;

                object[] args = CreateCastRayArgs(from, to);
                object result = _castRayMethod.Invoke(physics, args);
                object hitObject = ExtractCastRayHit(args, result);
                blocked = InterpretCastRayResult(args, result);
                if (blocked && IsFirstGridHitOpenDoor(from, to))
                {
                    blocked = false;
                    hitDistance = 0f;
                    return true;
                }

                if (blocked)
                    hitDistance = ResolveHitDistance(from, to, hitObject);
                else if (TryFindFirstGridHit(from, to, out double gridDistance, out MySlimBlock gridBlock))
                {
                    if (!IsOpenDoorBlock(gridBlock))
                    {
                        blocked = true;
                        hitDistance = Clamp((float)gridDistance, 0f, (float)Vector3D.Distance(from, to));
                    }
                }

                return true;
            }
            catch
            (Exception ex)
            {
                if (!_loggedCastRayException)
                {
                    _loggedCastRayException = true;
                    V2DebugLog.WriteEvent("player-env-raycast-error", ex.GetType().Name + ": " + ex.Message);
                }

                if (++_castRayErrors >= 5)
                {
                    _castRayDisabled = true;
                    _castRayModeName = "disabled";
                }

                return false;
            }
        }

        private static bool TryRaycastTypedHitList(Vector3D from, Vector3D to, out bool blocked, out float hitDistance)
        {
            blocked = false;
            hitDistance = 0f;
            if (_typedCastRayDisabled)
                return false;

            try
            {
                if (MyAPIGateway.Physics == null)
                    return false;

                DirectRayHits.Clear();
                MyAPIGateway.Physics.CastRay(from, to, DirectRayHits, 0);
                RememberTypedRaycastMode();
                blocked = TryGetClosestHitDistance(from, to, DirectRayHits, out hitDistance);
                if (blocked && IsFirstGridHitOpenDoor(from, to))
                {
                    blocked = false;
                    hitDistance = 0f;
                    return true;
                }

                if (!blocked && TryFindFirstGridHit(from, to, out double gridDistance, out MySlimBlock gridBlock))
                {
                    if (!IsOpenDoorBlock(gridBlock))
                    {
                        blocked = true;
                        hitDistance = Clamp((float)gridDistance, 0f, (float)Vector3D.Distance(from, to));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                DisableTypedRaycast(ex);
                return false;
            }
        }

        private static bool TryGetClosestHitDistance(Vector3D from, Vector3D to, List<VRage.Game.ModAPI.IHitInfo> hits, out float hitDistance)
        {
            hitDistance = 0f;
            if (hits == null || hits.Count == 0)
                return false;

            Vector3D ray = to - from;
            double rayLength = ray.Length();
            double rayLengthSquared = ray.LengthSquared();
            if (rayLength <= 0.05 || rayLengthSquared <= 0.0001)
                return false;

            double best = double.MaxValue;
            for (int i = 0; i < hits.Count; i++)
            {
                VRage.Game.ModAPI.IHitInfo hit = hits[i];
                if (hit == null || !hit.Position.IsValid())
                    continue;

                double distance = Vector3D.Dot(hit.Position - from, ray) / rayLengthSquared * rayLength;
                if (distance >= 0.0 && distance < best)
                    best = distance;
            }

            if (best == double.MaxValue)
                return false;

            hitDistance = Clamp((float)best, 0f, (float)rayLength);
            return true;
        }

        private static void RememberTypedRaycastMode()
        {
            _castRayModeName = "typed-hit-list";
            _castRayMode = CastRayMode.TypedHitList;
            if (_loggedCastRayResolved)
                return;

            _loggedCastRayResolved = true;
            V2DebugLog.WriteEvent("player-env-raycast", "typed-hit-list MyAPIGateway.Physics.CastRay(Vector3D, Vector3D, List<IHitInfo>, int)");
        }

        private static void DisableTypedRaycast(Exception ex)
        {
            _typedCastRayDisabled = true;
            if (_loggedTypedCastRayException)
                return;

            _loggedTypedCastRayException = true;
            V2DebugLog.WriteEvent("player-env-raycast-typed-error", ex.GetType().Name + ": " + ex.Message);
        }

        private static float ResolveHitDistance(Vector3D from, Vector3D to, object hitObject)
        {
            if (TryExtractHitPosition(hitObject, out Vector3D hitPosition))
                return Clamp((float)Vector3D.Distance(from, hitPosition), 0f, (float)Vector3D.Distance(from, to));

            if (TryFindFirstGridHitDistance(from, to, out double gridDistance))
                return Clamp((float)gridDistance, 0f, (float)Vector3D.Distance(from, to));

            return (float)Vector3D.Distance(from, to);
        }

        private static object ExtractCastRayHit(object[] args, object result)
        {
            switch (_castRayMode)
            {
                case CastRayMode.OutHit:
                    return args != null && args.Length >= 3 ? args[2] : null;
                case CastRayMode.HitList:
                    IList list = args != null && args.Length >= 3 ? args[2] as IList : null;
                    return list != null && list.Count > 0 ? list[0] : null;
                case CastRayMode.ReturnHit:
                    return result is bool ? null : result;
                default:
                    return null;
            }
        }

        private static bool TryExtractHitPosition(object hitObject, out Vector3D position)
        {
            position = Vector3D.Zero;
            if (hitObject == null)
                return false;

            if (hitObject is Vector3D vector3D)
            {
                position = vector3D;
                return position.IsValid();
            }

            if (hitObject is Vector3 vector3)
            {
                position = vector3;
                return position.IsValid();
            }

            string[] names = { "Position", "HitPosition", "HitPositionWorld", "PositionWorld", "Point", "Location" };
            Type type = hitObject.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                object value = null;
                try
                {
                    PropertyInfo property = type.GetProperty(names[i], InstanceMembers);
                    if (property != null)
                        value = property.GetValue(hitObject, null);
                    else
                    {
                        FieldInfo field = type.GetField(names[i], InstanceMembers);
                        if (field != null)
                            value = field.GetValue(hitObject);
                    }
                }
                catch
                {
                    value = null;
                }

                if (value is Vector3D v3d)
                {
                    position = v3d;
                    return position.IsValid();
                }

                if (value is Vector3 v3)
                {
                    position = v3;
                    return position.IsValid();
                }
            }

            return false;
        }

        private static bool TryFindFirstGridHitDistance(Vector3D from, Vector3D to, out double distance)
        {
            MySlimBlock block;
            return TryFindFirstGridHit(from, to, out distance, out block);
        }

        private static bool IsFirstGridHitOpenDoor(Vector3D from, Vector3D to)
        {
            double distance;
            MySlimBlock block;
            return TryFindFirstGridHit(from, to, out distance, out block) && IsOpenDoorBlock(block);
        }

        private static bool TryFindFirstGridHit(Vector3D from, Vector3D to, out double distance, out MySlimBlock block)
        {
            if (!TryFindDoorProbeGrid(from, to, out MyCubeGrid grid) || grid == null)
            {
                distance = 0.0;
                block = null;
                return false;
            }

            try
            {
                LineD line = new LineD(from, to);
                Vector3D? hit = grid.GetLineIntersectionExactAll(ref line, out distance, out block);
                return hit.HasValue;
            }
            catch
            {
                distance = 0.0;
                block = null;
                return false;
            }
        }

        // Resolves the first grid block a ray crosses (its grid + cell) for the thin-seal barrier test.
        // Wraps TryFindFirstGridHit; fails open (returns false -> pure thickness) on any miss/null.
        internal static bool TryGetFirstGridHitFace(Vector3D from, Vector3D to, out VRage.Game.ModAPI.IMyCubeGrid grid, out Vector3I cell)
        {
            grid = null;
            cell = default(Vector3I);
            if (!TryFindFirstGridHit(from, to, out double _, out MySlimBlock block) || block == null)
                return false;

            grid = block.CubeGrid as VRage.Game.ModAPI.IMyCubeGrid;
            cell = block.Position;
            return grid != null;
        }

        private static bool TryFindDoorProbeGrid(Vector3D from, Vector3D to, out MyCubeGrid grid)
        {
            grid = null;
            V2AudioListenerState listener = AudioEngineV2Runtime.Listener;
            long preferredGridId = listener.GridEntityId != 0L ? listener.GridEntityId : listener.ContactGridEntityId;
            if (preferredGridId != 0L && TryGetGridById(preferredGridId, out grid))
                return true;

            Vector3D mid = (from + to) * 0.5;
            return TryFindGridForPosition(from, preferredGridId, out grid)
                || TryFindGridForPosition(to, preferredGridId, out grid)
                || TryFindGridForPosition(mid, preferredGridId, out grid);
        }

        private static bool IsOpenDoorBlock(MySlimBlock block)
        {
            MyCubeBlock fat = block?.FatBlock;
            if (fat == null)
                return false;

            Sandbox.ModAPI.Ingame.IMyDoor door = fat as Sandbox.ModAPI.Ingame.IMyDoor;
            if (door == null)
                return false;

            try
            {
                Sandbox.ModAPI.Ingame.DoorStatus status = door.Status;
                return status == Sandbox.ModAPI.Ingame.DoorStatus.Open
                    || status == Sandbox.ModAPI.Ingame.DoorStatus.Opening;
            }
            catch
            {
                return false;
            }
        }

        private static bool ResolveCastRay(Type physicsType)
        {
            if (_castRayResolved)
                return _castRayMethod != null;

            _castRayResolved = true;
            if (physicsType == null)
                return false;

            foreach (MethodInfo method in EnumerateRaycastMethods(physicsType))
            {
                if (!string.Equals(method.Name, "CastRay", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2
                    || parameters[0].ParameterType != typeof(Vector3D)
                    || parameters[1].ParameterType != typeof(Vector3D))
                    continue;

                if (parameters.Length >= 3 && parameters[2].ParameterType.IsByRef)
                {
                    _castRayMethod = method;
                    _castRayMode = CastRayMode.OutHit;
                    _castRayModeName = "out-hit";
                    LogCastRayResolved(method);
                    return true;
                }

                if (parameters.Length >= 3 && typeof(IList).IsAssignableFrom(parameters[2].ParameterType))
                {
                    _castRayMethod = method;
                    _castRayMode = CastRayMode.HitList;
                    _castRayModeName = "hit-list";
                    LogCastRayResolved(method);
                    return true;
                }

                if (parameters.Length == 2 && method.ReturnType != typeof(void))
                {
                    _castRayMethod = method;
                    _castRayMode = CastRayMode.ReturnHit;
                    _castRayModeName = "return-hit";
                    LogCastRayResolved(method);
                    return true;
                }
            }

            _castRayModeName = "missing";
            if (!_loggedCastRayMissing)
            {
                _loggedCastRayMissing = true;
                V2DebugLog.WriteEvent("player-env-raycast-missing", "No usable CastRay on " + physicsType.FullName);
            }

            return false;
        }

        private static IEnumerable<MethodInfo> EnumerateRaycastMethods(Type physicsType)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            MethodInfo[] methods = physicsType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                string key = method.ToString();
                if (seen.Add(key))
                    yield return method;
            }

            Type[] interfaces = physicsType.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                methods = interfaces[i].GetMethods(BindingFlags.Instance | BindingFlags.Public);
                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    string key = method.DeclaringType.FullName + ":" + method;
                    if (seen.Add(key))
                        yield return method;
                }
            }
        }

        private static void LogCastRayResolved(MethodInfo method)
        {
            if (_loggedCastRayResolved)
                return;

            _loggedCastRayResolved = true;
            V2DebugLog.WriteEvent("player-env-raycast", _castRayModeName + " " + DescribeMethod(method));
        }

        private static string DescribeMethod(MethodInfo method)
        {
            if (method == null)
                return "?";

            ParameterInfo[] parameters = method.GetParameters();
            List<string> parts = new List<string>();
            for (int i = 0; i < parameters.Length; i++)
                parts.Add(parameters[i].ParameterType.Name + " " + parameters[i].Name);

            return method.DeclaringType.FullName + "." + method.Name + "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static object[] CreateCastRayArgs(Vector3D from, Vector3D to)
        {
            ParameterInfo[] parameters = _castRayMethod.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = from;
            args[1] = to;

            for (int i = 2; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                Type elementType = parameterType.IsByRef ? parameterType.GetElementType() : parameterType;
                if (_castRayMode == CastRayMode.HitList && i == 2)
                    args[i] = CreateHitList(elementType);
                else if (elementType == typeof(int))
                    args[i] = 0;
                else if (elementType == typeof(bool))
                    args[i] = false;
                else if (parameters[i].HasDefaultValue)
                    args[i] = parameters[i].DefaultValue;
                else
                    args[i] = null;
            }

            return args;
        }

        private static object CreateHitList(Type parameterType)
        {
            if (parameterType == null)
                return null;

            if (!parameterType.IsInterface && !parameterType.IsAbstract)
                return Activator.CreateInstance(parameterType);

            if (parameterType.IsGenericType)
            {
                Type[] args = parameterType.GetGenericArguments();
                if (args.Length == 1)
                    return Activator.CreateInstance(typeof(List<>).MakeGenericType(args[0]));
            }

            return new ArrayList();
        }

        private static bool InterpretCastRayResult(object[] args, object result)
        {
            switch (_castRayMode)
            {
                case CastRayMode.OutHit:
                    if (result is bool boolResult && !boolResult)
                        return false;
                    return args.Length >= 3 && args[2] != null;
                case CastRayMode.HitList:
                    IList list = args.Length >= 3 ? args[2] as IList : null;
                    return list != null && list.Count > 0;
                case CastRayMode.ReturnHit:
                    if (result is bool blocked)
                        return blocked;
                    return result != null;
                default:
                    return false;
            }
        }

        private static RoomAcousticEstimate CalculateRoomAcoustics(RoomRayAccumulator probe, OxygenProbe oxygenProbe, float rayLength, RealisticSoundPlusSettings settings)
        {
            RoomAcousticEstimate estimate = new RoomAcousticEstimate
            {
                Source = "none"
            };

            if (probe == null || probe.Rays <= 0)
                return estimate;

            bool sealedRoom = oxygenProbe.RoomPresent && oxygenProbe.RoomAirtight;
            List<float> distances = probe.GetRoomDistances(sealedRoom);
            if (distances.Count == 0)
                return estimate;

            distances.Sort();
            float near = PercentileSorted(distances, 0.20f);
            float median = PercentileSorted(distances, 0.50f);
            float p75 = PercentileSorted(distances, 0.75f);
            float p90 = PercentileSorted(distances, 0.90f);
            float mean = probe.DistanceSum / Math.Max(1, probe.Rays);
            float closedFraction = probe.Hits / (float)Math.Max(1, probe.Rays);
            float radius = Clamp(p75 * 0.45f + p90 * 0.35f + median * 0.20f, 0.8f, Math.Max(1f, rayLength));

            // Sealed-room geometry override (V2GridStructureProbe): where the gas system has an exact airtight
            // room, blend its cell-set geometry into the ray-derived size/wall-distances — exact and jitter-free.
            // The ray estimate still contributes at w<1 so the sealed/unsealed boundary ramps (further smoothed
            // downstream by SmoothReverbRoomSample). Geometry is clamped to an ABSOLUTE ceiling, NOT rayLength,
            // so a hangar larger than the ray horizon is not capped back down to it.
            bool sealedGeoApplied = false;
            if (sealedRoom && oxygenProbe.CellResolved && settings != null &&
                V2GridStructureProbe.TryGetRoomGeometry(oxygenProbe.Grid, oxygenProbe.Cell, out V2RoomGeometry geo) &&
                geo.Available && geo.Airtight)
            {
                const float geometryCeiling = 250f;
                float w = Clamp01(settings.ReverbSealedGeometryWeight);
                radius = Lerp(radius, Clamp(geo.EquivalentRadius, 0.8f, geometryCeiling), w);
                near = Lerp(near, Clamp(geo.NearWallDistance, 0.35f, geometryCeiling), w);
                median = Lerp(median, Clamp(geo.FarWallDistance, near, geometryCeiling), w);
                if (near > median)
                    near = median; // keep percentile ordering well-formed after the blend
                sealedGeoApplied = true;
            }

            float roomSize = NormalizeRoomRadius(radius, rayLength);
            bool usable = sealedRoom || probe.Hits >= Math.Max(8, probe.Rays / 3);
            if (!usable)
                return estimate;

            float roomSpread = Clamp01((p90 - near) / Math.Max(0.5f, p90));
            float hitSpread = Clamp01((p90 - p75) / Math.Max(0.5f, p90));
            float nearPresence = 1f - Clamp01((near - 0.5f) / 8f);
            float openFraction = Clamp01(1f - closedFraction);

            // Sabine sphere simplification: RT60 = 0.161 * V / (S * a), and V / S is radius / 3.
            float surfaceAbsorption = Clamp(0.115f + hitSpread * 0.025f, 0.08f, 0.18f);
            float apertureAbsorption = sealedRoom ? 0f : openFraction * 0.72f;
            float absorption = Clamp(surfaceAbsorption + apertureAbsorption, 0.035f, 0.95f);
            float rt60 = 0.0537f * radius / absorption;

            float predelayMs = Clamp(2000f * Math.Max(0.35f, near) / SpeedOfSoundMetersPerSecond, 3f, 160f);
            float lateDistance = Math.Max(0.35f, median - near);
            float lateDelayMs = Clamp(2000f * lateDistance / SpeedOfSoundMetersPerSecond, 4f, 180f);
            float density = Clamp(94f + roomSpread * 6f - openFraction * 3f, 86f, 100f);
            float diffusion = Clamp(0.78f + roomSpread * 0.16f + hitSpread * 0.06f, 0.70f, 0.98f);
            float earlyGain = Lerp(-6f, 5f, nearPresence) - openFraction * 5f;
            float toneHz = 12800f + roomSpread * 2400f - surfaceAbsorption * 5200f;
            float hfDb = -1.25f - surfaceAbsorption * 5f - openFraction * 2f;
            if (!sealedRoom)
            {
                float openScale = Clamp(closedFraction, 0.35f, 1f);
                rt60 *= Clamp(0.45f + openScale * 0.55f, 0.45f, 1f);
            }
            float tailGain = 1f + rt60 * 1.15f + roomSize * 3.5f - openFraction * 14f;

            estimate.Available = true;
            estimate.Source = sealedGeoApplied ? "sealed-geo" : (sealedRoom ? "sealed-ray" : "ray");
            estimate.Rays = probe.Rays;
            estimate.Hits = probe.Hits;
            estimate.OpenRays = probe.OpenRays;
            estimate.NearDistance = near;
            estimate.MedianDistance = median;
            estimate.P75Distance = p75;
            estimate.P90Distance = p90;
            estimate.MeanDistance = mean;
            estimate.ClosedFraction = closedFraction;
            estimate.EquivalentRadius = radius;
            estimate.RoomSize = roomSize;
            estimate.Diffusion = Clamp01(diffusion);
            estimate.DecaySeconds = Clamp(rt60, 0.35f, 18f);
            estimate.EarlyGainDb = Clamp(earlyGain, -12f, 8f);
            estimate.TailGainDb = Clamp(tailGain, -12f, 14f);
            estimate.PredelayMs = predelayMs;
            estimate.LateDelayMs = lateDelayMs;
            estimate.Density = Clamp(density, 35f, 100f);
            estimate.ToneHz = Clamp(toneHz, 3000f, 18000f);
            estimate.HighFrequencyDb = Clamp(hfDb, -10f, 0f);
            return estimate;
        }

        private static float PercentileSorted(List<float> sortedValues, float percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
                return 0f;
            if (sortedValues.Count == 1)
                return sortedValues[0];

            float index = Clamp01(percentile) * (sortedValues.Count - 1);
            int low = (int)Math.Floor(index);
            int high = Math.Min(sortedValues.Count - 1, low + 1);
            float t = index - low;
            return sortedValues[low] + (sortedValues[high] - sortedValues[low]) * t;
        }

        private static float NormalizeRoomRadius(float radius, float rayLength)
        {
            float minRadius = 1.2f;
            float maxRadius = Math.Max(18f, Math.Min(Math.Max(4f, rayLength), 65f));
            float logMin = (float)Math.Log(minRadius);
            float logMax = (float)Math.Log(maxRadius);
            float logValue = (float)Math.Log(Clamp(radius, minRadius, maxRadius));
            return Clamp01((logValue - logMin) / Math.Max(0.001f, logMax - logMin));
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * Clamp01(amount);
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return min;
            if (value <= min)
                return min;
            return value >= max ? max : value;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }

        private static float ApplyOcclusionStrength(float amount, float strength)
        {
            amount = Clamp01(amount);
            if (amount <= 0f)
                return 0f;

            strength = Math.Max(0f, strength);
            if (strength <= 1f)
                return Clamp01(amount * strength);

            return Clamp01(1f - (float)Math.Pow(1f - amount, strength));
        }

        private sealed class RoomRayAccumulator
        {
            private readonly List<float> _hitDistances = new List<float>(64);
            private readonly List<float> _roomDistances = new List<float>(64);
            private readonly float _rayLength;

            public RoomRayAccumulator(float rayLength)
            {
                _rayLength = Math.Max(1f, rayLength);
            }

            public int Rays;
            public int Hits;
            public int OpenRays;
            public float DistanceSum;

            public void Add(bool available, bool hit, float distance)
            {
                if (!available)
                    return;

                Rays++;
                float safeDistance = Clamp(distance, 0.35f, _rayLength);
                if (hit)
                {
                    Hits++;
                    _hitDistances.Add(safeDistance);
                }
                else
                {
                    OpenRays++;
                    safeDistance = _rayLength;
                }

                DistanceSum += safeDistance;
                _roomDistances.Add(safeDistance);
            }

            public List<float> GetRoomDistances(bool sealedRoom)
            {
                if (sealedRoom)
                    return new List<float>(_hitDistances);

                return _hitDistances.Count >= Math.Max(6, Rays / 3)
                    ? new List<float>(_roomDistances)
                    : new List<float>(_hitDistances);
            }
        }

        private struct ReverbRayDebugSample
        {
            public Vector3D From;
            public Vector3D To;
            public bool Available;
            public bool Hit;
        }

        private struct RollingRaySample
        {
            public DateTime UpdatedUtc;
            public float Weight;
            public bool RayAvailable;
            public bool RayHit;
            public float RayDistance;
            public bool IncludeEnvironment;
            public bool EnvironmentAvailable;
            public bool EnvironmentBlocked;
            public float OpenWeight;
            public float BlockedMeters;
            public float VoxelMeters;
            public Vector3D DebugFrom;
            public Vector3D DebugTo;
        }

        private struct RoomAcousticEstimate
        {
            public bool Available;
            public string Source;
            public int Rays;
            public int Hits;
            public int OpenRays;
            public float NearDistance;
            public float MedianDistance;
            public float P75Distance;
            public float P90Distance;
            public float MeanDistance;
            public float ClosedFraction;
            public float EquivalentRadius;
            public float RoomSize;
            public float Diffusion;
            public float DecaySeconds;
            public float EarlyGainDb;
            public float TailGainDb;
            public float PredelayMs;
            public float LateDelayMs;
            public float Density;
            public float ToneHz;
            public float HighFrequencyDb;
        }

        private struct OxygenProbe
        {
            public bool Available;
            public bool RoomPresent;
            public bool RoomAirtight;
            public bool RoomAtPositionAirtight;
            public bool RoomDirty;
            public int RoomProbeCount;
            public int AirtightProbeCount;
            public float LocalOxygen;
            public float RoomOxygen;
            public bool ExactLocalOxygenAvailable;
            public string Source;
            public long GridEntityId;
            public int CandidateGridCount;
            public object RoomKey;
            public VRage.Game.ModAPI.IMyCubeGrid Grid;
            public Vector3I Cell;
            public bool CellResolved;
        }

        private enum CastRayMode
        {
            Unknown,
            TypedHitList,
            OutHit,
            HitList,
            ReturnHit
        }
    }
}
