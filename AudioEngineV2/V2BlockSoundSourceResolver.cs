using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2BlockSoundSourceResolver
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
        private static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, CachedSources> Cache = new Dictionary<string, CachedSources>(StringComparer.OrdinalIgnoreCase);

        public static void Reset()
        {
            Cache.Clear();
        }

        public static bool TryResolve(string cueName, Vector3D listenerPosition, out Vector3D sourcePosition, out string sourceLabel)
        {
            return TryResolve(cueName, listenerPosition, 0, out sourcePosition, out sourceLabel);
        }

        public static bool TryResolve(string cueName, Vector3D listenerPosition, int sourceOrdinal, out Vector3D sourcePosition, out string sourceLabel)
        {
            sourcePosition = Vector3D.Zero;
            sourceLabel = null;

            if (string.IsNullOrWhiteSpace(cueName) || listenerPosition == Vector3D.Zero)
                return false;

            V2AudioListenerState listener = AudioEngineV2Runtime.Listener;
            long gridId = listener.GridEntityId != 0L ? listener.GridEntityId : listener.ContactGridEntityId;
            string key = gridId.ToString(CultureInfo.InvariantCulture) + ":" + cueName;
            DateTime now = DateTime.UtcNow;
            if (Cache.TryGetValue(key, out CachedSources cached) && now - cached.UpdatedUtc <= CacheLifetime)
            {
                return TrySelectCachedSource(cached, listenerPosition, sourceOrdinal, out sourcePosition, out sourceLabel);
            }

            CachedSources resolved = ResolveUncached(gridId, cueName, listenerPosition);
            Cache[key] = resolved;
            return TrySelectCachedSource(resolved, listenerPosition, sourceOrdinal, out sourcePosition, out sourceLabel);
        }

        private static bool TrySelectCachedSource(CachedSources cached, Vector3D listenerPosition, int sourceOrdinal, out Vector3D sourcePosition, out string sourceLabel)
        {
            sourcePosition = Vector3D.Zero;
            sourceLabel = null;
            if (!cached.Found || cached.Sources == null || cached.Sources.Count == 0)
                return false;

            int index = Math.Max(0, Math.Min(sourceOrdinal, cached.Sources.Count - 1));
            ResolvedSource source = cached.Sources[index];
            sourceLabel = source.Label;
            if (TryGetBlockById(source.EntityId, out MyCubeBlock block))
            {
                sourcePosition = GetListenerFacingSourcePosition(block, listenerPosition);
                sourceLabel = DescribeBlock(block);
                return true;
            }

            sourcePosition = source.Position;
            return true;
        }

        private static CachedSources ResolveUncached(long gridId, string cueName, Vector3D listenerPosition)
        {
            CachedSources resolved = new CachedSources
            {
                UpdatedUtc = DateTime.UtcNow,
                Sources = new List<ResolvedSource>()
            };
            try
            {
                if (TryGetGridById(gridId, out MyCubeGrid grid))
                    CollectMatchingBlocksOnGrid(grid, cueName, listenerPosition, resolved.Sources);

                CollectMatchingBlocksOnNearbyGrids(cueName, listenerPosition, resolved.Sources);
                DeduplicateSources(resolved.Sources);
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("block-source-resolve-failed", "cue=" + cueName + " grid=" + gridId + " " + ex.Message);
                return resolved;
            }

            resolved.Sources.Sort((left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
            resolved.Found = resolved.Sources.Count > 0;
            if (resolved.Found && SettingsManager.Current.V2DebugLogEnabled)
                V2DebugLog.WriteEvent("block-source-resolved", DescribeResolved(cueName, resolved.Sources));

            return resolved;
        }

        private static void CollectMatchingBlocksOnNearbyGrids(string cueName, Vector3D listenerPosition, List<ResolvedSource> sources)
        {
            if (MyAPIGateway.Entities == null)
                return;

            double range = Math.Max(10.0, SettingsManager.Current.PlayerFilterBlockRange + 50.0);
            HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, entity =>
            {
                MyCubeGrid grid = entity as MyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.Closed)
                    return false;

                double centerDistance = Vector3D.Distance(listenerPosition, grid.PositionComp.GetPosition());
                double radius = grid.PositionComp.WorldVolume.Radius;
                return centerDistance - radius <= range;
            });

            foreach (IMyEntity entity in entities)
            {
                MyCubeGrid grid = entity as MyCubeGrid;
                if (grid == null)
                    continue;

                CollectMatchingBlocksOnGrid(grid, cueName, listenerPosition, sources);
            }
        }

        private static void CollectMatchingBlocksOnGrid(MyCubeGrid grid, string cueName, Vector3D listenerPosition, List<ResolvedSource> sources)
        {
            if (grid == null)
                return;

            foreach (MyCubeBlock block in grid.GetFatBlocks<MyCubeBlock>())
            {
                if (block == null || !CueMatchesBlock(cueName, block))
                    continue;

                Vector3D position = GetListenerFacingSourcePosition(block, listenerPosition);
                double distanceSquared = Vector3D.DistanceSquared(listenerPosition, position);
                sources.Add(new ResolvedSource
                {
                    Position = position,
                    DistanceSquared = distanceSquared,
                    Label = DescribeBlock(block),
                    EntityId = block.EntityId
                });
            }
        }

        private static Vector3D GetListenerFacingSourcePosition(MyCubeBlock block, Vector3D listenerPosition)
        {
            if (block == null)
                return Vector3D.Zero;

            BoundingBoxD box = block.PositionComp.WorldAABB;
            if (box.Extents.LengthSquared() > 0.01)
            {
                Vector3D clamped = Vector3D.Clamp(listenerPosition, box.Min, box.Max);
                Vector3D center = box.Center;
                Vector3D outward = clamped - center;
                if (outward.LengthSquared() < 0.0001)
                    outward = listenerPosition - center;

                if (outward.LengthSquared() > 0.0001)
                {
                    outward.Normalize();
                    return clamped + outward * 0.35;
                }

                return clamped;
            }

            return block.WorldMatrix.Translation;
        }

        private static bool CueMatchesBlock(string cueName, MyCubeBlock block)
        {
            string cue = cueName ?? string.Empty;
            string description = DescribeBlock(block);
            if (description.Length == 0)
                return false;

            if (Contains(cue, "WindTurbine"))
                return Contains(description, "WindTurbine") || Contains(description, "Wind Turbine") || Contains(description, "Turbine");

            if (Contains(cue, "Assembler"))
                return Contains(description, "Assembler");

            if (Contains(cue, "Rafinery") || Contains(cue, "Refinery"))
                return Contains(description, "Rafinery") || Contains(description, "Refinery");

            if (Contains(cue, "OxyGen") || Contains(cue, "Oxygen"))
                return Contains(description, "GasGenerator")
                    || Contains(description, "O2/H2")
                    || Contains(description, "Oxygen Generator");

            if (Contains(cue, "HydrogenEngine"))
                return Contains(description, "HydrogenEngine") || Contains(description, "Hydrogen Engine");

            if (Contains(cue, "Medical"))
                return Contains(description, "Medical");

            if (Contains(cue, "GravityGen") || Contains(cue, "Gravity"))
                return Contains(description, "Gravity");

            if (Contains(cue, "AirVent"))
                return Contains(description, "AirVent") || Contains(description, "Air Vent");

            if (Contains(cue, "Door"))
                return Contains(description, "Door");

            if (Contains(cue, "Reactor"))
                return Contains(description, "Reactor");

            if (Contains(cue, "Battery"))
                return Contains(description, "Battery");

            if (Contains(cue, "JumpDrive"))
                return Contains(description, "JumpDrive") || Contains(description, "Jump Drive");

            if (Contains(cue, "Beacon"))
                return Contains(description, "Beacon");

            if (Contains(cue, "Antenna"))
                return Contains(description, "Antenna");

            if (Contains(cue, "Timer"))
                return Contains(description, "Timer");

            if (Contains(cue, "Programmable"))
                return Contains(description, "Programmable");

            if (Contains(cue, "SafeZone"))
                return Contains(description, "SafeZone") || Contains(description, "Safe Zone");

            if (Contains(cue, "Rotor"))
                return Contains(description, "Rotor");

            if (Contains(cue, "Piston"))
                return Contains(description, "Piston");

            if (Contains(cue, "Conveyor"))
                return Contains(description, "Conveyor");

            if (Contains(cue, "Drill"))
                return Contains(description, "Drill");

            if (Contains(cue, "Welder"))
                return Contains(description, "Welder");

            if (Contains(cue, "Grinder"))
                return Contains(description, "Grinder");

            return false;
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

        private static void DeduplicateSources(List<ResolvedSource> sources)
        {
            if (sources == null || sources.Count <= 1)
                return;

            HashSet<long> seen = new HashSet<long>();
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                long entityId = sources[i].EntityId;
                if (entityId == 0L)
                    continue;

                if (!seen.Add(entityId))
                    sources.RemoveAt(i);
            }
        }

        private static bool TryGetBlockById(long entityId, out MyCubeBlock block)
        {
            block = null;
            if (entityId == 0L)
                return false;

            try
            {
                MyEntity entity;
                if (!MyEntities.TryGetEntityById(entityId, out entity))
                    return false;

                block = entity as MyCubeBlock;
                return block != null && !block.MarkedForClose && !block.Closed;
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeBlock(MyCubeBlock block)
        {
            if (block == null)
                return string.Empty;

            string typeName = block.GetType().Name;
            string subtype = Convert.ToString(ReadNested(block, "BlockDefinition", "Id", "SubtypeName"), CultureInfo.InvariantCulture);
            string display = Convert.ToString(ReadMember(block, "DisplayNameText"), CultureInfo.InvariantCulture);
            return (typeName + " " + subtype + " " + display).Trim();
        }

        private static object ReadNested(object instance, params string[] names)
        {
            object value = instance;
            for (int i = 0; i < names.Length; i++)
            {
                value = ReadMember(value, names[i]);
                if (value == null)
                    return null;
            }

            return value;
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return null;

            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(name, Members);
                if (property != null)
                    return property.GetValue(instance, null);

                FieldInfo field = type.GetField(name, Members);
                return field?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeResolved(string cueName, List<ResolvedSource> sources)
        {
            if (sources == null || sources.Count == 0)
                return "cue=" + cueName + " none";

            int count = Math.Min(4, sources.Count);
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("cue=").Append(cueName).Append(" count=").Append(sources.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < count; i++)
            {
                ResolvedSource source = sources[i];
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    " [{0}] {1} d={2:0.0}",
                    i,
                    source.Label ?? "?",
                    Math.Sqrt(source.DistanceSquared));
            }

            return builder.ToString();
        }

        private struct CachedSources
        {
            public DateTime UpdatedUtc;
            public bool Found;
            public List<ResolvedSource> Sources;
        }

        private struct ResolvedSource
        {
            public Vector3D Position;
            public double DistanceSquared;
            public string Label;
            public long EntityId;
        }
    }
}
