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

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2PlayerEnvironmentTelemetry
    {
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);
        private const int SphereRingSegments = 12;
        private const float SphereRayWeight = 1.00f;
        private const double HighRingDegrees = 30.0;
        private const double UpperRingDegrees = 60.0;
        private const double EquatorRingDegrees = 90.0;
        private const double LowerRingDegrees = 120.0;
        private const double LowRingDegrees = 150.0;
        private const double OxygenGridSearchRange = 6.0;
        private const float VoxelOcclusionEpsilon = 0.001f;
        private const double EnvironmentVoxelMinSkyDot = 0.35;
        private const float EnvironmentVoxelMeterScale = 0.25f;
        private const float EnvironmentVoxelMinBlockedMeters = 0.10f;

        private static readonly HashSet<IMyEntity> GridSearchScratch = new HashSet<IMyEntity>();
        private static DateTime _lastUpdateUtc = DateTime.MinValue;
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
        private static bool _loggedVoxelProbeException;

        public static void Reset()
        {
            _lastUpdateUtc = DateTime.MinValue;
            _latest = default(V2PlayerEnvironmentSample);
            _castRayDisabled = false;
            _castRayErrors = 0;
            _loggedCastRayMissing = false;
            _loggedCastRayException = false;
            _loggedCastRayResolved = false;
            _loggedVoxelProbeException = false;
        }

        public static void Update(V2AudioListenerState listener)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastUpdateUtc < UpdateInterval)
                return;

            _lastUpdateUtc = now;
            _latest = Calculate(listener, SettingsManager.Current, now);
        }

        public static bool TryGetLatest(out V2PlayerEnvironmentSample sample)
        {
            sample = _latest;
            return sample.Valid && DateTime.UtcNow - sample.UpdatedUtc <= TimeSpan.FromSeconds(2);
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

        public static string FormatSummary()
        {
            if (!TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "No player environment sample yet.";

            return string.Format(
                CultureInfo.InvariantCulture,
                "windMuffle={0:0.00} exposure={1:0.00} audible={2:0.00} open={3:0.00} aperture={12:0.00} coverage={4:0.00} thick={11:0.0}m vox={13:0.0}m sealed={5}+{6:0.00} atm={7:0.00} rays={8}/{9} mode={10}",
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
                sample.VoxelBlockedMeters);
        }

        public static string FormatDetails()
        {
            if (!TryGetLatest(out V2PlayerEnvironmentSample sample))
                return "No live sample. Open a world and wait for the V2 listener update.";

            return string.Format(
                CultureInfo.InvariantCulture,
                "room={0}\noxygen={10}/{11} level={12:0.00} room={13:0.00} present={18} airtight={14} dirty={15} probes={19}/{20} grid={16}\nraycast={1}/{2} length={3:0}m blocked={4}/{5} weightedOpen={6:0.00}/{7:0.00} aperture={21:0.00} avgThick={17:0.0}m vox={22:0.0}m continuous={8:0.00} final={9:0.00}",
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
                sample.VoxelBlockedMeters);
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
            float voxelWeight = Math.Max(0f, settings.PlayerFilterVoxelOcclusionWeight);
            Vector3D probePosition = ResolveProbeOrigin(position);

            if (raycastAvailable)
            {
                Vector3D up = GetProbeUp(probePosition);
                BuildStableBasis(up, out Vector3D right, out Vector3D forward);
                ProbeDirection(probePosition, up, up, SphereRayWeight, rayLength, thicknessScale, voxelWeight, ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
                ProbeRing(probePosition, up, right, forward, HighRingDegrees, SphereRayWeight, rayLength, thicknessScale, voxelWeight, ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
                ProbeRing(probePosition, up, right, forward, UpperRingDegrees, SphereRayWeight, rayLength, thicknessScale, voxelWeight, ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
                ProbeRing(probePosition, up, right, forward, EquatorRingDegrees, SphereRayWeight, rayLength, thicknessScale, voxelWeight, ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
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

            return new V2PlayerEnvironmentSample
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
                ListenerMode = listener.ModeName
            };
        }

        private static OxygenProbe ProbeOxygen(Vector3D position, V2AudioListenerState listener)
        {
            OxygenProbe probe = new OxygenProbe
            {
                Source = "none",
                LocalOxygen = 0f,
                RoomOxygen = 0f
            };

            if (TryGetOxygenInPoint(position, out float pointOxygen))
            {
                probe.Available = true;
                probe.LocalOxygen = Clamp01(pointOxygen);
                probe.Source = "point";
            }

            long preferredGridId = listener.GridEntityId != 0L ? listener.GridEntityId : listener.ContactGridEntityId;
            if (!TryFindGridForPosition(position, preferredGridId, out MyCubeGrid grid) || grid == null)
                return probe;

            probe.Available = true;
            probe.GridEntityId = grid.EntityId;
            probe.Source = probe.Source == "none" ? "grid" : probe.Source + "+grid";

            if (!TryWorldToGridInteger(grid, position, out Vector3I cell))
                return probe;

            VRage.Game.ModAPI.IMyCubeGrid modGrid = grid as VRage.Game.ModAPI.IMyCubeGrid;
            if (modGrid == null)
                return probe;

            bool roomAtPositionAirtight = TryIsRoomAtPositionAirtight(modGrid, cell);
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

            probe.Source = probe.Source + "+gas";
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
            probe.RoomAirtight = probe.RoomAirtight || roomAtPositionAirtight || (!room.IsDirty && room.IsAirtight);
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
            probe.LocalOxygen = Math.Max(probe.LocalOxygen, probe.RoomOxygen);
            return probe;
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

                        bool candidateAirtight = cellAirtight || (!candidate.IsDirty && candidate.IsAirtight);
                        probe.RoomAirtight |= candidateAirtight;
                        airtight |= candidateAirtight;

                        try
                        {
                            float candidateOxygen = Clamp01(Math.Max(candidate.EnvironmentOxygen, candidate.OxygenLevel(gridSize)));
                            probe.RoomOxygen = Math.Max(probe.RoomOxygen, candidateOxygen);
                            probe.LocalOxygen = Math.Max(probe.LocalOxygen, candidateOxygen);
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

            if (oxygenProbe.Available && localOxygen > externalAtmosphere)
                return localOxygen;

            return externalAtmosphere;
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

        private static void ProbeRing(Vector3D position, Vector3D up, Vector3D right, Vector3D forward, double degreesFromUp, float weight, float rayLength, float thicknessScale, float voxelWeight, ref int open, ref int blocked, ref float openWeight, ref float totalWeight, ref float weightedBlockedMeters, ref float weightedVoxelBlockedMeters)
        {
            double radians = degreesFromUp * Math.PI / 180.0;
            double vertical = Math.Cos(radians);
            double horizontal = Math.Sin(radians);

            for (int i = 0; i < SphereRingSegments; i++)
            {
                double angle = (Math.PI * 2.0 * i) / SphereRingSegments;
                Vector3D tangent = right * Math.Cos(angle) + forward * Math.Sin(angle);
                Vector3D direction = up * vertical + tangent * horizontal;
                ProbeDirection(position, direction, up, weight, rayLength, thicknessScale, voxelWeight, ref open, ref blocked, ref openWeight, ref totalWeight, ref weightedBlockedMeters, ref weightedVoxelBlockedMeters);
            }
        }

        private static void ProbeDirection(Vector3D position, Vector3D direction, Vector3D skyUp, float weight, float rayLength, float thicknessScale, float voxelWeight, ref int open, ref int blocked, ref float openWeight, ref float totalWeight, ref float weightedBlockedMeters, ref float weightedVoxelBlockedMeters)
        {
            if (!direction.IsValid() || direction.LengthSquared() <= 0.0001)
                return;

            direction.Normalize();
            Vector3D from = position + direction * 0.85;
            Vector3D to = from + direction * rayLength;
            bool rayAvailable = TryRayBlocked(from, to, out bool hit);
            float rawVoxelMeters = ShouldUseEnvironmentVoxelOcclusion(direction, skyUp, voxelWeight)
                ? EstimateVoxelBlockedLength(from, to, 1.0f, 64)
                : 0f;
            float voxelMeters = rawVoxelMeters * voxelWeight * EnvironmentVoxelMeterScale;
            bool voxelHit = voxelMeters > EnvironmentVoxelMinBlockedMeters;
            if (!rayAvailable && !voxelHit)
                return;

            float safeWeight = Math.Max(0.001f, weight);
            totalWeight += safeWeight;
            if (hit || voxelHit)
            {
                blocked++;
                float blockedMeters = hit ? EstimateBlockedLength(from, to, 1.0f, 64) : 0f;
                if (voxelHit)
                    blockedMeters += voxelMeters * voxelWeight;

                if (blockedMeters <= 0.001f)
                    blockedMeters = Math.Max(0.1f, thicknessScale);

                weightedBlockedMeters += safeWeight * blockedMeters;
                weightedVoxelBlockedMeters += safeWeight * voxelMeters;
                openWeight += safeWeight * CalculateThicknessTransmission(blockedMeters, thicknessScale);
                return;
            }

            open++;
            openWeight += safeWeight;
        }

        private static bool ShouldUseEnvironmentVoxelOcclusion(Vector3D direction, Vector3D skyUp, float voxelWeight)
        {
            if (voxelWeight <= VoxelOcclusionEpsilon)
                return false;

            if (!skyUp.IsValid() || skyUp.LengthSquared() <= 0.0001)
                return true;

            skyUp.Normalize();
            return Vector3D.Dot(direction, skyUp) >= EnvironmentVoxelMinSkyDot;
        }

        private static Vector3D ResolveProbeOrigin(Vector3D position)
        {
            MatrixD? camera = MyAPIGateway.Session?.Camera?.WorldMatrix;
            if (!camera.HasValue)
                return position;

            Vector3D forward = camera.Value.Forward;
            Vector3D up = camera.Value.Up;
            if (!forward.IsValid() || forward.LengthSquared() <= 0.0001)
                forward = Vector3D.Zero;
            else
                forward.Normalize();

            if (!up.IsValid() || up.LengthSquared() <= 0.0001)
                up = Vector3D.Zero;
            else
                up.Normalize();

            return position + forward * 0.45 + up * 0.12;
        }

        private static Vector3D GetProbeUp(Vector3D position)
        {
            if (TryGetNaturalGravity(position, out Vector3D gravity) && gravity.LengthSquared() > 0.0001)
            {
                gravity.Normalize();
                return -gravity;
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
            blocked = false;
            if (_castRayDisabled)
                return false;

            try
            {
                object physics = MyAPIGateway.Physics;
                if (physics == null)
                    return false;

                if (!ResolveCastRay(physics.GetType()))
                    return false;

                object[] args = CreateCastRayArgs(from, to);
                object result = _castRayMethod.Invoke(physics, args);
                blocked = InterpretCastRayResult(args, result);
                if (blocked && IsFirstGridHitOpenDoor(from, to))
                    blocked = false;
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

        private static bool IsFirstGridHitOpenDoor(Vector3D from, Vector3D to)
        {
            if (!TryFindDoorProbeGrid(from, to, out MyCubeGrid grid) || grid == null)
                return false;

            try
            {
                LineD line = new LineD(from, to);
                double distance;
                MySlimBlock block;
                Vector3D? hit = grid.GetLineIntersectionExactAll(ref line, out distance, out block);
                return hit.HasValue && IsOpenDoorBlock(block);
            }
            catch
            {
                return false;
            }
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

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
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

        private struct OxygenProbe
        {
            public bool Available;
            public bool RoomPresent;
            public bool RoomAirtight;
            public bool RoomDirty;
            public int RoomProbeCount;
            public int AirtightProbeCount;
            public float LocalOxygen;
            public float RoomOxygen;
            public string Source;
            public long GridEntityId;
            public object RoomKey;
        }

        private enum CastRayMode
        {
            Unknown,
            OutHit,
            HitList,
            ReturnHit
        }
    }
}
