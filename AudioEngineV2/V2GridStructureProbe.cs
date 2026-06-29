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

        // Reusable flood-fill scratch buffers. The search grows these to thousands of entries (hundreds of KB of
        // backing arrays); allocating them fresh per call churned multi-MB/s of garbage while moving (every blocked
        // source recomputes on each poll), spiking gen0 GC into a repeating frame hitch. TryFindAirPath is only
        // ever called from the single-threaded audio poll and is NOT re-entrant, so sharing+clearing these is safe.
        private static readonly Dictionary<Vector3I, int> FloodDist = new Dictionary<Vector3I, int>(2048);
        private static readonly Dictionary<Vector3I, Vector3I> FloodCameFrom = new Dictionary<Vector3I, Vector3I>(2048);
        private static readonly SortedDictionary<int, Queue<Vector3I>> FloodFrontier = new SortedDictionary<int, Queue<Vector3I>>();

        // Diagnostics for the LAST TryFindAirPath flood (read by the caller's air-path-diag log). They disambiguate
        // a failed search: BudgetHit => stopped because dist.Count reached maxCells (route may exist but the flood
        // gave up - a focus/budget problem); FrontierEmpty => exhausted all reachable cells without touching the
        // listener (the listener is genuinely unreachable through open air inside the box - a sealed-path/box
        // problem). Cells = cells visited; BoxVolume = the search box cell count (if << Cells*something it's a tight
        // box). Single-threaded poll, overwritten each call; the caller reads the LAST retry attempt's values.
        internal static int LastFloodCells;
        internal static bool LastFloodBudgetHit;
        internal static bool LastFloodFrontierEmpty;
        internal static long LastFloodBoxVolume;

        // Absolute ceiling on flood cells, regardless of box size: bounds worst-case CPU per flood for a far or
        // sealed source whose box is enormous. Sits under the 131072 hard clamp on the passed-in budget. Observed
        // stairwell boxes are ~45-50k, so this comfortably covers real routes while capping pathological cases.
        private const int FloodHardCellCap = 98304;

        // Boundary-seal diagnostic (debug-only; the caller sets SealDiagEnabled from the path-debug flag). While on,
        // the flood records the distinct NULL-airtight block subtypes it bumps into at the edge of the reachable
        // region - the exact regression suspects from 0d48feb (null flag defaulted to sealing). Lets a trapped
        // source name the wrong wall. Off by default => zero overhead in normal play.
        internal static bool SealDiagEnabled;
        internal static readonly HashSet<string> LastFloodSealTypes = new HashSet<string>();

        public static void Reset()
        {
            RoomGeometryCache.Clear();
        }

        // Per-position airtightness — the engine's own per-cell seal test. NOTE: this reflects whether the cell
        // currently holds a sealed (pressurised) room, so it is FALSE in an unsealed/depressurised base even on a
        // solid wall. For "is this block a sealing surface" independent of room state, use IsSealingBlockAtCell.
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

        // Whether the block occupying a cell is a sealing (airtight-by-definition) surface - full armour, glass/
        // window, solid plate. Independent of whether the room is pressurised, so the thin-seal barrier loss
        // works in an unsealed base too. Grated/open blocks (stairs, catwalks, railings) are not sealing.
        public static bool IsSealingBlockAtCell(IMyCubeGrid grid, Vector3I cell)
        {
            if (grid == null)
                return false;
            try
            {
                IMySlimBlock slim = grid.GetCubeBlock(cell);
                return slim != null && IsBlockSealing(slim);
            }
            catch
            {
                return false;
            }
        }

        // Lightweight name check: is the block at this cell a window or panel? Used by the env sky-probe to boost the
        // muffle of thin sealed glazing/panels (a closed pane seals far more than its geometric thinness implies).
        public static bool IsWindowOrPanelAtCell(IMyCubeGrid grid, Vector3I cell)
        {
            return IsWindowOrPanelSubtype(GetSubtypeAtCell(grid, cell));
        }

        // The block subtype name at a cell (or null). Exposed so callers can diagnose what a ray actually hit.
        public static string GetSubtypeAtCell(IMyCubeGrid grid, Vector3I cell)
        {
            if (grid == null)
                return null;
            try
            {
                IMySlimBlock slim = grid.GetCubeBlock(cell);
                Sandbox.Definitions.MyCubeBlockDefinition def = slim?.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
                return def?.Id.SubtypeName;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsWindowOrPanelSubtype(string subtype)
        {
            if (string.IsNullOrEmpty(subtype))
                return false;
            return subtype.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0
                || subtype.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0;
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
            return TryFindAirPath(grid, sourceWorld, listenerWorld, AirPathBoundsPad, MaxAirPathCells, false, DefaultOpenBias, null, null, out pathLengthMeters, out _, out _, out _, out _);
        }

        // Default-reach overload (back-compat for the portal callers).
        public static bool TryFindAirPath(IMyCubeGrid grid, Vector3D sourceWorld, Vector3D listenerWorld, out float pathLengthMeters, out Vector3D portalWorld, out bool portalValid)
        {
            return TryFindAirPath(grid, sourceWorld, listenerWorld, AirPathBoundsPad, MaxAirPathCells, false, DefaultOpenBias, null, null, out pathLengthMeters, out portalWorld, out portalValid, out _, out _);
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
        public static bool TryFindAirPath(IMyCubeGrid grid, Vector3D sourceWorld, Vector3D listenerWorld, int reachPad, int maxCells, bool throughBlocks, int openBias, Vector3D? boundsAnchorWorld, List<Vector3D> routeOut, out float pathLengthMeters, out Vector3D portalWorld, out bool portalValid, out Vector3D firstHiddenWorld, out bool hasHiddenOut)
        {
            pathLengthMeters = 0f;
            portalWorld = Vector3D.Zero;
            portalValid = false;
            firstHiddenWorld = listenerWorld; // bend cell (route topology) for portal-slide; defaults to listener
            hasHiddenOut = false;
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

                // ROOT FIX for "route collapses the instant you step off the stairs onto the upper floor": the
                // search box is anchored to source<->listener, so when you cross the floor the open stairwell mouth
                // (the only aperture the route can climb) can fall OUTSIDE this box - the flood from the lower-floor
                // source then can't even reach the stairs, the recompute returns false, and the previously-good
                // route vanishes. Fold the LAST-KNOWN aperture (the caller's held portal bend cell) into the bounds
                // so the fresh search keeps including the stairwell and re-finds the same route as you walk away
                // from it. No held/stale route, no time window - the route stays freshly computed and correct.
                if (boundsAnchorWorld.HasValue)
                {
                    Vector3I anchorCell = grid.WorldToGridInteger(boundsAnchorWorld.Value);
                    lo = Vector3I.Min(lo, anchorCell - pad);
                    hi = Vector3I.Max(hi, anchorCell + pad);
                }

                // ROOT FIX for "the route never reaches the top floor" (proven by air-path-diag: fail=BUDGET with
                // cells=32768 < box). The cell budget must be able to COVER the search box, or the flood exhausts
                // its budget partway through and quits BEFORE reaching a listener that is genuinely in-box - a real
                // route is missed. The adaptive reach (16-cell pad) inflates the box to ~45-90k cells, well past the
                // old flat 32768 budget, so the flood could not even traverse the region it was told to search.
                // Scale the budget up to the box volume (hard-capped) so an existing in-box route is reachable. A
                // search that STILL fails after this genuinely ran the open-air frontier dry (fail=FRONTIER_EMPTY),
                // which is a true acoustic seal (closed door / airtight pocket), not the search giving up early.
                long boxVolume = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
                LastFloodBoxVolume = boxVolume;
                int boxBudget = boxVolume > FloodHardCellCap ? FloodHardCellCap : (int)boxVolume;
                if (boxBudget > maxCells)
                    maxCells = boxBudget;

                // Cost-weighted search (Dijkstra via ordered cost buckets) that PREFERS OPEN AIR. An empty cell
                // or open door costs 1; a passable non-airtight block (grated stairs/catwalks/floor in
                // throughBlocks mode) costs 1+openBias. So a longer route through the open stairwell beats a
                // short hop straight up through a grated floor, and the portal climbs the stairwell instead of
                // sitting directly below you. Airtight cells (solid walls/floors, closed doors) are impassable.
                // Shared scratch buffers, cleared per call (see field comment): single-threaded poll, non-reentrant.
                Dictionary<Vector3I, int> dist = FloodDist;
                Dictionary<Vector3I, Vector3I> cameFrom = FloodCameFrom;
                SortedDictionary<int, Queue<Vector3I>> frontier = FloodFrontier;
                dist.Clear();
                cameFrom.Clear();
                frontier.Clear();
                // GOAL-DIRECTED (A*) ordering, not isotropic Dijkstra. The frontier is keyed by f = g + h, where
                // g is the accumulated openBias-weighted cost (still stored in dist[] and used for the path) and
                // h is the Manhattan cell-distance to the listener. Every hop costs >= 1, so h never overestimates
                // the remaining cost -> the heuristic is admissible AND consistent, so the route found is the SAME
                // optimal "prefer open air" path uniform-cost Dijkstra would find - the search just advances toward
                // the listener instead of filling the whole lower floor in all directions. ROOT FIX for "the route
                // doesn't reach the top floor": plain Dijkstra exhausted the maxCells budget on a far/high source
                // (it spent the budget exploring away from the goal before the frontier climbed the stairwell);
                // goal-direction reaches the listener within a fraction of the cells, so far sources resolve and
                // per-flood work drops too. dist[]/cameFrom[] still hold g, so path length and portal extraction
                // are unchanged.
                dist[sourceCell] = 0;
                PushFrontier(frontier, Heuristic(sourceCell, listenerCell), sourceCell);

                LastFloodBudgetHit = false;
                LastFloodFrontierEmpty = false;
                if (SealDiagEnabled)
                    LastFloodSealTypes.Clear();

                bool reached = false;
                while (frontier.Count > 0 && !reached)
                {
                    if (dist.Count >= maxCells)
                    {
                        LastFloodBudgetHit = true;
                        break;
                    }

                    int f = PopMinFrontier(frontier, out Vector3I current);
                    int g = dist[current];
                    if (f - Heuristic(current, listenerCell) > g)
                        continue; // stale entry (its g was superseded by a cheaper relaxation)

                    // The block (if any) occupying the cell we are leaving. A face between two cells is sealed if
                    // EITHER side seals it, so we must also check that sound can EXIT `current` toward a neighbour -
                    // not just that it can ENTER the neighbour. Skipped for the source cell, which radiates. Without
                    // this, the flood enters a slope/panel through its open face and walks out the SOLID side, i.e.
                    // straight through the floor ("route goes through the floor instead of the stairs").
                    IMySlimBlock currentSlim = (throughBlocks && current != sourceCell) ? grid.GetCubeBlock(current) : null;

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

                        // EXIT face: if the cell we are leaving is itself an occupied block, sound can only pass to
                        // `next` if that block is NOT airtight on its face toward next (next - current).
                        if (currentSlim != null && IsFaceAirtight(currentSlim, current, next - current))
                            continue;

                        // Directional sealing: the block at `next` blocks entry only if airtight on the face toward
                        // the open cell we are coming from (current - next).
                        int step = StepCostDirectional(grid, next, current - next, throughBlocks, openBias);
                        if (step < 0)
                        {
                            // Boundary diagnostic: this neighbour walls off the reachable region. Record the
                            // regression suspects - null-airtight blocks that 0d48feb flipped passable->sealing -
                            // so a trapped source (FRONTIER_EMPTY) reveals exactly which block is the wrong wall.
                            if (SealDiagEnabled && LastFloodSealTypes.Count < 24)
                                RecordSealType(grid, next);
                            continue; // impassable
                        }

                        int nd = g + step;
                        if (!dist.TryGetValue(next, out int old) || nd < old)
                        {
                            dist[next] = nd;
                            cameFrom[next] = current;
                            PushFrontier(frontier, nd + Heuristic(next, listenerCell), next);
                        }
                    }
                }

                LastFloodCells = dist.Count;
                LastFloodFrontierEmpty = !reached && !LastFloodBudgetHit; // ran the frontier dry without arriving
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

                // Portal = the cell FARTHEST back along the listener->source chain (toward the origin block) that
                // the listener can still directly see. Walk cameFrom from the listener toward the source and push
                // the portal as deep as line of sight reaches; stop at the first cell hidden behind structure (the
                // bend). Line of sight is the SOLE constraint - we deliberately do NOT clamp to the listener's floor
                // level: if you can see straight down an open stairwell shaft, the sound localises to the deepest
                // point you can see down it (toward the source), not the mouth at your feet. That can't read as
                // "through the floor" because a SOLID floor would block the sightline and stop the walk anyway.
                // The LOS test is PERMISSIVE (throughBlocks: true): only solid/SEALING blocks break the sightline;
                // see-through grated stairs/catwalks/railings (the IsKnownOpenBlockType blacklist) do NOT. With the
                // old strict test, a single grated catwalk/stair cell in the line collapsed the portal back onto
                // the listener ("falls back to right next to the player") even though you can plainly see past it.
                Vector3I portalCell = listenerCell;
                Vector3I firstHidden = listenerCell;
                bool hasHidden = false;
                Vector3I walk = listenerCell;
                int guard = 0;
                while (cameFrom.TryGetValue(walk, out Vector3I prev) && guard++ < maxCells)
                {
                    Vector3D prevWorld = grid.GridIntegerToWorld(prev);

                    // PERMISSIVE line of sight (throughBlocks: true): only solid/SEALING blocks block the
                    // sightline; see-through grated stairs/catwalks/railings do not (you can plainly see past
                    // them). If the listener really can see straight down an open grated shaft to the source,
                    // the portal correctly lands at/near the source and no reposition is needed - that is the
                    // intended behaviour, not a bug. A solid floor between the listener and a deep cell still
                    // breaks LOS, so the portal stops at the open stairwell aperture (the visible mouth).
                    if (!HasLineOfSight(grid, listenerWorld, prevWorld, gridSize, true))
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

                // Valid whenever the route enters structure the listener can't see past (hasHidden), EVEN if
                // that happens on the very first step (portalCell still == listener, e.g. standing right at the
                // grated stairwell mouth) - otherwise the portal would collapse onto the listener and the source
                // would not reposition at all. The grazing point toward the first hidden cell is the localisation
                // point (the open stairwell mouth); only fall back to a cell centre when the route is fully
                // visible to the source (no structure between - then no reposition is needed anyway).
                portalValid = hasHidden || portalCell != listenerCell;
                Vector3D firstHiddenWorldLocal = grid.GridIntegerToWorld(firstHidden);
                // Portal = the CENTRE of the deepest path cell still in direct line of sight ("sit at the furthest
                // path block centre I can see"). Cell centre, NOT the sub-cell graze edge, so it does not slide or
                // jitter as the flood picks slightly different top-room routes; the caller holds it with hysteresis
                // and only ever advances it deeper toward the source while you keep sight of it.
                portalWorld = grid.GridIntegerToWorld(portalCell);
                firstHiddenWorld = hasHidden ? firstHiddenWorldLocal : portalWorld;
                hasHiddenOut = hasHidden;

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

        // Strict straight-line clearance: every cell STRICTLY between source and listener (both endpoint cells
        // skipped, so the source's own block and the listener's cell never self-block) is empty or an open door.
        // Used to detect a genuinely unobstructed source even when the physics occlusion ray false-positives on
        // the emitter's own block - so an in-room jukebox is not pushed onto a winding air path and muffled.
        public static bool IsStraightPathOpen(IMyCubeGrid grid, Vector3D fromWorld, Vector3D toWorld)
        {
            if (grid == null)
                return false;
            try
            {
                float gridSize = grid.GridSize;
                if (gridSize <= 0.001f)
                    return false;

                Vector3I fromCell = grid.WorldToGridInteger(fromWorld);
                Vector3I toCell = grid.WorldToGridInteger(toWorld);
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
                    if (cell == last || cell == fromCell || cell == toCell)
                        continue;
                    last = cell;
                    if (StepCost(grid, cell, false, 0) < 0)
                        return false; // a solid block strictly between -> genuinely obstructed
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Sight-opacity test for the PORTAL WALK. Only a HARD, fully-solid block - a full airtight cube (armour
        // walls, large functional boxes) or a CLOSED door - blocks the line of sight. Everything partial is
        // see-through: armour slopes/corners/panels, catwalks, railings, gratings, fences, furniture. This is on
        // purpose MORE permissive than the flood's per-face sealing - the flood decides where sound can TRAVEL,
        // sight decides how far down that travelled path you can still SEE, and you can see PAST anything that is
        // not a solid wall. The old test reused the flood's omnidirectional seal (IsBlockSealing), which counts
        // armour slopes/panels as solid, so the sightline snagged on the stairwell's own slope/panel landing a
        // step from the listener and the portal collapsed back onto the player's head.
        private static bool IsSightBlocking(IMyCubeGrid grid, Vector3I cell)
        {
            IMySlimBlock slim = grid.GetCubeBlock(cell);
            if (slim == null)
                return false; // empty air - see-through

            Sandbox.ModAPI.Ingame.IMyDoor door = slim.FatBlock as Sandbox.ModAPI.Ingame.IMyDoor;
            if (door != null)
            {
                Sandbox.ModAPI.Ingame.DoorStatus status = door.Status;
                return !(status == Sandbox.ModAPI.Ingame.DoorStatus.Open || status == Sandbox.ModAPI.Ingame.DoorStatus.Opening);
            }

            Sandbox.Definitions.MyCubeBlockDefinition def = slim.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
            if (def == null)
                return true; // unknown definition -> opaque (conservative)

            bool? airtight = def.IsAirTight;
            if (airtight.HasValue)
                return airtight.Value; // full airtight cube = opaque wall; explicit-open (grates/catwalks) = see-through

            // null airtight = a PARTIAL shape (slope/corner/panel/furniture/window). Full solid cubes are flagged
            // airtight=true and handled above, so a null block is something you can plainly see past -> see-through.
            return false;
        }

        // Cheap voxel-free line-of-sight test across grid cells: marches the segment and fails on the first
        // HARD (sight-blocking) cell. Endpoints are excluded (start past the first step, stop before the target)
        // so the listener's own cell and the target cell never self-block. throughBlocks is accepted for signature
        // compatibility but sight always uses the hard-block test (IsSightBlocking), independent of the flood rule.
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
                if (IsSightBlocking(grid, cell))
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
                    if (IsSightBlocking(grid, cell))
                        break; // entered a hard block: lastClear is the grazing point at the aperture edge
                }
                lastClear = p;
            }

            return lastClear;
        }

        // Public portal-slide: for a HELD route, re-derive the open aperture edge from the LIVE listener toward the
        // bend cell (firstHidden) the route last resolved. Lets the portal track the listener smoothly between the
        // (rare, hysteresis-gated) route-topology recomputes instead of freezing until the next one - which is what
        // made the repositioned emitter step/rubberband as you walked.
        // Public direct-sight test for the caller's portal hysteresis: does the listener have a clear line of sight
        // (HARD blocks only - slopes/panels/grates/furniture are see-through) to a held world point? Used to decide
        // whether a held portal is still visible (keep it) or has gone behind structure (let it recompute).
        public static bool HasDirectSight(IMyCubeGrid grid, Vector3D fromWorld, Vector3D toWorld)
        {
            if (grid == null)
                return false;
            try
            {
                float gridSize = grid.GridSize;
                if (gridSize <= 0.001f)
                    return false;
                return HasLineOfSight(grid, fromWorld, toWorld, gridSize, true);
            }
            catch
            {
                return false;
            }
        }

        public static Vector3D GrazePortal(IMyCubeGrid grid, Vector3D listenerWorld, Vector3D firstHiddenWorld)
        {
            if (grid == null)
                return firstHiddenWorld;
            try
            {
                float gridSize = grid.GridSize;
                if (gridSize <= 0.001f)
                    return firstHiddenWorld;
                // PERMISSIVE LOS (throughBlocks: true) to match the computed-portal walk above: the held-route
                // slide must use the same grating-transparent rule, else the live portal would jump differently
                // from the recomputed one as the listener moves.
                return FindLosGrazePoint(grid, listenerWorld, firstHiddenWorld, gridSize, true);
            }
            catch
            {
                return firstHiddenWorld;
            }
        }

        // Cost to ENTER a cell, or -1 if impassable. Empty/open-door = 1 (open air). In throughBlocks mode a
        // non-SEALING occupied block (grated stairs/catwalks/railings) is passable but costs 1+openBias, so the
        // search prefers open routes; a SEALING block (full armour wall/floor, closed door) is impassable. The
        // sealing test is the BLOCK DEFINITION's airtightness (IsBlockSealing) - NOT IsRoomAtPositionAirtight,
        // which returns false for any occupied cell (solid or grated alike) and let the route punch through a
        // solid floor.
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

            if (IsBlockSealing(slim))
                return -1; // full-armour wall/floor: seals air -> blocks the path

            return 1 + (openBias < 0 ? 0 : openBias); // grated/non-sealing block: passable but penalised
        }

        // DIRECTIONAL step cost for the flood: identical to StepCost except sealing is decided PER FACE - the block
        // at `cell` blocks entry from the open neighbour only if it is airtight on the face pointing back toward that
        // neighbour (towardOpenNormal = openNeighbourCell - cell). This is what fixes the "room sealed by its own
        // wall-mounted furniture / stairwell sealed by a slope" cases: a food processor or armour slope is airtight
        // on its structural/mount face but OPEN on the side facing into the room, so sound passes that side while a
        // full armour cube (airtight every face) and a closed door still seal.
        private static int StepCostDirectional(IMyCubeGrid grid, Vector3I cell, Vector3I towardOpenNormal, bool throughBlocks, int openBias)
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
                return -1;

            if (IsFaceAirtight(slim, cell, towardOpenNormal))
                return -1; // this block's face toward the open cell is airtight -> blocks the path

            return 1 + (openBias < 0 ? 0 : openBias);
        }

        // Whether the block's face whose grid-space outward normal is `gridNormal` is airtight, per the GAME'S own
        // per-face pressurization table (the data the oxygen system uses). Explicit IsAirTight (true/false) short-
        // circuits. For the null case we transform the grid normal into the block's local frame (via its
        // orientation) and look up IsCubePressurized. 1x1 blocks (every fragmenting culprit here) key the table at
        // local (0,0,0); multi-cell blocks fall back to "any airtight face" to avoid a risky per-cell offset
        // transform. No data for the face -> open (a non-structural block, sound passes).
        private static bool IsFaceAirtight(IMySlimBlock slim, Vector3I cell, Vector3I gridNormal)
        {
            try
            {
                Sandbox.Definitions.MyCubeBlockDefinition def = slim.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
                if (def == null)
                    return true; // unknown -> seal (never leak through something solid)

                bool? airtight = def.IsAirTight;
                if (airtight.HasValue)
                    return airtight.Value;

                // Solid functional machinery (air vents, gas generators, etc.) is solid to SOUND regardless of the
                // game's room-pressurization table - which only marks such a block's mount face airtight and leaves
                // the rest "open", so the flood used to treat an air vent as a 1-cell air gap and route sound
                // straight through it. Seal all such machinery here (doors are excluded - their open/closed state is
                // handled by the caller). This fixes the vent leak broadly, without naming individual subtypes.
                if (IsSolidFunctionalBlock(slim))
                    return true;

                bool knownOpen = IsKnownOpenBlockType(def);
                var table = def.IsCubePressurized;

                if (def.Size != Vector3I.One)
                {
                    // Multi-cell open block (e.g. a 2-high stairwell): use the game's PER-CELL, per-face
                    // pressurization data so the block passes only through its genuine openings - not
                    // omnidirectionally, and crucially not from beneath. Resolving which local cell `cell` maps to
                    // lets the bottom cell's downward face stay sealed while the internal face between the two
                    // cells stays open for the climb. Falls back to the previous behaviour (whitelist -> open, else
                    // any-airtight-face) if the cell mapping can't be resolved, so it can never regress.
                    if (table != null
                        && TryResolveLocalCellOffset(slim, def, cell, out Vector3I localOffset)
                        && table.TryGetValue(localOffset, out var perCellFaces)
                        && perCellFaces != null)
                    {
                        Vector3I localNormalMulti = ToLocalNormal(slim, gridNormal);
                        Sandbox.Definitions.MyCubeBlockDefinition.MyCubePressurizationMark markMulti;
                        if (perCellFaces.TryGetValue(localNormalMulti, out markMulti))
                            return markMulti != Sandbox.Definitions.MyCubeBlockDefinition.MyCubePressurizationMark.NotPressurized;
                        return false; // no entry for this face -> open
                    }

                    return knownOpen ? false : HasAnyAirtightFace(def);
                }

                // 1x1 block.
                if (knownOpen)
                    return false; // small open structural families (catwalk/railing/passage) pass on all faces - unchanged
                if (table == null)
                    return false; // no pressurization data -> not a sealing structural block

                // Grid-space face normal (a unit axis: current - next) -> the block's LOCAL face normal, using the
                // engine's own orientation mapping. Convention-proof: TransformDirectionInverse(gridDir) gives the
                // local direction directly, so there is no hand-rolled matrix transpose to get the sign/axis wrong
                // (which previously read solid floor faces as open and let the route punch through the floor).
                Vector3I localNormal = ToLocalNormal(slim, gridNormal);

                Dictionary<Vector3I, Sandbox.Definitions.MyCubeBlockDefinition.MyCubePressurizationMark> faces;
                if (!table.TryGetValue(Vector3I.Zero, out faces) || faces == null)
                    return false;
                Sandbox.Definitions.MyCubeBlockDefinition.MyCubePressurizationMark mark;
                if (faces.TryGetValue(localNormal, out mark))
                    return mark != Sandbox.Definitions.MyCubeBlockDefinition.MyCubePressurizationMark.NotPressurized;
                return false;
            }
            catch
            {
                return true; // can't read it -> seal conservatively
            }
        }

        // A grid-space face normal -> the block's LOCAL face normal via the engine's own orientation mapping.
        private static Vector3I ToLocalNormal(IMySlimBlock slim, Vector3I gridNormal)
        {
            Base6Directions.Direction gridDir = Base6Directions.GetDirection(gridNormal);
            Base6Directions.Direction localDir = slim.Orientation.TransformDirectionInverse(gridDir);
            return Base6Directions.GetIntVector(localDir);
        }

        // True if the block carries a functional machinery component (air vent, gas generator, assembler, ...).
        // Such blocks are solid to sound even though the oxygen pressurization table marks most of their faces
        // "open" (it only seals the mount face). Doors are NOT functional-sealed here: their open/closed state is
        // resolved by the caller, so a closed door seals and an open one passes.
        private static bool IsSolidFunctionalBlock(IMySlimBlock slim)
        {
            var fat = slim.FatBlock;
            if (fat == null)
                return false; // no machinery -> a slim structural/decorative block; let the table decide
            if (fat is Sandbox.ModAPI.Ingame.IMyDoor)
                return false;
            return fat is Sandbox.ModAPI.Ingame.IMyFunctionalBlock;
        }

        // Map a grid cell occupied by a multi-cell block to that block's LOCAL cell offset (the outer key of the
        // pressurization table). Rotates each local cell by the block's orientation and anchors the rotated set to
        // the block's grid AABB min, so it is convention-proof (no hand-rolled transpose). Returns false if no
        // local cell lands on `cell`, in which case the caller falls back to the omnidirectional behaviour.
        private static bool TryResolveLocalCellOffset(IMySlimBlock slim, Sandbox.Definitions.MyCubeBlockDefinition def, Vector3I cell, out Vector3I localOffset)
        {
            localOffset = Vector3I.Zero;
            try
            {
                Matrix rotation;
                slim.Orientation.GetMatrix(out rotation); // local -> grid (pure rotation)
                Vector3I size = def.Size;

                Vector3I rotatedMin = new Vector3I(int.MaxValue, int.MaxValue, int.MaxValue);
                for (int x = 0; x < size.X; x++)
                    for (int y = 0; y < size.Y; y++)
                        for (int z = 0; z < size.Z; z++)
                            rotatedMin = Vector3I.Min(rotatedMin, RotateOffset(x, y, z, rotation));

                for (int x = 0; x < size.X; x++)
                    for (int y = 0; y < size.Y; y++)
                        for (int z = 0; z < size.Z; z++)
                        {
                            Vector3I gridCell = slim.Min + (RotateOffset(x, y, z, rotation) - rotatedMin);
                            if (gridCell == cell)
                            {
                                localOffset = new Vector3I(x, y, z);
                                return true;
                            }
                        }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static Vector3I RotateOffset(int x, int y, int z, Matrix rotation)
        {
            Vector3 r = Vector3.TransformNormal(new Vector3(x, y, z), rotation);
            return new Vector3I((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Z));
        }

        // Whether an occupied cell's block fully seals air (full armour cube, solid wall/floor). Uses the block
        // DEFINITION's airtightness: IsAirTight == true means a fully sealing cube; false/null (grated stairs,
        // catwalks, railings, slopes) lets air - and sound - through. Unknown definition -> treat as sealing so a
        // stray route never leaks through something solid.
        // Record a boundary cell that blocked the flood, but ONLY the regression suspects: an occupied non-door
        // block whose definition airtight flag is NULL (the case 0d48feb defaulted to sealing). Explicit at=true
        // (real armour seal) and at=false (already passable) are not interesting. The collected subtypes are the
        // candidate walls to add to the open-block whitelist / refine IsBlockSealing.
        private static void RecordSealType(IMyCubeGrid grid, Vector3I cell)
        {
            try
            {
                IMySlimBlock slim = grid.GetCubeBlock(cell);
                if (slim == null)
                    return;
                if (slim.FatBlock is Sandbox.ModAPI.Ingame.IMyDoor)
                    return; // a closed door sealing is correct, not a regression
                Sandbox.Definitions.MyCubeBlockDefinition def = slim.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
                if (def == null || def.IsAirTight.HasValue)
                    return; // only the null-airtight default-to-sealing suspects
                string sub = def.Id.SubtypeName;
                if (string.IsNullOrEmpty(sub))
                    sub = def.Id.TypeId.ToString();
                LastFloodSealTypes.Add(sub);
            }
            catch
            {
            }
        }

        // Diagnostic: list the OCCUPIED cells within +/- radius of a world point with each block's subtype, its
        // definition airtight flag (true/false/null), and the flood's sealing verdict. Empty (open-air) cells are
        // skipped. Used to find a stairwell/opening block the path model wrongly treats as sealing - the "two
        // disconnected open-air regions / route won't climb the stairs" case. Capped so it never floods the log.
        public static string DescribeCellsAround(IMyCubeGrid grid, Vector3D world, int radius)
        {
            if (grid == null)
                return "nogrid";
            try
            {
                Vector3I c = grid.WorldToGridInteger(world);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                int shown = 0;
                for (int dx = -radius; dx <= radius && shown < 14; dx++)
                    for (int dy = -radius; dy <= radius && shown < 14; dy++)
                        for (int dz = -radius; dz <= radius && shown < 14; dz++)
                        {
                            Vector3I cell = new Vector3I(c.X + dx, c.Y + dy, c.Z + dz);
                            IMySlimBlock slim = grid.GetCubeBlock(cell);
                            if (slim == null)
                                continue; // empty cell = open air, not interesting here
                            Sandbox.Definitions.MyCubeBlockDefinition def = slim.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
                            string sub = def != null ? def.Id.SubtypeName : "?";
                            if (string.IsNullOrEmpty(sub))
                                sub = def != null ? def.Id.TypeId.ToString() : "?";
                            string at = (def != null && def.IsAirTight.HasValue) ? (def.IsAirTight.Value ? "T" : "F") : "null";
                            bool sealing = IsBlockSealing(slim);
                            sb.Append('(').Append(dx).Append(',').Append(dy).Append(',').Append(dz).Append(')')
                              .Append(sub).Append("[at=").Append(at).Append(",seal=").Append(sealing ? "Y" : "N").Append("] ");
                            shown++;
                        }
                return shown == 0 ? "all-open-air" : sb.ToString();
            }
            catch (Exception ex)
            {
                return "err:" + ex.GetType().Name;
            }
        }

        private static bool IsBlockSealing(IMySlimBlock slim)
        {
            try
            {
                Sandbox.Definitions.MyCubeBlockDefinition def = slim.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
                if (def == null)
                    return true; // unknown definition -> treat as solid so a route never leaks through it

                bool? airtight = def.IsAirTight;
                if (airtight.HasValue)
                    return airtight.Value; // explicit: true = full seal; false = open (grates/catwalks/stairs set this)

                // Ambiguous (null): omnidirectional fallback used by the LOS / straight-line tests, which have no
                // direction of approach. The FLOOD uses the directional IsFaceAirtight instead (per-face, correct
                // for wall-mounted furniture and slopes). Here we keep the conservative "null -> sealing unless a
                // recognised walk-through family" so sightlines never leak through something solid.
                return !IsKnownOpenBlockType(def);
            }
            catch
            {
                return true;
            }
        }

        // True if the block definition marks ANY face airtight in the game's own pressurization table (the data
        // the oxygen system uses). Orientation-free on purpose: we only need "is this a structural/sealing block
        // at all" for cell-level flood connectivity, not which specific face. Full armour cubes, slopes, corners,
        // ramps and windows have airtight faces -> seal; furniture/decor/most functional blocks have none -> pass.
        private static bool HasAnyAirtightFace(Sandbox.Definitions.MyCubeBlockDefinition def)
        {
            try
            {
                var table = def.IsCubePressurized;
                if (table == null)
                    return false; // no pressurization data -> not a sealing structural block
                foreach (var perCell in table.Values)
                {
                    foreach (var mark in perCell.Values)
                    {
                        if (mark != Sandbox.Definitions.MyCubeBlockDefinition.MyCubePressurizationMark.NotPressurized)
                            return true;
                    }
                }
                return false;
            }
            catch
            {
                return true; // can't read the table -> be conservative and seal (never leak through something solid)
            }
        }

        // Recognised open / walk-through block families whose definition airtightness is left null but which sound
        // (and a person) clearly pass through: stairs, ladders, catwalks, railings, passages, gratings, fences,
        // scaffolds. Matched on the subtype id so it covers vanilla and most modded variants. NOTE: armour ramps
        // are deliberately NOT here - they seal their full faces like any armour block.
        private static bool IsKnownOpenBlockType(Sandbox.Definitions.MyCubeBlockDefinition def)
        {
            string subtype = def.Id.SubtypeName;
            if (string.IsNullOrEmpty(subtype))
                return false;
            return ContainsAny(subtype,
                "Stair", "Ladder", "Catwalk", "Railing", "Passage", "Grat", "Fence", "Scaffold");
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (value.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // Admissible + consistent A* heuristic: Manhattan cell-distance to the goal. Each grid hop costs at least
        // 1 (open air), so the straight-line hop count never overestimates the true remaining openBias-weighted
        // cost - the optimal route is preserved while the search is focused toward the listener.
        private static int Heuristic(Vector3I cell, Vector3I goal)
        {
            return Math.Abs(cell.X - goal.X) + Math.Abs(cell.Y - goal.Y) + Math.Abs(cell.Z - goal.Z);
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
