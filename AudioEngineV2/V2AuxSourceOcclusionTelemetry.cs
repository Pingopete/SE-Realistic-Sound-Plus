using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2AuxSourceOcclusionTelemetry
    {
        private const int MaxSamples = 32;
        private const int MaxPathProbeCache = 256;
        private const float DefaultSourceClearRadius = 1.6f;
        private const float MaxSourceClearRadius = 5.0f;
        private const float MaxSourceSkipMeters = 6.0f;
        private const float ListenerClearRadius = 0.6f;
        private const float SourceClearMargin = 0.5f;
        private const float VoxelOcclusionEpsilon = 0.001f;
        private const float VoxelOcclusionMinUsefulWeight = 0.05f;
        private const float BlockVoxelMinBlockedMeters = 0.35f;
        private const double PathProbeSourceCellMeters = 1.5;
        private static readonly TimeSpan SampleLifetime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PathProbeInterval = TimeSpan.FromMilliseconds(250);
        // Route hysteresis: re-run the flood-fill only when the listener has moved past this, or the route is
        // this old. Stops the discrete grid route flip-flopping when sub-cell jitter flips the listener cell.
        private const double AirPathRecomputeMovedSq = 0.4 * 0.4; // ~0.4 m
        // Portal hold hysteresis: a freshly recomputed portal must be at least this much DEEPER (farther from the
        // listener) than the held one to replace it; otherwise, while the held portal is still in sight, it stays.
        private const double PortalHoldDepthEpsSq = 1.0 * 1.0; // ~1 m of "clearly deeper" before advancing
        // The remembered aperture counts as "on the listener->source sightline" (we are threading the shaft, keep
        // the route bridged) only within this distance of that line. Beyond it the source is directly visible and
        // the occlusion clears immediately - no waiting for the 3 s aperture memory to lapse.
        private const double StraightClearApertureOnLineM = 3.0;
        private static readonly TimeSpan AirPathRecomputeMaxAge = TimeSpan.FromMilliseconds(2500);
        // Minimum wall-clock between full flood-fill recomputes for one source. The flood (structure discovery) is
        // the only work that runs WHILE MOVING - every source recomputes on each 500 ms poll, and each flood
        // allocates large search buffers, so a moving player triggers a synchronised GC/CPU spike every poll (the
        // "heavy repeating frame drop on the move"). Between floods the cheap portal-slide already tracks your live
        // position, so the route topology only needs an occasional refresh: gate the flood to this cadence.
        private static readonly TimeSpan AirPathRecomputeMinInterval = TimeSpan.FromMilliseconds(750);
        // How long the last open aperture (stairwell mouth) is remembered after the route last resolved through it.
        // While fresh, the air-path/reposition feature is kept alive even if the straight physics ray momentarily
        // reads clear (threading the open shaft as you cross the mouth) and the flood is anchored to that aperture -
        // so the route survives the step from the stairwell onto the upper floor instead of collapsing there.
        private static readonly TimeSpan ApertureMemory = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PathProbeLifetime = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan OcclusionSmoothingResetGap = TimeSpan.FromSeconds(1.5);
        private const int MaxOcclusionSmoothing = 256;
        private static readonly Dictionary<string, V2AuxSourceOcclusionSample> Samples = new Dictionary<string, V2AuxSourceOcclusionSample>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PathProbeState> PathProbeCache = new Dictionary<string, PathProbeState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, OcclusionSmoothState> OcclusionSmoothing = new Dictionary<string, OcclusionSmoothState>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Order = new List<string>();
        private static DateTime _lastLogUtc = DateTime.MinValue;
        private static long _pathProbeCacheHits;
        private static long _pathProbeCacheMisses;
        private static long _rangeRejects;
        private static long _pathRays;
        private static long _voxelEstimates;
        private static long _thicknessEstimates;
        private static long _airPathFound;
        private static long _airPathMerged;
        private static long _thinSealHits;
        // Last air-path source's reposition decision, surfaced in the perf line for diagnosis.
        private static string _dbgRepoCue = "-";
        private static bool _dbgRepoPortalValid;
        private static bool _dbgRepoApplied;
        private static float _dbgRepoBlend;
        private static float _dbgRepoMoveMeters;
        private static float _dbgRepoAirLen;

        public static void Reset()
        {
            Samples.Clear();
            PathProbeCache.Clear();
            OcclusionSmoothing.Clear();
            Order.Clear();
            _lastLogUtc = DateTime.MinValue;
            _pathProbeCacheHits = 0L;
            _pathProbeCacheMisses = 0L;
            _rangeRejects = 0L;
            _pathRays = 0L;
            _voxelEstimates = 0L;
            _thicknessEstimates = 0L;
            _airPathFound = 0L;
            _airPathMerged = 0L;
            _thinSealHits = 0L;
        }

        public static void Update()
        {
            V2BlockRangeScaler.Update();
        }

        public static void RecordVoice(string kind, string cueName, IMySourceVoice voice, float score)
        {
            if (!IsCandidate(kind, cueName, voice, score))
                return;

            if (!RspDynamicAudioFilters.TryResolveEmitter(voice, out MyEntity3DSoundEmitter emitter) || emitter == null)
                return;

            Vector3D source = ResolveEmitterSourcePosition(emitter);
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            if (listener == Vector3D.Zero)
                listener = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return;

            RecordRangeRelation(cueName, source, listener);

            V2AuxSourceOcclusionSample sample = Calculate(kind, cueName, score, source, listener, "physical", ResolveEmitterEntityId(emitter));
            sample.CustomRangeApplied = TryApplyCustomRange(emitter, cueName, sample.EffectiveRange, sample.VanillaMaxDistance);
            if (sample.CustomRangeApplied)
                sample.EstimatedGain = CalculateEstimatedGain(sample, true);

            StoreSample(sample, source);

            // Move the live emitter to the weight-blended target (or ease it back to its block when not applied).
            // The matching attenuation already lives in sample.EstimatedGain (applied via VolumeMultiplier).
            V2BlockEmitterReposition.Request(emitter, source, sample.RepositionTarget, sample.RepositionApplied, DateTime.UtcNow, cueName);
        }

        public static string FormatSummary()
        {
            PurgeStale();
            int count = 0;
            float strongest = 0f;
            string strongestCue = "?";
            float strongestMuffle = 0f;
            float strongestCutoff = 0f;

            foreach (V2AuxSourceOcclusionSample sample in Samples.Values)
            {
                count++;
                if (sample.Score >= strongest)
                {
                    strongest = sample.Score;
                    strongestCue = sample.CueName;
                    strongestMuffle = sample.FinalMuffling;
                    strongestCutoff = sample.EstimatedCutoff;
                }
            }

            if (count == 0)
                return "No physical non-engine source candidates observed yet.";

            return string.Format(
                CultureInfo.InvariantCulture,
                "candidates={0} strongest={1} muffle={2:0.00} cutoff={3:0}Hz score={4:0.00}",
                count,
                Trim(strongestCue, 28),
                strongestMuffle,
                strongestCutoff,
                strongest);
        }

        public static string FormatPerfSummary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "aux rayState={4}/{5} rangeFar={0} rays={1} voxel={2} thick={3} airPath={6}/{7} thinSeal={8} repo[{9}:pv={10} R={11} b={12:0.00} mv={13:0.0}m air={14:0.0}m]",
                _rangeRejects,
                _pathRays,
                _voxelEstimates,
                _thicknessEstimates,
                _pathProbeCacheHits,
                _pathProbeCacheMisses,
                _airPathFound,
                _airPathMerged,
                _thinSealHits,
                Trim(_dbgRepoCue, 20),
                _dbgRepoPortalValid ? "Y" : "N",
                _dbgRepoApplied ? "Y" : "N",
                _dbgRepoBlend,
                _dbgRepoMoveMeters,
                _dbgRepoAirLen);
        }

        public static string FormatSources(int maxLines)
        {
            PurgeStale();
            if (Samples.Count == 0)
                return "cue/class  dist  range  open near thick  room seal muff  cutoff  gain  air";

            List<V2AuxSourceOcclusionSample> sorted = new List<V2AuxSourceOcclusionSample>(Samples.Values);
            sorted.Sort((left, right) => right.Score.CompareTo(left.Score));

            StringBuilder builder = new StringBuilder();
            builder.Append("cue/class  dist  range  open near thick  room seal muff  cutoff  gain  air");
            int count = Math.Min(maxLines, sorted.Count);
            for (int i = 0; i < count; i++)
            {
                V2AuxSourceOcclusionSample sample = sorted[i];
                builder.AppendLine();
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0,-18} {1,4:0}m {2,3:0}/{3,3:0}{8}m {4,4:0.00} {12,4:0.00} {9,4:0.0}m {11,-4} {10} {5,4:0.00} {6,5:0}Hz {7,4:0.00} {13}",
                    Trim(sample.CueName, 18),
                    sample.Distance,
                    sample.VanillaMaxDistance,
                    sample.EffectiveRange,
                    sample.OpenFraction,
                    sample.FinalMuffling,
                    sample.EstimatedCutoff,
                    sample.EstimatedGain,
                    sample.CustomRangeApplied ? "*" : " ",
                    sample.EstimatedBlockedLength,
                    sample.SealedExtraApplied ? "S" : "-",
                    FormatRoomComparison(sample),
                    sample.NearFieldScale,
                    sample.AirPathAvailable
                        ? string.Format(CultureInfo.InvariantCulture, "{0:0}m{1}{2} b{3:0.00}", sample.AirPathLength, sample.MergedFromAirPath ? "M" : "-", sample.RepositionApplied ? "R" : "", sample.RepositionBlend)
                        : "-");
            }

            return builder.ToString();
        }

        private static string FormatRoomComparison(V2AuxSourceOcclusionSample sample)
        {
            if (!sample.RoomComparisonAvailable)
                return "?";

            if (sample.SameOxygenRoom)
                return "same";

            string reason = sample.RoomComparison ?? string.Empty;
            if (reason.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "grid";

            if (reason.IndexOf("room", StringComparison.OrdinalIgnoreCase) >= 0)
                return "diff";

            return "out";
        }

        public static List<V2AuxSourceOcclusionSample> GetRecentSamples(int maxSamples)
        {
            PurgeStale();
            List<V2AuxSourceOcclusionSample> sorted = new List<V2AuxSourceOcclusionSample>(Samples.Values);
            sorted.Sort((left, right) => right.Score.CompareTo(left.Score));
            if (maxSamples > 0 && sorted.Count > maxSamples)
                sorted.RemoveRange(maxSamples, sorted.Count - maxSamples);

            return sorted;
        }

        public static void LogIfDue()
        {
            if (!SettingsManager.Current.V2DebugLogEnabled)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastLogUtc = now;
            V2DebugLog.WriteEvent("aux-occlusion", FormatSummary() + " | " + FormatSources(4).Replace(Environment.NewLine, "; "));
        }

        private static bool IsCandidate(string kind, string cueName, IMySourceVoice voice, float score)
        {
            if (!string.Equals(kind, "S", StringComparison.OrdinalIgnoreCase))
                return false;

            if (voice == null || !voice.IsValid || !voice.IsPlaying || score <= 0.001f)
                return false;

            if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty")
                return false;

            if (V2AuxCueClassifier.IsNonWorldCue(cueName))
                return false;

            if (V2AuxCueClassifier.IsEngineCue(cueName))
                return false;

            if (V2AuxCueClassifier.IsPlayerLocalCue(cueName))
                return false;

            return true;
        }

        public static bool TryCalculate(string kind, string cueName, float score, MyEntity3DSoundEmitter emitter, out V2AuxSourceOcclusionSample sample)
        {
            sample = default(V2AuxSourceOcclusionSample);
            if (emitter == null)
                return false;

            Vector3D source = ResolveEmitterSourcePosition(emitter);
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            if (listener == Vector3D.Zero)
                listener = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return false;

            RecordRangeRelation(cueName, source, listener);

            sample = Calculate(kind, cueName, score, source, listener, "physical", ResolveEmitterEntityId(emitter));
            sample.CustomRangeApplied = TryApplyCustomRange(emitter, cueName, sample.EffectiveRange, sample.VanillaMaxDistance);
            if (sample.CustomRangeApplied)
                sample.EstimatedGain = CalculateEstimatedGain(sample, true);
            StoreSample(sample, source);
            return true;
        }

        public static bool TryCalculate(string kind, string cueName, float score, Vector3D source, out V2AuxSourceOcclusionSample sample)
        {
            sample = default(V2AuxSourceOcclusionSample);
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            if (listener == Vector3D.Zero)
                listener = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return false;

            RecordRangeRelation(cueName, source, listener);

            sample = Calculate(kind, cueName, score, source, listener, "resolved", 0L);
            StoreSample(sample, source);
            return true;
        }

        public static bool TryCalculate(string kind, string cueName, float score, Vector3D source, long sourceEntityId, out V2AuxSourceOcclusionSample sample)
        {
            sample = default(V2AuxSourceOcclusionSample);
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            if (listener == Vector3D.Zero)
                listener = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return false;

            RecordRangeRelation(cueName, source, listener);

            sample = Calculate(kind, cueName, score, source, listener, "resolved", sourceEntityId);
            StoreSample(sample, source);
            return true;
        }

        private static V2AuxSourceOcclusionSample Calculate(string kind, string cueName, float score, Vector3D source, Vector3D listener, string className, long sourceEntityId)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float distance = (float)Vector3D.Distance(source, listener);
            float pathLength = distance;
            float vanillaMaxDistance = V2BlockRangeScaler.ResolveVanillaMaxDistance(cueName, settings);
            float effectiveRange = V2BlockRangeScaler.ResolveEffectiveRange(settings, vanillaMaxDistance);
            float rangeScale = vanillaMaxDistance > 0.5f ? effectiveRange / vanillaMaxDistance : 1f;
            DateTime now = DateTime.UtcNow;
            int open;
            int blocked;
            float openWeight;
            float totalWeight;
            float weightedBlockedMeters;
            bool mainRayBlocked;
            bool rayMeasuredBlocked;
            Vector3D probeFrom = Vector3D.Zero;
            Vector3D probeTo = Vector3D.Zero;
            bool airPathAvailable;
            float airPathLength;
            Vector3D portalWorld;
            bool portalValid;
            List<Vector3D> airRoute;

            // Only probe occlusion within audible range. Beyond it, casting a ray is meaningless and
            // can spuriously report a full block (e.g. a source with a stale/far position), which would
            // silence a voice the distance/range gain already handles. Treat far sources as unoccluded.
            float maxProbeMeters = Math.Max(effectiveRange, settings.PlayerFilterBlockMaxRange) * 1.5f + 10f;
            if (distance <= maxProbeMeters)
            {
                ResolveSourceClearance(source, sourceEntityId, out long _, out float sourceClearRadius);
                ProbePath(cueName, source, listener, sourceEntityId, settings, now, sourceClearRadius, out open, out blocked, out openWeight, out totalWeight, out weightedBlockedMeters, out mainRayBlocked, out rayMeasuredBlocked, out airPathAvailable, out airPathLength, out portalWorld, out portalValid, out airRoute);
                if (settings.PlayerFilterPathDebugEnabled)
                {
                    // Expose the exact probed sub-segment (after source-clearance and listener skips) so the
                    // debug overlay draws the same span the thickness estimate measured, with the skipped
                    // near-source/near-listener stretches shown separately. Pure math, so enabling the
                    // overlay does not alter the audible occlusion result.
                    ComputeProbeEndpoints(source, listener, sourceClearRadius, out probeFrom, out probeTo);
                }
            }
            else
            {
                open = 1;
                blocked = 0;
                openWeight = 1f;
                totalWeight = 1f;
                weightedBlockedMeters = 0f;
                mainRayBlocked = false;
                rayMeasuredBlocked = false;
                airPathAvailable = false;
                airPathLength = 0f;
                portalWorld = Vector3D.Zero;
                portalValid = false;
                airRoute = null;
            }
            int rays = open + blocked;
            if (rays == 0)
            {
                open = 1;
                rays = 1;
                openWeight = 1f;
                totalWeight = 1f;
            }

            float estimatedBlockedLength = totalWeight <= 0.001f ? 0f : Math.Max(0f, weightedBlockedMeters / totalWeight);
            float openFraction = totalWeight <= 0.001f ? Clamp01(open / (float)rays) : Clamp01(openWeight / totalWeight);
            float occlusion = Clamp01(1f - openFraction);
            float blockThicknessScale = Math.Max(0.1f, settings.PlayerFilterBlockStructureThicknessScale);
            float blockOcclusionCurve = Math.Max(0.1f, settings.PlayerFilterBlockOcclusionCurve);
            float continuous = Clamp01((float)Math.Pow(occlusion, blockOcclusionCurve));
            float nearFieldScale = CalculateNearFieldOcclusionScale(distance, effectiveRange);
            nearFieldScale = CalculateStructureAwareNearFieldScale(
                nearFieldScale,
                mainRayBlocked,
                blocked,
                estimatedBlockedLength,
                blockThicknessScale);
            continuous *= nearFieldScale;
            float vanillaDistanceGain = EvaluateDistanceGain(distance, vanillaMaxDistance, settings.PlayerFilterBlockDistanceCurve);
            float desiredDistanceGain = EvaluateDistanceGain(distance, effectiveRange, settings.PlayerFilterBlockDistanceCurve);
            float distanceFactor = Clamp01((float)Math.Pow(distance / effectiveRange, Math.Max(0.1f, settings.PlayerFilterBlockDistanceCurve)));
            bool isSealed = false;
            bool roomComparisonAvailable = false;
            bool sameOxygenRoom = false;
            string roomComparison = "none";
            if (V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample playerSample))
            {
                roomComparisonAvailable = V2PlayerEnvironmentTelemetry.TryCompareOxygenRooms(listener, source, out sameOxygenRoom, out roomComparison);
                bool sourceOutsidePlayerRoom = roomComparisonAvailable && !sameOxygenRoom;
                isSealed = playerSample.SealedEstimate && sourceOutsidePlayerRoom;
            }

            float sealedExtra = isSealed ? settings.PlayerFilterBlockSealedFactor : 0f;
            float pathMuffling = continuous;
            float finalMuffling = ApplyOcclusionStrength(Clamp01(pathMuffling + (1f - pathMuffling) * sealedExtra), settings.PlayerFilterOcclusionStrength);
            finalMuffling = LimitNearSourceMuffling(finalMuffling, distance, openFraction, estimatedBlockedLength, blockThicknessScale);
            // A single source->listener ray is a binary test: it flips fully blocked/open as the listener
            // moves across wall edges, doorways, and gaps. Smoothing the per-source occlusion over time turns
            // those flips into a graded result — the temporal equivalent of averaging several spatial rays.
            finalMuffling = SmoothOcclusion(sourceEntityId, source, cueName, finalMuffling, now, settings);
            float localAtmosphere = ResolveListenerAtmosphere(source, listener);
            float occlusionMuffling = ApplyOcclusionStrength(Clamp01(continuous + (1f - continuous) * sealedExtra), settings.PlayerFilterOcclusionStrength);
            float rangeCompensation = desiredDistanceGain / Math.Max(0.05f, vanillaDistanceGain);
            float estimatedGain = CalculateEstimatedGain(continuous, sealedExtra, localAtmosphere, rangeCompensation);
            float estimatedCutoff = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterBlockMuffledFrequency, finalMuffling);

            // ---- Around-the-corner (air-diffraction) leg merge ----
            // When the straight ray is blocked but a bounded open-air detour reaches the listener (a doorway,
            // a stair bend), the sound also arrives bright via that route. Blend the two arrival paths by their
            // loudness: the structure-borne leg (muffled, straight distance) and the air leg (bright, longer
            // detour). The air leg can only ever BRIGHTEN (Math.Min on the muffle), so unobstructed and fully
            // sealed sources stay unchanged and the binary single-ray flip dissolves into a graded result.
            bool mergedFromAirPath = false;
            float preAirMuffling = finalMuffling;
            float airWeight = 0f;    // air-leg loudness weight (shared with the position blend below)
            float structWeight = 0f; // through-structure leg loudness weight
            if (mainRayBlocked && airPathAvailable)
            {
                _airPathFound = SaturatingIncrement(_airPathFound);
                float structGainDist = EvaluateDistanceGain(distance, effectiveRange, settings.PlayerFilterBlockDistanceCurve);
                float structTrans = V2PlayerEnvironmentTelemetry.CalculateThicknessTransmission(estimatedBlockedLength, blockThicknessScale);
                float structGain = Clamp01(structGainDist * structTrans);

                // Air leg muffle = brightness FLOOR + muffle accumulated over the detour length (HF loss with
                // distance), so a longer around-the-corner path arrives progressively duller, not a flat value.
                float airMuffle = Clamp01(settings.PlayerFilterBlockAirBrightness + airPathLength * Math.Max(0f, settings.PlayerFilterBlockAirLengthMuffle));
                float airGain = EvaluateDistanceGain(airPathLength, effectiveRange, settings.PlayerFilterBlockDistanceCurve);

                airWeight = airGain;
                structWeight = structGain;

                // FULLY DECOUPLED muffle: once a source is in the air-path regime (straight ray blocked AND a bounded
                // detour reaches the listener), the muffle is driven by the AIR leg ALONE - never merged with the
                // straight-through-the-structure occlusion. That structure leg (openFraction -> continuous ->
                // finalMuffling, fed by the block-thickness scale) is a DIFFERENT, weaker arrival path we no longer
                // render: the emitter has been moved to the portal, so the sound localises at the air detour, not
                // through the wall. Merging the two made the muffle LURCH as the listener crossed positions where the
                // sightline (mis)aligned with the open shaft - and turning UP block thickness amplified the lurch,
                // because a darker/quieter structure leg handed the loudness-weighted merge ever more weight to the
                // bright air leg. So block thickness now only affects sources WITHOUT an air detour (true thin walls);
                // for around-the-corner sources it touches the reposition DISTANCE blend (via structWeight) but never
                // the tone. The air leg muffle already grades smoothly with detour length, so a long way around still
                // arrives duller while a short hop stays bright, with zero dependence on the flipping straight ray.
                continuous = airMuffle;
                finalMuffling = ApplyOcclusionStrength(Clamp01(continuous + (1f - continuous) * sealedExtra), settings.PlayerFilterOcclusionStrength);
                mergedFromAirPath = true;
                _airPathMerged = SaturatingIncrement(_airPathMerged);
                estimatedGain = CalculateEstimatedGain(continuous, sealedExtra, localAtmosphere, rangeCompensation);
                estimatedCutoff = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterBlockMuffledFrequency, finalMuffling);
            }

            // ---- Emitter repositioning (Option B: position carries direction, gain carries distance) ----
            // POSITION is the portal itself - the deepest path point you can still SEE toward the source (held stable
            // by the portal hysteresis in ProbePath). It is deliberately NOT lerped by the straight-vs-air loudness
            // ratio any more: that averaging made the emitter drift erratically and pull back toward the block. The
            // loudness ratio is still computed, but only to set the perceived DISTANCE for gain - the sound localises
            // cleanly at the visible portal while still attenuating as if it travelled the real (blended) path length.
            bool repositionApplied = false;
            Vector3D repositionTarget = source;
            float repositionBlend = 0f;
            if (settings.PlayerFilterBlockRepositionEnabled && mainRayBlocked && airPathAvailable && portalValid)
            {
                repositionApplied = true;
                // Loudness ratio (air vs straight-through leg, biased) - used ONLY for the perceived distance below,
                // no longer for the position.
                float airBias = Math.Max(0.01f, settings.PlayerFilterBlockRepositionAirBias);
                float biasedAir = airWeight * airBias;
                float wsum = biasedAir + structWeight;
                repositionBlend = wsum > 1e-4f ? Clamp01(biasedAir / wsum) : 1f;
                repositionTarget = portalWorld; // sit AT the visible portal, full stop

                float repoCurve = Math.Max(0.1f, settings.PlayerFilterBlockDistanceCurve);
                float effectiveDist = distance + (airPathLength - distance) * repositionBlend;
                float repoTargetDist = (float)Vector3D.Distance(listener, repositionTarget);
                float gWant = EvaluateDistanceGain(effectiveDist, effectiveRange, repoCurve);
                float gHave = EvaluateDistanceGain(repoTargetDist, effectiveRange, repoCurve);
                estimatedGain = Clamp01(estimatedGain * Clamp01(gWant / Math.Max(0.05f, gHave)));
            }

            // Diagnostic capture (last blocked source with an air path): see why/whether it repositions.
            if (mainRayBlocked && airPathAvailable)
            {
                _dbgRepoCue = cueName;
                _dbgRepoPortalValid = portalValid;
                _dbgRepoApplied = repositionApplied;
                _dbgRepoBlend = repositionBlend;
                _dbgRepoMoveMeters = (float)Vector3D.Distance(source, repositionTarget);
                _dbgRepoAirLen = airPathLength;
            }

            V2AuxSourceOcclusionSample sample = new V2AuxSourceOcclusionSample
            {
                UpdatedUtc = now,
                CueName = cueName,
                Kind = kind,
                ClassName = className ?? "physical",
                Score = score,
                SourcePosition = source,
                ListenerPosition = listener,
                ProbeFrom = probeFrom,
                ProbeTo = probeTo,
                Distance = distance,
                PathLength = pathLength,
                MainRayBlocked = mainRayBlocked,
                EstimatedBlockedLength = estimatedBlockedLength,
                VanillaMaxDistance = vanillaMaxDistance,
                EffectiveRange = effectiveRange,
                CustomRangeApplied = false,
                VanillaDistanceGain = vanillaDistanceGain,
                DesiredDistanceGain = desiredDistanceGain,
                RangeScale = rangeScale,
                RaysCast = rays,
                OpenRays = open,
                BlockedRays = blocked,
                OpenFraction = openFraction,
                Occlusion = occlusion,
                ContinuousMuffling = continuous,
                DistanceFactor = distanceFactor,
                PathMuffling = pathMuffling,
                NearFieldScale = nearFieldScale,
                RoomComparisonAvailable = roomComparisonAvailable,
                SameOxygenRoom = sameOxygenRoom,
                RoomComparison = roomComparison,
                SealedExtraApplied = isSealed,
                FinalMuffling = finalMuffling,
                EstimatedGain = estimatedGain,
                EstimatedCutoff = estimatedCutoff,
                EstimatedQ = settings.Filter2Q,
                LocalAtmosphere = localAtmosphere,
                AirPathAvailable = airPathAvailable,
                AirPathLength = airPathLength,
                MergedFromAirPath = mergedFromAirPath,
                PreAirPathMuffling = preAirMuffling,
                PortalWorld = portalWorld,
                PortalValid = portalValid,
                RepositionApplied = repositionApplied,
                RepositionTarget = repositionTarget,
                RepositionBlend = repositionBlend,
                AirRoute = airRoute
            };
            return sample;
        }

        // The owning block entity is the authoritative physical source position. emitter.SourcePosition
        // can freeze on a stale world point — observed pinned ~60 km away at a previously-visited base
        // while the block sits a few metres from the listener — which pushes the source out of probe
        // range and silently disables occlusion (rays never fire, muffle collapses to atmosphere only).
        // The game's audio engine plays the block at its live entity position, so we trust that too and
        // fall back to SourcePosition only when no entity position is available.
        private static Vector3D ResolveEmitterSourcePosition(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return Vector3D.Zero;

            try
            {
                MyEntity entity = emitter.Entity;
                if (entity != null && entity.PositionComp != null)
                {
                    Vector3D entityPosition = entity.PositionComp.GetPosition();
                    if (entityPosition != Vector3D.Zero)
                        return entityPosition;
                }
            }
            catch
            {
            }

            return emitter.SourcePosition;
        }

        private static long ResolveEmitterEntityId(MyEntity3DSoundEmitter emitter)
        {
            return emitter?.Entity?.EntityId ?? 0L;
        }

        // Exponential moving average of the per-source occlusion, keyed by the owning block entity so each
        // physical source is smoothed independently. Replaces the spatial averaging the old multi-ray model
        // provided: a single ray slipping through a gap for a few frames now only nudges the result instead
        // of collapsing it to zero. Time constant is tunable (PlayerFilterBlockOcclusionSmoothingMs); 0 = off.
        private static float SmoothOcclusion(long sourceEntityId, Vector3D source, string cueName, float target, DateTime now, RealisticSoundPlusSettings settings)
        {
            target = Clamp01(target);
            float timeConstantMs = Math.Max(0f, settings?.PlayerFilterBlockOcclusionSmoothingMs ?? 0f);
            if (timeConstantMs <= 0.001f)
                return target;

            string key = sourceEntityId != 0L
                ? "e" + sourceEntityId.ToString(CultureInfo.InvariantCulture)
                : BuildKey(cueName, source);

            if (!OcclusionSmoothing.TryGetValue(key, out OcclusionSmoothState state) || now - state.UpdatedUtc > OcclusionSmoothingResetGap)
            {
                if (OcclusionSmoothing.Count > MaxOcclusionSmoothing)
                    PurgeOcclusionSmoothing(now);
                if (OcclusionSmoothing.Count > MaxOcclusionSmoothing)
                    OcclusionSmoothing.Clear();

                OcclusionSmoothing[key] = new OcclusionSmoothState { Muffle = target, UpdatedUtc = now };
                return target;
            }

            float elapsedMs = (float)Math.Max(0.0, (now - state.UpdatedUtc).TotalMilliseconds);
            float alpha = Clamp01(elapsedMs / timeConstantMs);
            float smoothed = Clamp01(state.Muffle + (target - state.Muffle) * alpha);
            OcclusionSmoothing[key] = new OcclusionSmoothState { Muffle = smoothed, UpdatedUtc = now };
            return smoothed;
        }

        private static void PurgeOcclusionSmoothing(DateTime now)
        {
            if (OcclusionSmoothing.Count == 0)
                return;

            List<string> remove = null;
            foreach (KeyValuePair<string, OcclusionSmoothState> pair in OcclusionSmoothing)
            {
                if (now - pair.Value.UpdatedUtc <= OcclusionSmoothingResetGap)
                    continue;

                if (remove == null)
                    remove = new List<string>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                OcclusionSmoothing.Remove(remove[i]);
        }

        private static void RecordRangeRelation(string cueName, Vector3D source, Vector3D listener)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float vanillaRange = V2BlockRangeScaler.ResolveVanillaMaxDistance(cueName, settings);
            float effectiveRange = V2BlockRangeScaler.ResolveEffectiveRange(settings, vanillaRange);
            double range = Math.Max(1.0, effectiveRange) + 0.5;
            bool withinRange = Vector3D.DistanceSquared(source, listener) <= range * range;
            if (!withinRange)
                _rangeRejects = SaturatingIncrement(_rangeRejects);
        }

        private static void StoreSample(V2AuxSourceOcclusionSample sample, Vector3D source)
        {
            string key = BuildKey(sample.CueName, source);
            if (!Samples.ContainsKey(key))
            {
                Order.Add(key);
                if (Order.Count > MaxSamples)
                {
                    string oldest = Order[0];
                    Order.RemoveAt(0);
                    Samples.Remove(oldest);
                }
            }

            Samples[key] = sample;
        }

        private static float ResolveListenerAtmosphere(Vector3D source, Vector3D listener)
        {
            if (V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample))
                return Clamp01(sample.LocalAtmosphere);

            return Clamp01(Math.Max(
                ExteriorSoundTransmission.GetAtmosphericPressure(source),
                ExteriorSoundTransmission.GetAtmosphericPressure(listener)));
        }

        private static float CalculateNearFieldOcclusionScale(float distance, float range)
        {
            range = Math.Max(1f, range);
            float clearDistance = Math.Min(12f, Math.Max(4f, range * 0.08f));
            float fullDistance = Math.Min(24f, clearDistance * 2.5f);
            if (distance <= clearDistance)
                return 0f;

            if (distance >= fullDistance)
                return 1f;

            return Clamp01((distance - clearDistance) / Math.Max(0.1f, fullDistance - clearDistance));
        }

        private static float CalculateStructureAwareNearFieldScale(float nearFieldScale, bool mainRayBlocked, int blockedRays, float estimatedBlockedLength, float structureThicknessScale)
        {
            if (!mainRayBlocked && blockedRays <= 0 && estimatedBlockedLength <= 0.001f)
                return nearFieldScale;

            float thickness = Math.Max(0f, estimatedBlockedLength);
            if (thickness <= 0.001f)
                return nearFieldScale;

            float scale = Math.Max(0.1f, structureThicknessScale);
            float thicknessFactor = Clamp01((float)Math.Sqrt(thickness / scale));
            if (mainRayBlocked)
                thicknessFactor = Math.Max(thicknessFactor, 0.25f);

            return Math.Max(nearFieldScale, thicknessFactor);
        }

        private static float CalculateEstimatedGain(V2AuxSourceOcclusionSample sample, bool customRangeApplied)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float sealedExtra = sample.SealedExtraApplied ? settings.PlayerFilterBlockSealedFactor : 0f;
            float rangeCompensation = customRangeApplied
                ? 1f
                : sample.DesiredDistanceGain / Math.Max(0.05f, sample.VanillaDistanceGain);
            return CalculateEstimatedGain(sample.ContinuousMuffling, sealedExtra, sample.LocalAtmosphere, rangeCompensation);
        }

        private static float CalculateEstimatedGain(float continuousMuffling, float sealedExtra, float localAtmosphere, float rangeCompensation)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float occlusionMuffling = ApplyOcclusionStrength(Clamp01(continuousMuffling + (1f - continuousMuffling) * sealedExtra), settings.PlayerFilterOcclusionStrength);
            float transmission = Clamp01((1f - occlusionMuffling) * Math.Max(0.15f, localAtmosphere));
            return Clamp(rangeCompensation * transmission, 0f, 6f);
        }

        private static float LimitNearSourceMuffling(float muffle, float distance, float openFraction, float estimatedBlockedLength, float structureThicknessScale)
        {
            muffle = Clamp01(muffle);
            if (distance > 4.5f)
                return muffle;

            float scale = Math.Max(0.1f, structureThicknessScale);
            bool realWallBetween = estimatedBlockedLength >= scale * 1.25f && openFraction <= 0.35f;
            if (realWallBetween)
                return muffle;

            float distanceFactor = Clamp01(distance / 4.5f);
            float maxNearMuffle = 0.03f + 0.22f * distanceFactor;
            return Math.Min(muffle, maxNearMuffle);
        }

        private static float EvaluateDistanceGain(float distance, float range, float curve)
        {
            range = Math.Max(1f, range);
            curve = Math.Max(0.1f, curve);
            float normalized = Clamp01(distance / range);
            return Clamp01(1f - (float)Math.Pow(normalized, curve));
        }

        private static void ProbePath(string cueName, Vector3D source, Vector3D listener, long sourceEntityId, RealisticSoundPlusSettings settings, DateTime now, float sourceClearRadius, out int open, out int blocked, out float openWeight, out float totalWeight, out float weightedBlockedMeters, out bool mainRayBlocked, out bool rayMeasuredBlocked, out bool airPathAvailable, out float airPathLength, out Vector3D portalWorld, out bool portalValid, out List<Vector3D> airRoute)
        {
            string key = BuildPathProbeKey(cueName, source, sourceEntityId, settings);
            if (!PathProbeCache.TryGetValue(key, out PathProbeState state))
                state = CreateDefaultPathProbe(now);

            bool probeDue = now - state.LastProbeUtc >= PathProbeInterval;
            if (probeDue)
            {
                _pathProbeCacheMisses = SaturatingIncrement(_pathProbeCacheMisses);
                PathProbeState prevState = state; // keep the previous route so it can be reused (hysteresis)
                PathProbeMeasurement measurement = ProbeSinglePath(source, listener, settings, settings.PlayerFilterBlockStructureThicknessScale, sourceClearRadius);
                state = CreatePathProbeState(measurement, now);

                // ---- air-path diagnostics (only when /rsp auxpathdebug is on; emitted on signature change) ----
                bool measuredRayBlocked = state.MainRayBlocked; // BEFORE the aperture-memory override below
                state.MeasuredRayBlocked = measuredRayBlocked;  // surfaced so the muffle can ignore a spuriously-
                                                                // clear straight ray that only threaded an opening
                string airDiagBranch = "gate-off";
                bool airDiagAnchor = false;
                int airDiagAttempts = 0;
                bool airDiagHasHidden = false;

                // Around-the-corner air-diffraction leg: only worth a flood-fill when the straight ray is
                // actually blocked. The result rides in this same 250 ms probe cache, so the bounded BFS fires
                // at most once per source per interval (and never for far/unblocked sources). The portal (the
                // doorway in the listener's line of sight) anchors emitter repositioning.
                state.AirPathProbed = true;
                state.AirPathAvailable = false;
                state.AirPathLength = 0f;
                state.PortalWorld = Vector3D.Zero;
                state.PortalValid = false;
                state.PortalFirstHiddenWorld = Vector3D.Zero;
                state.PortalHasHidden = false;
                state.AirRoute = null;

                // Carry the remembered aperture forward (it persists across probes even when THIS probe finds no
                // route). If it is still fresh we are mid-bridge across the stairwell mouth: the straight ray can
                // momentarily thread the open shaft and read clear (mainRayBlocked=false), which by itself switches
                // the whole air-path/reposition feature off (it is gated on mainRayBlocked downstream) and wipes the
                // anchor, so the search on the far side has nothing to localise to and the route collapses. While
                // the aperture is fresh, force the gate on (the MEASURED occlusion weights/tone are left untouched -
                // the merge only ever brightens) so reposition keeps running and the flood stays anchored to it.
                state.ApertureWorld = prevState.ApertureWorld;
                state.ApertureUtc = prevState.ApertureUtc;
                bool recentAperture = prevState.ApertureUtc != DateTime.MinValue && now - prevState.ApertureUtc < ApertureMemory;
                if (recentAperture)
                    state.MainRayBlocked = true;

                if (state.MainRayBlocked)
                {
                    VRage.Game.ModAPI.IMyCubeGrid sourceGrid = ResolveSourceGrid(sourceEntityId);

                    // Guard against a false-positive straight ray (it can hit the emitter's own block): if the
                    // direct line is genuinely clear of solid blocks, the source is unobstructed - drop the
                    // occlusion to open and skip the air path entirely so an in-room source is not wound onto a
                    // long detour and muffled, and the emitter is not repositioned.
                    //
                    // This used to be blocked by a blunt TIME gate (!recentAperture), which is why the muffle hung
                    // for ~1-2 s after you walked into the source's room: the aperture you refreshed on the stairs
                    // stayed "fresh" for 3 s and suppressed the clear. Use a GEOMETRY gate instead: only suppress the
                    // clear while the remembered aperture actually lies ON the listener->source line (the clear line
                    // is threading the open stairwell shaft and we are still around the corner). Once you are in the
                    // room the aperture (the stairwell mouth, now behind you) is far off that line, so we clear at
                    // once. Behind-the-listener / beyond-the-source apertures fall out naturally (segment distance).
                    bool apertureOnSightline = recentAperture
                        && state.ApertureWorld != Vector3D.Zero
                        && ApertureOnSightline(state.ApertureWorld, listener, source, StraightClearApertureOnLineM);
                    if (!apertureOnSightline && sourceGrid != null && V2GridStructureProbe.IsStraightPathOpen(sourceGrid, source, listener))
                    {
                        state.Open = 1;
                        state.Blocked = 0;
                        state.OpenWeight = 1f;
                        state.TotalWeight = 1f;
                        state.WeightedBlockedMeters = 0f;
                        state.MainRayBlocked = false;
                        state.LastProbeUtc = now;
                        state.UpdatedUtc = now;
                        if (settings.PlayerFilterPathDebugEnabled)
                            LogAirPathDiag(key, cueName, "straight-open", measuredRayBlocked, false, recentAperture,
                                ApertureAgeMs(prevState, now), false, false, false, 0, listener, source);
                        StorePathProbe(key, state, now);
                        open = state.Open;
                        blocked = state.Blocked;
                        openWeight = state.OpenWeight;
                        totalWeight = state.TotalWeight;
                        weightedBlockedMeters = state.WeightedBlockedMeters;
                        mainRayBlocked = state.MainRayBlocked;
                        rayMeasuredBlocked = state.MeasuredRayBlocked;
                        airPathAvailable = state.AirPathAvailable;
                        airPathLength = state.AirPathLength;
                        portalWorld = state.PortalWorld;
                        portalValid = state.PortalValid;
                        airRoute = state.AirRoute;
                        return;
                    }

                    // Route hysteresis (ROOT-CAUSE fix for the position rubberbanding): the flood-fill runs on a
                    // discrete grid, so when sub-cell listener jitter flips WorldToGridInteger(listener) between
                    // two cells it returns a different route/portal and the emitter snaps back and forth. Only
                    // re-run the search when the listener has actually moved past a threshold (or the route is
                    // stale); otherwise reuse the previous route/portal unchanged.
                    bool listenerMoved = Vector3D.DistanceSquared(listener, prevState.AirPathListenerPos) > AirPathRecomputeMovedSq;
                    bool intervalElapsed = now - prevState.AirPathComputeUtc >= AirPathRecomputeMinInterval;
                    bool routeStale = now - prevState.AirPathComputeUtc > AirPathRecomputeMaxAge;
                    // Full flood only when you've actually moved AND the rate-limit has elapsed (or the held route is
                    // stale). Otherwise hold the topology and slide the portal - cheap, no flood, no GC spike.
                    bool doRecompute = routeStale || (listenerMoved && intervalElapsed);
                    if (prevState.AirPathProbed && prevState.MainRayBlocked && !doRecompute)
                    {
                        airDiagBranch = "reuse";
                        state.AirPathAvailable = prevState.AirPathAvailable;
                        state.AirPathLength = prevState.AirPathLength;
                        state.PortalValid = prevState.PortalValid;
                        state.PortalFirstHiddenWorld = prevState.PortalFirstHiddenWorld;
                        state.PortalHasHidden = prevState.PortalHasHidden;
                        state.AirRoute = prevState.AirRoute;
                        state.AirPathListenerPos = prevState.AirPathListenerPos;
                        state.AirPathComputeUtc = prevState.AirPathComputeUtc;
                        // Hold the portal FROZEN between recomputes (no slide). The portal is a stable world point at
                        // the deepest cell you can see down the path; while you still have sight of it, it must not
                        // move - the temporal EMA in the reposition manager handles any small settling.
                        state.PortalWorld = prevState.PortalWorld;
                        // A held hidden-route still goes through the aperture - keep it fresh so the memory does not
                        // expire while you legitimately hold a route around the corner.
                        if (prevState.PortalHasHidden && prevState.AirPathAvailable)
                        {
                            state.ApertureWorld = prevState.PortalFirstHiddenWorld;
                            state.ApertureUtc = now;
                        }
                    }
                    else
                    {
                        int baseReach = (int)Math.Max(1f, settings.PlayerFilterBlockAirPathReach);
                        int openBias = (int)Math.Max(0f, settings.PlayerFilterBlockAirPathOpenBias);
                        bool throughBlocks = settings.PlayerFilterBlockAirPathThroughBlocks;
                        V2GridStructureProbe.SealDiagEnabled = settings.PlayerFilterPathDebugEnabled;
                        List<Vector3D> route = settings.PlayerFilterPathDebugEnabled ? new List<Vector3D>(32) : null;
                        // Anchor the fresh search box to the LAST-KNOWN aperture (stairwell mouth) so it keeps
                        // including the opening as you cross the upper floor away from it - otherwise the open route
                        // up the side stairwell falls outside the tight source<->listener box and the recompute
                        // collapses. ROOT FIX for "the route fails the moment I exit the top of the stairwell": the
                        // old anchor was gated on the route CURRENTLY being available (recentAperture OR
                        // prevState.AirPathAvailable). So a single miss made prevState.AirPathAvailable false, the
                        // anchor evaporated, the bare box no longer contained the side stairwell, and EVERY following
                        // recompute missed too (anch=False chained) until the listener happened to wander somewhere
                        // the bare box re-included the stairs. state.ApertureWorld persists across probes once any
                        // route has been found, so anchoring to it unconditionally keeps the stairwell in the box
                        // through a momentary collapse. Safe with the A* flood: a larger box is not over-explored
                        // (the search is goal-directed toward the listener), and a stale anchor is replaced the
                        // moment a fresh route resolves a new aperture.
                        Vector3D? boundsAnchor = state.ApertureWorld != Vector3D.Zero
                            ? state.ApertureWorld
                            : (Vector3D?)null;
                        airDiagBranch = "recompute-miss";
                        airDiagAnchor = boundsAnchor.HasValue;
                        if (sourceGrid != null)
                        {
                            // Adaptive reach: a sealed stairwell can sit outside the tight source<->listener box,
                            // so if no path is found, expand the search a couple of times before giving up.
                            for (int attempt = 0; attempt < 3; attempt++)
                            {
                                airDiagAttempts = attempt + 1;
                                int reach = Math.Min(16, baseReach << attempt);
                                int budget = Math.Min(32768, Math.Max(4096, reach * reach * reach * 32));
                                if (V2GridStructureProbe.TryFindAirPath(sourceGrid, source, listener, reach, budget, throughBlocks, openBias, boundsAnchor, route, out float airLen, out Vector3D portal, out bool portalOk, out Vector3D firstHidden, out bool hasHidden))
                                {
                                    airDiagBranch = "recompute-found";
                                    airDiagHasHidden = hasHidden;
                                    state.AirPathAvailable = true;
                                    state.AirPathLength = airLen;
                                    state.PortalWorld = portal;
                                    state.PortalValid = portalOk;
                                    state.PortalFirstHiddenWorld = firstHidden;
                                    state.PortalHasHidden = hasHidden;
                                    state.AirRoute = route;
                                    // Refresh the aperture memory whenever the route bends behind structure (it goes
                                    // through a real opening) so it can anchor/bridge the next probes across the mouth.
                                    if (hasHidden)
                                    {
                                        state.ApertureWorld = firstHidden;
                                        state.ApertureUtc = now;
                                    }
                                    break;
                                }
                                if (reach >= 16)
                                    break;
                            }
                        }
                        state.AirPathListenerPos = listener;
                        state.AirPathComputeUtc = now;
                    }

                    // ---- Portal hysteresis: hold the emitter at the deepest point you can SEE down the path ----
                    // In an open top room the flood keeps finding slightly different shortest routes, so a freshly
                    // recomputed portal hops between them and the emitter jitters near the stairwell mouth. Resolve
                    // it by REFUSING to retreat: while the listener still has direct sight of the previously held
                    // portal, the portal may only ADVANCE deeper (toward the source) or stay put - it never jumps
                    // back to a shallower point. It is released to the fresh portal only when sight of the held
                    // point is actually lost (you rounded a corner) or no route is available.
                    if (state.AirPathAvailable && state.PortalValid
                        && prevState.PortalValid && prevState.PortalWorld != Vector3D.Zero
                        && prevState.PortalWorld != state.PortalWorld)
                    {
                        bool heldStillVisible = V2GridStructureProbe.HasDirectSight(sourceGrid, listener, prevState.PortalWorld);
                        bool newIsDeeper = Vector3D.DistanceSquared(listener, state.PortalWorld)
                                         > Vector3D.DistanceSquared(listener, prevState.PortalWorld) + PortalHoldDepthEpsSq;
                        if (heldStillVisible && !newIsDeeper)
                        {
                            // Held portal is still in sight and the new one is not deeper -> keep the held point.
                            state.PortalWorld = prevState.PortalWorld;
                            state.PortalFirstHiddenWorld = prevState.PortalFirstHiddenWorld;
                            state.PortalHasHidden = prevState.PortalHasHidden;
                        }
                    }
                }

                state.LastProbeUtc = now;
                state.UpdatedUtc = now;
                StorePathProbe(key, state, now);

                if (settings.PlayerFilterPathDebugEnabled)
                {
                    bool ranFlood = airDiagBranch == "recompute-found" || airDiagBranch == "recompute-miss";
                    bool frontierEmpty = ranFlood && V2GridStructureProbe.LastFloodFrontierEmpty;
                    bool wrote = LogAirPathDiag(key, cueName, airDiagBranch, measuredRayBlocked, state.MainRayBlocked, recentAperture,
                        ApertureAgeMs(prevState, now), airDiagAnchor, state.AirPathAvailable, state.PortalHasHidden,
                        airDiagAttempts, listener, source,
                        ranFlood ? V2GridStructureProbe.LastFloodCells : -1,
                        ranFlood && V2GridStructureProbe.LastFloodBudgetHit,
                        frontierEmpty,
                        ranFlood ? V2GridStructureProbe.LastFloodBoxVolume : -1);

                    // On a FRESH frontier-empty miss, dump the blocks sealing the two open-air regions apart so we
                    // can identify a stairwell/opening block wrongly classified as sealing. Tied to the throttled
                    // diag write so it appears once per transition, not every probe.
                    if (wrote && frontierEmpty)
                    {
                        VRage.Game.ModAPI.IMyCubeGrid g = ResolveSourceGrid(sourceEntityId);
                        string nullSeals = V2GridStructureProbe.LastFloodSealTypes.Count > 0
                            ? string.Join(",", V2GridStructureProbe.LastFloodSealTypes)
                            : "(none)";
                        V2DebugLog.WriteEvent("air-path-seal", string.Format(CultureInfo.InvariantCulture,
                            "cue={0} | NULLSEAL-WALLS {1} | SRC {2} | ANCHOR {3} | LISTENER {4}",
                            cueName ?? "?",
                            nullSeals,
                            V2GridStructureProbe.DescribeCellsAround(g, source, 1),
                            V2GridStructureProbe.DescribeCellsAround(g, state.ApertureWorld, 1),
                            V2GridStructureProbe.DescribeCellsAround(g, listener, 1)));
                    }
                }
            }
            else
            {
                _pathProbeCacheHits = SaturatingIncrement(_pathProbeCacheHits);
                state.UpdatedUtc = now;
                PathProbeCache[key] = state;
            }

            open = state.Open;
            blocked = state.Blocked;
            openWeight = state.OpenWeight;
            totalWeight = state.TotalWeight;
            weightedBlockedMeters = state.WeightedBlockedMeters;
            mainRayBlocked = state.MainRayBlocked;
            rayMeasuredBlocked = state.MeasuredRayBlocked;
            airPathAvailable = state.AirPathAvailable;
            airPathLength = state.AirPathLength;
            portalWorld = state.PortalWorld;
            portalValid = state.PortalValid;
            airRoute = state.AirRoute;
        }

        // ---- air-path diagnostics ---------------------------------------------------------------------------
        // Per-source last-emitted signature so the log only records the MOMENT something changes (e.g. crossing
        // from the stairwell into the top floor) instead of one line every probe. Keyed by the same probe key.
        private static readonly Dictionary<string, string> AirPathDiagLast = new Dictionary<string, string>();

        private static double ApertureAgeMs(PathProbeState prevState, DateTime now)
        {
            return prevState.ApertureUtc == DateTime.MinValue ? -1.0 : (now - prevState.ApertureUtc).TotalMilliseconds;
        }

        // True only when the aperture lies in the INTERIOR of the listener->source line AND within maxPerpM of it -
        // i.e. it is genuinely BETWEEN you and the source (you are still upstairs threading the open stairwell shaft
        // down toward it). It deliberately does NOT count an aperture whose nearest point is an endpoint: once you
        // walk through the mouth into the source's room the aperture falls BEHIND the listener (projection t <= 0),
        // so this returns false and the "source directly visible -> clear" fires AT ONCE instead of hanging until the
        // 3 s aperture memory expires. The small positive t-floor keeps an aperture sitting right at the listener
        // (you are standing in the doorway) from re-arming the suppression as you cross the threshold.
        private static bool ApertureOnSightline(Vector3D aperture, Vector3D listener, Vector3D source, double maxPerpM)
        {
            Vector3D ls = source - listener;
            double lsLenSq = ls.LengthSquared();
            if (lsLenSq < 1e-9)
                return false;
            double t = Vector3D.Dot(aperture - listener, ls) / lsLenSq;
            if (t <= 0.05 || t >= 1.0)   // behind/at the listener, or beyond the source -> not between us
                return false;
            Vector3D proj = listener + ls * t;
            return Vector3D.DistanceSquared(aperture, proj) < maxPerpM * maxPerpM;
        }

        // Records WHY the air path is/ isn't available this probe: which branch ran (gate-off / straight-open /
        // reuse / recompute-found / recompute-miss), the MEASURED ray-blocked flag vs the post-aperture-override
        // gate, whether the aperture memory is still bridging, whether the flood had a bounds anchor, and the
        // listener->source separation. Emits only on signature change so the collapse transition stands out.
        private static bool LogAirPathDiag(string key, string cueName, string branch, bool measuredRayBlocked,
            bool gateOn, bool recentAperture, double apertureAgeMs, bool anchor, bool airAvailable,
            bool portalHasHidden, int attempts, Vector3D listener, Vector3D source,
            int floodCells = -1, bool budgetHit = false, bool frontierEmpty = false, long boxVolume = -1)
        {
            // Categorical fields only in the signature (cell counts jitter every probe and would spam): the failure
            // REASON (budgetHit/frontierEmpty) is what we want to see change.
            string sig = string.Format(CultureInfo.InvariantCulture,
                "{0}|gate{1}|avail{2}|hid{3}|anch{4}|recap{5}|meas{6}|bud{7}|fe{8}",
                branch, gateOn ? 1 : 0, airAvailable ? 1 : 0, portalHasHidden ? 1 : 0,
                anchor ? 1 : 0, recentAperture ? 1 : 0, measuredRayBlocked ? 1 : 0,
                budgetHit ? 1 : 0, frontierEmpty ? 1 : 0);
            string last;
            if (AirPathDiagLast.TryGetValue(key, out last) && last == sig)
                return false;
            AirPathDiagLast[key] = sig;

            double dist = Vector3D.Distance(listener, source);
            double dy = listener.Y - source.Y;
            V2DebugLog.WriteEvent("air-path-diag", string.Format(CultureInfo.InvariantCulture,
                "cue={0} branch={1} measRayBlk={2} gateOn={3} recentAp={4} apAgeMs={5:F0} anchor={6} airAvail={7} hasHidden={8} attempts={9} dist={10:F1} dy={11:F1} fail={12} cells={13} box={14}",
                cueName ?? "?", branch, measuredRayBlocked, gateOn, recentAperture, apertureAgeMs,
                anchor, airAvailable, portalHasHidden, attempts, dist, dy,
                budgetHit ? "BUDGET" : (frontierEmpty ? "FRONTIER_EMPTY" : "-"), floodCells, boxVolume));
            return true;
        }

        // The source's owning grid, for the open-air flood-fill. Mirrors ResolveSourceClearance's entity
        // lookup; returns null on the Vector3D-only path (sourceEntityId == 0) or any non-grid top parent.
        private static VRage.Game.ModAPI.IMyCubeGrid ResolveSourceGrid(long sourceEntityId)
        {
            if (sourceEntityId == 0L)
                return null;

            try
            {
                if (!MyEntities.TryGetEntityById(sourceEntityId, out MyEntity entity) || entity == null)
                    return null;

                return entity.GetTopMostParent() as VRage.Game.ModAPI.IMyCubeGrid;
            }
            catch
            {
                return null;
            }
        }

        private static PathProbeState CreateDefaultPathProbe(DateTime now)
        {
            return new PathProbeState
            {
                UpdatedUtc = now,
                LastProbeUtc = DateTime.MinValue,
                Open = 1,
                Blocked = 0,
                OpenWeight = 1f,
                TotalWeight = 1f,
                WeightedBlockedMeters = 0f,
                MainRayBlocked = false,
                Initialized = false
            };
        }

        private static PathProbeState CreatePathProbeState(PathProbeMeasurement measurement, DateTime now)
        {
            return new PathProbeState
            {
                UpdatedUtc = now,
                LastProbeUtc = now,
                Open = measurement.Open,
                Blocked = measurement.Blocked,
                OpenWeight = measurement.OpenWeight,
                TotalWeight = measurement.TotalWeight,
                WeightedBlockedMeters = measurement.WeightedBlockedMeters,
                MainRayBlocked = measurement.MainRayBlocked,
                Initialized = true
            };
        }

        private static PathProbeMeasurement ProbeSinglePath(Vector3D source, Vector3D listener, RealisticSoundPlusSettings settings, float structureThicknessScale, float sourceClearRadius)
        {
            PathProbeMeasurement measurement = new PathProbeMeasurement
            {
                Open = 1,
                Blocked = 0,
                OpenWeight = 1f,
                TotalWeight = 1f,
                WeightedBlockedMeters = 0f,
                MainRayBlocked = false
            };

            Vector3D path = listener - source;
            double length = path.Length();
            if (length <= 0.5)
                return measurement;

            // Listener is at/just outside the source block itself: there is no room for occluding
            // structure between them, so report open rather than letting the probe graze the source
            // block's own cells (which previously caused phantom close-range muffling).
            if (length <= Math.Max(0.75f, sourceClearRadius + SourceClearMargin) + 0.75)
                return measurement;

            ComputeProbeEndpoints(source, listener, sourceClearRadius, out Vector3D from, out Vector3D to);
            float blockThicknessScale = Math.Max(0.1f, structureThicknessScale);
            float voxelWeight = NormalizeVoxelWeight(settings.PlayerFilterBlockVoxelOcclusionWeight);
            _pathRays = SaturatingIncrement(_pathRays);
            bool rayAvailable = V2PlayerEnvironmentTelemetry.TryRayBlocked(from, to, out bool hit);
            float rawVoxelMeters = voxelWeight > VoxelOcclusionEpsilon
                ? EstimateVoxelBlockedLength(from, to)
                : 0f;
            float voxelMeters = rawVoxelMeters * voxelWeight;
            float voxelThreshold = Math.Max(BlockVoxelMinBlockedMeters, blockThicknessScale * 0.20f);
            bool voxelHit = voxelMeters > voxelThreshold;

            if (!rayAvailable && !voxelHit)
                return measurement;

            measurement.TotalWeight = 1f;
            measurement.MainRayBlocked = (rayAvailable && hit) || voxelHit;
            if (hit || voxelHit)
            {
                float blockedMeters = 0f;
                if (hit)
                {
                    blockedMeters = EstimateBlockedLength(from, to);
                    if (blockedMeters <= 0.001f)
                        blockedMeters = Math.Max(0.1f, blockThicknessScale * 0.25f);
                }

                if (voxelHit)
                    blockedMeters += voxelMeters;

                measurement.Open = 0;
                measurement.Blocked = 1;
                measurement.WeightedBlockedMeters = blockedMeters;

                // Thin-seal barrier loss: a thin sealed face (glass canopy, single airtight plate) should
                // muffle far more than its thickness implies. When the blocking grid face is an airtight cell
                // AND the crossing is thin, cap transmission at a thickness-independent STC floor. Min() means
                // thick sealed walls (already below the ceiling) and open gratings/voxel are unchanged.
                float transA = V2PlayerEnvironmentTelemetry.CalculateThicknessTransmission(blockedMeters, blockThicknessScale);
                if (hit && V2PlayerEnvironmentTelemetry.TryApplyThinSeal(from, to, blockedMeters, settings.PlayerFilterBlockSealedBarrierLoss, settings.PlayerFilterSealedBarrierThinFactor, ref transA))
                {
                    _thinSealHits = SaturatingIncrement(_thinSealHits);
                }

                measurement.OpenWeight = transA;
                return measurement;
            }

            measurement.Open = 1;
            measurement.Blocked = 0;
            measurement.OpenWeight = 1f;
            measurement.WeightedBlockedMeters = 0f;
            return measurement;
        }

        private static void StorePathProbe(string key, PathProbeState state, DateTime now)
        {
            if (PathProbeCache.Count > MaxPathProbeCache)
                PurgePathProbeCache(now);

            if (PathProbeCache.Count > MaxPathProbeCache)
                PathProbeCache.Clear();

            PathProbeCache[key] = state;
        }

        private static void PurgePathProbeCache(DateTime now)
        {
            if (PathProbeCache.Count == 0)
                return;

            List<string> remove = null;
            foreach (KeyValuePair<string, PathProbeState> pair in PathProbeCache)
            {
                if (now - pair.Value.UpdatedUtc <= PathProbeLifetime && PathProbeCache.Count <= MaxPathProbeCache)
                    continue;

                if (remove == null)
                    remove = new List<string>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                PathProbeCache.Remove(remove[i]);
        }

        private static void ComputeProbeEndpoints(Vector3D source, Vector3D listener, float sourceClearRadius, out Vector3D from, out Vector3D to)
        {
            from = source;
            to = listener;
            Vector3D path = listener - source;
            double length = path.Length();
            if (length <= 0.05)
                return;

            Vector3D forward = path / length;
            double proportionalSkip = Math.Max(0.75, length * 0.15);
            double clearanceSkip = Math.Max(0.75, sourceClearRadius + SourceClearMargin);
            double sourceSkip = Math.Min(MaxSourceSkipMeters, Math.Max(proportionalSkip, clearanceSkip));
            double listenerSkip = Math.Min(0.35, length * 0.1);
            if (sourceSkip + listenerSkip >= length * 0.85)
            {
                sourceSkip = length * 0.25;
                listenerSkip = length * 0.1;
            }

            from = source + forward * sourceSkip;
            to = listener - forward * listenerSkip;
        }

        // Resolves the source emitter's owning grid id and a bounding radius for its own block, so
        // the probe ray can both skip past the block (#2) and ignore source-grid hits inside that
        // radius (#1) without losing genuine same-grid walls farther along the path.
        private static void ResolveSourceClearance(Vector3D source, long sourceEntityId, out long sourceGridId, out float sourceClearRadius)
        {
            sourceGridId = 0L;
            sourceClearRadius = DefaultSourceClearRadius;
            if (sourceEntityId == 0L)
                return;

            try
            {
                if (!MyEntities.TryGetEntityById(sourceEntityId, out MyEntity entity) || entity == null)
                    return;

                IMyEntity top = entity.GetTopMostParent();
                sourceGridId = top?.EntityId ?? entity.EntityId;

                // Use the block's own bounding sphere; skip the whole-grid case so a large host grid
                // does not inflate the clearance.
                if (!(entity is MyCubeGrid) && entity.PositionComp != null)
                {
                    float radius = (float)entity.PositionComp.WorldVolume.Radius;
                    if (radius > 0.1f)
                        sourceClearRadius = Clamp(radius, 0.75f, MaxSourceClearRadius);
                }
            }
            catch
            {
            }
        }

        private static void PurgeStale()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = Order.Count - 1; i >= 0; i--)
            {
                string key = Order[i];
                if (!Samples.TryGetValue(key, out V2AuxSourceOcclusionSample sample) || now - sample.UpdatedUtc > SampleLifetime)
                {
                    Order.RemoveAt(i);
                    Samples.Remove(key);
                }
            }

            PurgePathProbeCache(now);
            PurgeOcclusionSmoothing(now);
            V2BlockRangeScaler.Update();
        }

        private static bool TryApplyCustomRange(MyEntity3DSoundEmitter emitter, string cueName, float range, float vanillaRange)
        {
            return V2BlockRangeScaler.TryApplyToEmitter(emitter, cueName, range, vanillaRange, "aux");
        }

        private static string BuildKey(string cueName, Vector3D source)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:0}:{2:0}:{3:0}",
                cueName ?? "?",
                source.X,
                source.Y,
                source.Z);
        }

        private static string BuildPathProbeKey(string cueName, Vector3D source, long sourceEntityId, RealisticSoundPlusSettings settings)
        {
            if (sourceEntityId != 0L)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "ent:{0}|t{1:0.00}|v{2:0.00}",
                    sourceEntityId,
                    settings?.PlayerFilterBlockStructureThicknessScale ?? 2.5f,
                    NormalizeVoxelWeight(settings?.PlayerFilterBlockVoxelOcclusionWeight ?? 0f));
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "pos:{0:0.0}:{1:0.0}:{2:0.0}|t{3:0.00}|v{4:0.00}",
                Quantize(source.X, PathProbeSourceCellMeters),
                Quantize(source.Y, PathProbeSourceCellMeters),
                Quantize(source.Z, PathProbeSourceCellMeters),
                settings?.PlayerFilterBlockStructureThicknessScale ?? 2.5f,
                NormalizeVoxelWeight(settings?.PlayerFilterBlockVoxelOcclusionWeight ?? 0f));
        }

        private static float EstimateVoxelBlockedLength(Vector3D from, Vector3D to)
        {
            _voxelEstimates = SaturatingIncrement(_voxelEstimates);
            return V2PlayerEnvironmentTelemetry.EstimateVoxelBlockedLength(from, to, 0.75f, 48);
        }

        private static float EstimateBlockedLength(Vector3D from, Vector3D to)
        {
            _thicknessEstimates = SaturatingIncrement(_thicknessEstimates);
            return V2PlayerEnvironmentTelemetry.EstimateBlockedLength(from, to, 0.75f, 24);
        }

        private static double Quantize(double value, double step)
        {
            if (step <= 0.0)
                return value;

            return Math.Round(value / step) * step;
        }

        private static float BlendCutoff(float clearCutoff, float muffledCutoff, float amount)
        {
            double logClear = Math.Log(Math.Max(1f, clearCutoff));
            double logMuffled = Math.Log(Math.Max(1f, muffledCutoff));
            return (float)Math.Exp(logClear + (logMuffled - logClear) * Clamp01(amount));
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

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;

            return value > max ? max : value;
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

        private static long SaturatingIncrement(long value)
        {
            return value == long.MaxValue ? value : value + 1L;
        }

        private static float NormalizeVoxelWeight(float value)
        {
            value = Math.Max(0f, value);
            return value < VoxelOcclusionMinUsefulWeight ? 0f : value;
        }

        private struct PathProbeMeasurement
        {
            public int Open;
            public int Blocked;
            public float OpenWeight;
            public float TotalWeight;
            public float WeightedBlockedMeters;
            public bool MainRayBlocked;
        }

        private struct PathProbeState
        {
            public DateTime UpdatedUtc;
            public DateTime LastProbeUtc;
            public int Open;
            public int Blocked;
            public float OpenWeight;
            public float TotalWeight;
            public float WeightedBlockedMeters;
            public bool MainRayBlocked;
            public bool MeasuredRayBlocked;         // the PHYSICS ray result BEFORE the aperture bridge override.
                                                    // false while MainRayBlocked is true = bridged: the straight
                                                    // line threaded an opening, so the measured occlusion is bright
                                                    // and must NOT drive the muffle (use the air-path leg instead).
            public bool AirPathAvailable;
            public float AirPathLength;
            public bool AirPathProbed;
            public Vector3D PortalWorld;
            public bool PortalValid;
            public Vector3D PortalFirstHiddenWorld; // route bend cell; held route -> portal slides toward it
            public bool PortalHasHidden;            // whether the route bends behind structure (portal is a graze)
            public Vector3D ApertureWorld;          // last open aperture (stairwell mouth); remembered across probes
            public DateTime ApertureUtc;            // when ApertureWorld was last refreshed by a routed flood
            public List<Vector3D> AirRoute;
            public Vector3D AirPathListenerPos; // listener position when the route was last (re)computed
            public DateTime AirPathComputeUtc;  // when the route was last (re)computed
            public bool Initialized;
        }

        private struct OcclusionSmoothState
        {
            public float Muffle;
            public DateTime UpdatedUtc;
        }
    }
}
