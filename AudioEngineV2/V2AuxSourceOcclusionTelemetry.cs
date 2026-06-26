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

            // Move the live emitter to the doorway portal (or ease it back to its block when not applied).
            // The air-path attenuation already lives in sample.EstimatedGain (applied via VolumeMultiplier).
            V2BlockEmitterReposition.Request(emitter, source, sample.PortalWorld, sample.RepositionApplied, DateTime.UtcNow);
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
                "aux rayState={4}/{5} rangeFar={0} rays={1} voxel={2} thick={3} airPath={6}/{7} thinSeal={8}",
                _rangeRejects,
                _pathRays,
                _voxelEstimates,
                _thicknessEstimates,
                _pathProbeCacheHits,
                _pathProbeCacheMisses,
                _airPathFound,
                _airPathMerged,
                _thinSealHits);
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
                        ? string.Format(CultureInfo.InvariantCulture, "{0:0}m{1}{2}", sample.AirPathLength, sample.MergedFromAirPath ? "M" : "-", sample.RepositionApplied ? "R" : "")
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
            Vector3D probeFrom = Vector3D.Zero;
            Vector3D probeTo = Vector3D.Zero;
            bool airPathAvailable;
            float airPathLength;
            Vector3D portalWorld;
            bool portalValid;

            // Only probe occlusion within audible range. Beyond it, casting a ray is meaningless and
            // can spuriously report a full block (e.g. a source with a stale/far position), which would
            // silence a voice the distance/range gain already handles. Treat far sources as unoccluded.
            float maxProbeMeters = Math.Max(effectiveRange, settings.PlayerFilterBlockMaxRange) * 1.5f + 10f;
            if (distance <= maxProbeMeters)
            {
                ResolveSourceClearance(source, sourceEntityId, out long _, out float sourceClearRadius);
                ProbePath(cueName, source, listener, sourceEntityId, settings, now, sourceClearRadius, out open, out blocked, out openWeight, out totalWeight, out weightedBlockedMeters, out mainRayBlocked, out airPathAvailable, out airPathLength, out portalWorld, out portalValid);
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
                airPathAvailable = false;
                airPathLength = 0f;
                portalWorld = Vector3D.Zero;
                portalValid = false;
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
            if (mainRayBlocked && airPathAvailable)
            {
                _airPathFound = SaturatingIncrement(_airPathFound);
                float structMuffle = finalMuffling;
                float structGainDist = EvaluateDistanceGain(distance, effectiveRange, settings.PlayerFilterBlockDistanceCurve);
                float structTrans = V2PlayerEnvironmentTelemetry.CalculateThicknessTransmission(estimatedBlockedLength, blockThicknessScale);
                float structGain = Clamp01(structGainDist * structTrans);

                float airMuffle = Clamp01(settings.PlayerFilterBlockAirBrightness); // air leg brightness floor (lower = brighter); tunable via Air Path Brightness
                float airGain = EvaluateDistanceGain(airPathLength, effectiveRange, settings.PlayerFilterBlockDistanceCurve);

                float denom = airGain + structGain;
                float mergedMuffle = denom <= 1e-3f
                    ? structMuffle
                    : (airGain * airMuffle + structGain * structMuffle) / denom;

                float newContinuous = Math.Min(continuous, mergedMuffle);
                if (newContinuous < continuous - 1e-4f)
                {
                    continuous = newContinuous;
                    finalMuffling = Math.Min(finalMuffling, mergedMuffle);
                    mergedFromAirPath = true;
                    _airPathMerged = SaturatingIncrement(_airPathMerged);
                    estimatedGain = CalculateEstimatedGain(continuous, sealedExtra, localAtmosphere, rangeCompensation);
                    estimatedCutoff = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterBlockMuffledFrequency, finalMuffling);
                }
            }

            // ---- Emitter repositioning (Option B: position = portal, distance = software gain) ----
            // When enabled and a doorway portal exists, the emitter is moved to that portal (direction anchor)
            // by V2BlockEmitterReposition. Because it then sits NEAR the listener, the engine's own 3D curve no
            // longer attenuates for the true detour distance, so we fold that falloff into the gain here: the
            // ratio gAir/gPortal expresses "how much quieter the full air path is than the doorway" using our
            // distance curve as a proxy for the engine's (the proxy cancels, leaving the air-length attenuation).
            bool repositionApplied = false;
            if (settings.PlayerFilterBlockRepositionEnabled && mainRayBlocked && airPathAvailable && portalValid)
            {
                repositionApplied = true;
                float portalDist = (float)Vector3D.Distance(listener, portalWorld);
                float repoCurve = Math.Max(0.1f, settings.PlayerFilterBlockDistanceCurve);
                float gAir = EvaluateDistanceGain(airPathLength, effectiveRange, repoCurve);
                float gPortal = EvaluateDistanceGain(portalDist, effectiveRange, repoCurve);
                float distanceComp = Clamp01(gAir / Math.Max(0.05f, gPortal));
                estimatedGain = Clamp01(estimatedGain * distanceComp);
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
                RepositionApplied = repositionApplied
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

        private static void ProbePath(string cueName, Vector3D source, Vector3D listener, long sourceEntityId, RealisticSoundPlusSettings settings, DateTime now, float sourceClearRadius, out int open, out int blocked, out float openWeight, out float totalWeight, out float weightedBlockedMeters, out bool mainRayBlocked, out bool airPathAvailable, out float airPathLength, out Vector3D portalWorld, out bool portalValid)
        {
            string key = BuildPathProbeKey(cueName, source, sourceEntityId, settings);
            if (!PathProbeCache.TryGetValue(key, out PathProbeState state))
                state = CreateDefaultPathProbe(now);

            bool probeDue = now - state.LastProbeUtc >= PathProbeInterval;
            if (probeDue)
            {
                _pathProbeCacheMisses = SaturatingIncrement(_pathProbeCacheMisses);
                PathProbeMeasurement measurement = ProbeSinglePath(source, listener, settings, settings.PlayerFilterBlockStructureThicknessScale, sourceClearRadius);
                state = CreatePathProbeState(measurement, now);

                // Around-the-corner air-diffraction leg: only worth a flood-fill when the straight ray is
                // actually blocked. The result rides in this same 250 ms probe cache, so the bounded BFS fires
                // at most once per source per interval (and never for far/unblocked sources). The portal (the
                // doorway in the listener's line of sight) anchors emitter repositioning.
                state.AirPathProbed = true;
                state.AirPathAvailable = false;
                state.AirPathLength = 0f;
                state.PortalWorld = Vector3D.Zero;
                state.PortalValid = false;
                if (state.MainRayBlocked)
                {
                    VRage.Game.ModAPI.IMyCubeGrid sourceGrid = ResolveSourceGrid(sourceEntityId);
                    int reach = (int)Math.Max(1f, settings.PlayerFilterBlockAirPathReach);
                    int budget = Math.Min(32768, Math.Max(4096, reach * reach * reach * 32));
                    if (sourceGrid != null && V2GridStructureProbe.TryFindAirPath(sourceGrid, source, listener, reach, budget, settings.PlayerFilterBlockAirPathThroughBlocks, out float airLen, out Vector3D portal, out bool portalOk))
                    {
                        state.AirPathAvailable = true;
                        state.AirPathLength = airLen;
                        state.PortalWorld = portal;
                        state.PortalValid = portalOk;
                    }
                }

                state.LastProbeUtc = now;
                state.UpdatedUtc = now;
                StorePathProbe(key, state, now);
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
            airPathAvailable = state.AirPathAvailable;
            airPathLength = state.AirPathLength;
            portalWorld = state.PortalWorld;
            portalValid = state.PortalValid;
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
                float lossA = Clamp01(settings.PlayerFilterBlockSealedBarrierLoss);
                if (lossA > 0f && hit && blockedMeters < blockThicknessScale * Math.Max(0.05f, settings.PlayerFilterSealedBarrierThinFactor)
                    && V2PlayerEnvironmentTelemetry.TryGetFirstGridHitFace(from, to, out VRage.Game.ModAPI.IMyCubeGrid sealGrid, out Vector3I sealCell)
                    && V2GridStructureProbe.IsCellAirtight(sealGrid, sealCell))
                {
                    transA = Math.Min(transA, 1f - lossA);
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
            public bool AirPathAvailable;
            public float AirPathLength;
            public bool AirPathProbed;
            public Vector3D PortalWorld;
            public bool PortalValid;
            public bool Initialized;
        }

        private struct OcclusionSmoothState
        {
            public float Muffle;
            public DateTime UpdatedUtc;
        }
    }
}
