using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    // World-space geometry of an airtight room, derived directly from the gas system's cell set
    // (IMyOxygenRoom.Blocks) instead of ray-sampling. Stable and exact wherever a sealed room exists, so it
    // can drive reverb size/shape without the per-frame ray jitter, and anchor the acoustic-path primitives.
    internal struct V2RoomGeometry
    {
        public bool Available;
        public Vector3D Center;        // world centre of the room's cell bounds
        public float GridSize;         // metres per cell
        public int BlockCount;
        public float Volume;           // m^3
        public float EquivalentRadius; // m: radius of a sphere of equal volume
        public Vector3D HalfExtents;   // world half-dimensions of the cell bounds
        public float NearWallDistance; // shortest half-extent (m)
        public float FarWallDistance;  // longest half-extent (m)
        public bool Airtight;
    }

    // Shared, low-cost reader over Space Engineers' grid pressurisation data. The whole acoustic-structure
    // story (room connectivity, reverb geometry, per-cell sealing) reads from here so the new occlusion/env/
    // reverb systems lean on the engine's own structural facts rather than each re-deriving them. Every call
    // is defensive (the gas system can be mid-recompute / unavailable) and falls back to "no data".
    internal static class V2GridStructureProbe
    {
        private const int MaxRoomGeometryCache = 128;
        private const int MaxRoomBlocksScanned = 20000; // guard against a pathologically large single room
        private static readonly TimeSpan RoomGeometryTtl = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan RoomGeometryLifetime = TimeSpan.FromSeconds(5);
        private static readonly Dictionary<IMyOxygenRoom, CachedRoomGeometry> RoomGeometryCache =
            new Dictionary<IMyOxygenRoom, CachedRoomGeometry>();

        public static void Reset()
        {
            RoomGeometryCache.Clear();
        }

        // Per-position airtightness — the engine's own per-cell seal test, available on any grid regardless of
        // whether the whole structure is sealed to vacuum (so it survives an open front door).
        public static bool IsCellAirtight(IMyCubeGrid grid, Vector3I cell)
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

        // Resolves the airtight room containing a cell and returns its world-space geometry, cached by room
        // identity (rooms are stable objects between topology changes) with a short TTL and a block-count
        // invalidation so a remodelled room is recomputed.
        public static bool TryGetRoomGeometry(IMyCubeGrid grid, Vector3I cell, out V2RoomGeometry geometry)
        {
            geometry = default(V2RoomGeometry);
            if (grid == null)
                return false;

            IMyOxygenRoom room = ResolveRoom(grid, cell);
            if (room == null)
                return false;

            DateTime now = DateTime.UtcNow;
            int blockCount = SafeBlockCount(room);
            if (RoomGeometryCache.TryGetValue(room, out CachedRoomGeometry cached)
                && now - cached.UpdatedUtc <= RoomGeometryTtl
                && cached.Geometry.BlockCount == blockCount)
            {
                geometry = cached.Geometry;
                return geometry.Available;
            }

            if (!TryComputeRoomGeometry(grid, room, out geometry))
                return false;

            if (RoomGeometryCache.Count > MaxRoomGeometryCache)
                PurgeRoomGeometryCache(now);

            RoomGeometryCache[room] = new CachedRoomGeometry { Geometry = geometry, UpdatedUtc = now };
            return geometry.Available;
        }

        private static IMyOxygenRoom ResolveRoom(IMyCubeGrid grid, Vector3I cell)
        {
            try
            {
                IMyGridGasSystem gas = grid.GasSystem;
                if (gas == null)
                    return null;

                return gas.GetOxygenRoomForCubeGridPosition(ref cell);
            }
            catch
            {
                return null;
            }
        }

        private static int SafeBlockCount(IMyOxygenRoom room)
        {
            try
            {
                return room.BlockCount;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryComputeRoomGeometry(IMyCubeGrid grid, IMyOxygenRoom room, out V2RoomGeometry geometry)
        {
            geometry = default(V2RoomGeometry);
            try
            {
                float gridSize = grid.GridSize;
                if (gridSize <= 0.001f)
                    return false;

                Vector3I min = new Vector3I(int.MaxValue);
                Vector3I max = new Vector3I(int.MinValue);
                int count = 0;
                foreach (Vector3I blockCell in room.Blocks)
                {
                    min = Vector3I.Min(min, blockCell);
                    max = Vector3I.Max(max, blockCell);
                    count++;
                    if (count >= MaxRoomBlocksScanned)
                        break;
                }

                if (count <= 0)
                    return false;

                Vector3D minWorld = grid.GridIntegerToWorld(min);
                Vector3D maxWorld = grid.GridIntegerToWorld(max);
                Vector3D center = (minWorld + maxWorld) * 0.5;
                double half = gridSize * 0.5;
                Vector3D span = (maxWorld - minWorld) * 0.5;
                Vector3D halfExtents = new Vector3D(Math.Abs(span.X) + half, Math.Abs(span.Y) + half, Math.Abs(span.Z) + half);

                float volume = count * gridSize * gridSize * gridSize;
                float equivalentRadius = (float)Math.Pow(3.0 * volume / (4.0 * Math.PI), 1.0 / 3.0);
                float near = (float)Math.Min(halfExtents.X, Math.Min(halfExtents.Y, halfExtents.Z));
                float far = (float)Math.Max(halfExtents.X, Math.Max(halfExtents.Y, halfExtents.Z));

                bool airtight = false;
                try
                {
                    airtight = room.IsAirtight;
                }
                catch
                {
                }

                geometry = new V2RoomGeometry
                {
                    Available = true,
                    Center = center,
                    GridSize = gridSize,
                    BlockCount = count,
                    Volume = volume,
                    EquivalentRadius = equivalentRadius,
                    HalfExtents = halfExtents,
                    NearWallDistance = near,
                    FarWallDistance = far,
                    Airtight = airtight
                };
                return true;
            }
            catch
            {
                geometry = default(V2RoomGeometry);
                return false;
            }
        }

        private static void PurgeRoomGeometryCache(DateTime now)
        {
            List<IMyOxygenRoom> remove = null;
            foreach (KeyValuePair<IMyOxygenRoom, CachedRoomGeometry> pair in RoomGeometryCache)
            {
                if (now - pair.Value.UpdatedUtc <= RoomGeometryLifetime)
                    continue;

                if (remove == null)
                    remove = new List<IMyOxygenRoom>();
                remove.Add(pair.Key);
            }

            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                    RoomGeometryCache.Remove(remove[i]);
            }

            if (RoomGeometryCache.Count > MaxRoomGeometryCache)
                RoomGeometryCache.Clear();
        }

        private static readonly Vector3I[] Neighbors6 =
        {
            new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0),
            new Vector3I(0, 1, 0), new Vector3I(0, -1, 0),
            new Vector3I(0, 0, 1), new Vector3I(0, 0, -1)
        };
        private const int MaxAirPathCells = 4096;
        // Pad the source<->listener search box by a few cells so a path can bend around an interior corner.
        // Larger = the flood can follow a longer detour (multi-flight switchback staircases, paths that swing
        // wide of the direct line) at more BFS cost; too large risks escaping into exterior open space and
        // "reaching" the listener around the outside of a genuinely sealing wall. Now a tunable: this default
        // is the floor when no explicit reach is given.
        private const int AirPathBoundsPad = 3;
        private const int DefaultOpenBias = 6;

        // Length-only overload (back-compat): callers that only need the detour distance.
        public static bool TryFindAirPath(IMyCubeGrid grid, Vector3D sourceWorld, Vector3D listenerWorld, out float pathLengthMeters)
        {
            return TryFindAirPath(grid, sourceWorld, listenerWorld, AirPathBoundsPad, MaxAirPathCells, false, DefaultOpenBias, null, out pathLengthMeters, out _, out _);
        }

        // Default-reach overload (back-compat for the portal callers).
        public static bool TryFindAirPath(IMyCubeGrid grid, Vector3D sourceWorld, Vector3D listenerWorld, out float pathLengthMeters, out Vector3D portalWorld, out bool portalValid)
        {
            return TryFindAirPath(grid, sourceWorld, listenerWorld, AirPathBoundsPad, MaxAirPathCells, false, DefaultOpenBias, null, out pathLengthMeters, out portalWorld, out portalValid);
        }

        // Bounded 6-connected flood fill through traversable cells (empty air gaps + open doors) from the
        // source toward the listener, returning the shortest open-air PATH LENGTH (the detour distance) if one
        // exists within the cell budget. It never consults the airtight-room flag, so an open front door does
        // not collapse it and it works on unsealed/partially-covered structures too. Returns false when the
        // listener is unreachable through open cells within the bounds/budget (genuinely walled off here -> the
        // caller falls back to straight-line through-structure occlusion).
        //
        // Also extracts the PORTAL: the last cell on the reconstructed path that the LISTENER still has a clear
        // line of sight to (the doorway the sound diffracts through on your side). This is the psychoacoustic
        // localisation point for emitter repositioning - direction comes from here, distance from pathLength.
        public static bool TryFindAirPath(IMyCubeGrid grid, Vector3D sourceWorld, Vector3D listenerWorld, int reachPad, int maxCells, bool throughBlocks, int openBias, List<Vector3D> routeOut, out float pathLengthMeters, out Vector3D portalWorld, out bool portalValid)
        {
            pathLengthMeters = 0f;
            portalWorld = Vector3D.Zero;
            portalValid = false;
            routeOut?.Clear();
            if (grid == null)
                return false;

            reachPad = reachPad < 1 ? 1 : (reachPad > 64 ? 64 : reachPad);
            maxCells = maxCells < 256 ? 256 : (maxCells > 131072 ? 131072 : maxCells);

            try
            {
                float gridSize = grid.GridSize;
                if (gridSize <= 0.001f)
                    return false;

                Vector3I sourceCell = grid.WorldToGridInteger(sourceWorld);
                Vector3I listenerCell = grid.WorldToGridInteger(listenerWorld);
                if (sourceCell == listenerCell)
                    return true;

                Vector3I pad = new Vector3I(reachPad);
                Vector3I lo = Vector3I.Min(sourceCell, listenerCell) - pad;
                Vector3I hi = Vector3I.Max(sourceCell, listenerCell) + pad;

                // Cost-weighted search (Dijkstra via ordered cost buckets) that PREFERS OPEN AIR. An empty cell
                // or open door costs 1; a passable non-airtight block (grated stairs/catwalks/floor in
                // throughBlocks mode) costs 1+openBias. So a longer route through the open stairwell beats a
                // short hop straight up through a grated floor, and the portal climbs the stairwell instead of
                // sitting directly below you. Airtight cells (solid walls/floors, closed doors) are impassable.
                // Local buffers (not shared statics); gated to ~once / 250 ms / blocked source.
                Dictionary<Vector3I, int> dist = new Dictionary<Vector3I, int>(512);
                Dictionary<Vector3I, Vector3I> cameFrom = new Dictionary<Vector3I, Vector3I>(512);
                SortedDictionary<int, Queue<Vector3I>> frontier = new SortedDictionary<int, Queue<Vector3I>>();
                dist[sourceCell] = 0;
                PushFrontier(frontier, 0, sourceCell);

                bool reached = false;
                while (frontier.Count > 0 && !reached)
                {
                    if (dist.Count >= maxCells)
                        break;

                    int cost = PopMinFrontier(frontier, out Vector3I current);
                    if (cost > dist[current])
                        continue; // stale entry superseded by a cheaper relaxation

                    for (int i = 0; i < Neighbors6.Length; i++)
                    {
                        Vector3I next = current + Neighbors6[i];
                        if (next.X < lo.X || next.X > hi.X || next.Y < lo.Y || next.Y > hi.Y || next.Z < lo.Z || next.Z > hi.Z)
                            continue;

                        if (next == listenerCell)
                        {
                            cameFrom[listenerCell] = current;
                            reached = true;
                            break;
                        }

                        int step = StepCost(grid, next, throughBlocks, openBias);
                        if (step < 0)
                            continue; // impassable

                        int nd = cost + step;
                        if (!dist.TryGetValue(next, out int old) || nd < old)
                        {
                            dist[next] = nd;
                            cameFrom[next] = current;
                            PushFrontier(frontier, nd, next);
                        }
                    }
                }

                if (!reached)
                    return false;

                // Metric path length (for attenuation) = hop count along the chosen route * cell size (the cost
                // is openBias-weighted and not a distance, so count hops separately).
                int hops = 0;
                Vector3I lengthWalk = listenerCell;
                int lenGuard = 0;
                while (cameFrom.TryGetValue(lengthWalk, out Vector3I lprev) && lenGuard++ < maxCells)
                {
                    hops++;
                    lengthWalk = lprev;
                    if (lprev == sourceCell)
                        break;
                }
                pathLengthMeters = hops * gridSize;

                // Portal = farthest cell on the listener->source chain still in clear line of sight from the
                // listener. Walk cameFrom from the listener toward the source; stop at the first cell hidden
                // behind structure (the bend). The cell before it is the aperture the sound emerges from.
                Vector3I portalCell = listenerCell;
                Vector3I firstHidden = listenerCell;
                bool hasHidden = false;
                Vector3I walk = listenerCell;
                int guard = 0;
                while (cameFrom.TryGetValue(walk, out Vector3I prev) && guard++ < maxCells)
                {
                    Vector3D prevWorld = grid.GridIntegerToWorld(prev);
                    if (!HasLineOfSight(grid, listenerWorld, prevWorld, gridSize, throughBlocks))
                    {
                        firstHidden = prev;
                        hasHidden = true;
                        break;
                    }

                    portalCell = prev;
                    walk = prev;
                    if (prev == sourceCell)
                        break;
                }

                portalValid = portalCell != listenerCell;
                // Sub-cell resolution: rather than the quantised cell centre, place the portal at the continuous
                // grazing point where the listener's sightline toward the first hidden cell clips the structure
                // (the doorway edge). This slides smoothly as the listener/geometry move instead of snapping in
                // whole grid cells. Fall back to the cell centre when the path is fully visible.
                portalWorld = (hasHidden && portalValid)
                    ? FindLosGrazePoint(grid, listenerWorld, grid.GridIntegerToWorld(firstHidden), gridSize, throughBlocks)
                    : grid.GridIntegerToWorld(portalCell);

                // Debug route: the actual surface air path as world points, listener -> ... -> source (the line
                // the overlay draws). Capped so a huge flood does not flood the renderer.
                if (routeOut != null)
                {
                    routeOut.Add(listenerWorld);
                    Vector3I trace = listenerCell;
                    int steps = 0;
                    while (cameFrom.TryGetValue(trace, out Vector3I prev) && steps++ < 256)
                    {
                        routeOut.Add(grid.GridIntegerToWorld(prev));
                        trace = prev;
                        if (prev == sourceCell)
                            break;
                    }
                }
                return true;
            }
            catch
            {
                portalWorld = Vector3D.Zero;
                portalValid = false;
                return false;
            }
        }

        // Cheap voxel-free line-of-sight test across grid cells: marches the segment and fails on the first
        // non-traversable (solid, non-open-door) cell. Endpoints are excluded (start past the first step, stop
        // before the target) so the listener's own cell and the target cell never self-block.
        private static bool HasLineOfSight(IMyCubeGrid grid, Vector3D fromWorld, Vector3D toWorld, float gridSize, bool throughBlocks)
        {
            Vector3D delta = toWorld - fromWorld;
            double dist = delta.Length();
            if (dist < 0.001)
                return true;

            Vector3D dir = delta / dist;
            double step = Math.Max(0.25, gridSize * 0.5);
            Vector3I last = new Vector3I(int.MinValue);
            for (double t = step; t < dist - 0.001; t += step)
            {
                Vector3I cell = grid.WorldToGridInteger(fromWorld + dir * t);
                if (cell == last)
                    continue;
                last = cell;
                if (!IsCellTraversable(grid, cell, throughBlocks))
                    return false;
            }

            return true;
        }

        // Continuous (sub-cell) grazing point: march from the listener toward the first hidden cell and return
        // the last world point still in traversable space - the doorway edge where the sightline clips the
        // structure. Sliding this point (instead of snapping to a cell centre) is what makes the portal move
        // smoothly as the listener walks.
        private static Vector3D FindLosGrazePoint(IMyCubeGrid grid, Vector3D fromWorld, Vector3D towardWorld, float gridSize, bool throughBlocks)
        {
            Vector3D delta = towardWorld - fromWorld;
            double dist = delta.Length();
            if (dist < 0.001)
                return fromWorld;

            Vector3D dir = delta / dist;
            double step = Math.Max(0.15, gridSize * 0.2);
            Vector3D lastClear = fromWorld;
            Vector3I lastCell = new Vector3I(int.MinValue);
            for (double t = step; t <= dist; t += step)
            {
                Vector3D p = fromWorld + dir * t;
                Vector3I cell = grid.WorldToGridInteger(p);
                if (cell != lastCell)
                {
                    lastCell = cell;
                    if (!IsCellTraversable(grid, cell, throughBlocks))
                        break; // entered solid: lastClear is the grazing point at the aperture edge
                }
                lastClear = p;
            }

            return lastClear;
        }

        // Cost to ENTER a cell, or -1 if impassable. Empty/open-door = 1 (open air). In throughBlocks mode a
        // non-airtight occupied cell (grated stairs/catwalks/floor) is passable but costs 1+openBias, so the
        // search prefers open routes (the stairwell void) and only squeezes through grated structure when it
        // must - and far less readily than a short straight-up hop through a grated floor. Airtight cells
        // (solid walls/floors, closed doors) are impassable. Works on unsealed grids (no gas-room dependency).
        private static int StepCost(IMyCubeGrid grid, Vector3I cell, bool throughBlocks, int openBias)
        {
            IMySlimBlock slim = grid.GetCubeBlock(cell);
            if (slim == null)
                return 1; // empty cell: open air

            Sandbox.ModAPI.Ingame.IMyDoor door = slim.FatBlock as Sandbox.ModAPI.Ingame.IMyDoor;
            if (door != null)
            {
                Sandbox.ModAPI.Ingame.DoorStatus status = door.Status;
                return (status == Sandbox.ModAPI.Ingame.DoorStatus.Open
                    || status == Sandbox.ModAPI.Ingame.DoorStatus.Opening) ? 1 : -1;
            }

            if (!throughBlocks)
                return -1; // solid block stops the open-only fill

            if (IsCellAirtight(grid, cell))
                return -1; // sealed armour/wall: blocks even in through-blocks mode

            return 1 + (openBias < 0 ? 0 : openBias); // grated/non-sealing block: passable but penalised
        }

        // For LOS / grazing only: a cell the sightline can pass (cost-free passability, no openBias).
        private static bool IsCellTraversable(IMyCubeGrid grid, Vector3I cell, bool throughBlocks)
        {
            return StepCost(grid, cell, throughBlocks, 0) >= 0;
        }

        private static void PushFrontier(SortedDictionary<int, Queue<Vector3I>> frontier, int cost, Vector3I cell)
        {
            if (!frontier.TryGetValue(cost, out Queue<Vector3I> q))
            {
                q = new Queue<Vector3I>();
                frontier[cost] = q;
            }
            q.Enqueue(cell);
        }

        // Pop a cell at the lowest cost bucket. SortedDictionary keeps keys ordered, so the first entry is the
        // minimum; its enumerator is a value type, so this allocates nothing per pop.
        private static int PopMinFrontier(SortedDictionary<int, Queue<Vector3I>> frontier, out Vector3I cell)
        {
            SortedDictionary<int, Queue<Vector3I>>.Enumerator e = frontier.GetEnumerator();
            e.MoveNext();
            int key = e.Current.Key;
            Queue<Vector3I> q = e.Current.Value;
            cell = q.Dequeue();
            if (q.Count == 0)
                frontier.Remove(key);
            return key;
        }

        private struct CachedRoomGeometry
        {
            public V2RoomGeometry Geometry;
            public DateTime UpdatedUtc;
        }
    }
}
