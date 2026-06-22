using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Audio;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2AuxSourceOcclusionTelemetry
    {
        private const int MaxSamples = 32;
        private const int MaxCalculationCache = 96;
        private static readonly TimeSpan SampleLifetime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CalculationCacheLifetime = TimeSpan.FromMilliseconds(200);
        private static readonly Dictionary<string, V2AuxSourceOcclusionSample> Samples = new Dictionary<string, V2AuxSourceOcclusionSample>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, V2AuxSourceOcclusionSample> CalculationCache = new Dictionary<string, V2AuxSourceOcclusionSample>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Order = new List<string>();
        private static DateTime _lastLogUtc = DateTime.MinValue;

        public static void Reset()
        {
            Samples.Clear();
            CalculationCache.Clear();
            Order.Clear();
            _lastLogUtc = DateTime.MinValue;
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

            Vector3D source = emitter.SourcePosition;
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            if (listener == Vector3D.Zero)
                listener = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return;

            V2AuxSourceOcclusionSample sample = Calculate(kind, cueName, score, source, listener, "physical");
            sample.CustomRangeApplied = TryApplyCustomRange(emitter, cueName, sample.EffectiveRange, sample.VanillaMaxDistance);
            if (sample.CustomRangeApplied)
                sample.EstimatedGain = CalculateEstimatedGain(sample, true);

            StoreSample(sample, source);
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

        public static string FormatSources(int maxLines)
        {
            PurgeStale();
            if (Samples.Count == 0)
                return "cue/class  dist  range  open near thick  room seal muff  cutoff  gain";

            List<V2AuxSourceOcclusionSample> sorted = new List<V2AuxSourceOcclusionSample>(Samples.Values);
            sorted.Sort((left, right) => right.Score.CompareTo(left.Score));

            StringBuilder builder = new StringBuilder();
            builder.Append("cue/class  dist  range  open near thick  room seal muff  cutoff  gain");
            int count = Math.Min(maxLines, sorted.Count);
            for (int i = 0; i < count; i++)
            {
                V2AuxSourceOcclusionSample sample = sorted[i];
                builder.AppendLine();
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0,-18} {1,4:0}m {2,3:0}/{3,3:0}{8}m {4,4:0.00} {12,4:0.00} {9,4:0.0}m {11,-4} {10} {5,4:0.00} {6,5:0}Hz {7,4:0.00}",
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
                    sample.NearFieldScale);
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

            Vector3D source = emitter.SourcePosition;
            Vector3D listener = AudioEngineV2Runtime.Listener.Position;
            if (listener == Vector3D.Zero)
                listener = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return false;

            sample = Calculate(kind, cueName, score, source, listener, "physical");
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

            sample = Calculate(kind, cueName, score, source, listener, "resolved");
            StoreSample(sample, source);
            return true;
        }

        private static V2AuxSourceOcclusionSample Calculate(string kind, string cueName, float score, Vector3D source, Vector3D listener, string className)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            float distance = (float)Vector3D.Distance(source, listener);
            float pathLength = distance;
            float vanillaMaxDistance = V2BlockRangeScaler.ResolveVanillaMaxDistance(cueName, settings);
            float rangeScale = Math.Max(0.1f, settings.PlayerFilterBlockRangeScale);
            float effectiveRange = V2BlockRangeScaler.ResolveEffectiveRange(settings, vanillaMaxDistance);
            string cacheKey = BuildCalculationKey(cueName, source, listener, settings);
            DateTime now = DateTime.UtcNow;
            if (CalculationCache.TryGetValue(cacheKey, out V2AuxSourceOcclusionSample cached)
                && now - cached.UpdatedUtc <= CalculationCacheLifetime)
            {
                cached.UpdatedUtc = now;
                cached.Kind = kind;
                cached.ClassName = className ?? cached.ClassName;
                cached.Score = score;
                return cached;
            }

            ProbePath(source, listener, settings, out int open, out int blocked, out float openWeight, out float totalWeight, out float weightedBlockedMeters);
            TryFindFirstBlockedPosition(source, listener, out bool mainRayBlocked, out Vector3D firstBlockedPosition);
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
            float pathMuffling = Clamp01(continuous + (1f - continuous) * distanceFactor);
            float finalMuffling = ApplyOcclusionStrength(Clamp01(pathMuffling + (1f - pathMuffling) * sealedExtra), settings.PlayerFilterOcclusionStrength);
            finalMuffling = LimitNearSourceMuffling(finalMuffling, distance, openFraction, estimatedBlockedLength, blockThicknessScale);
            float localAtmosphere = ResolveListenerAtmosphere(source, listener);
            float occlusionMuffling = ApplyOcclusionStrength(Clamp01(continuous + (1f - continuous) * sealedExtra), settings.PlayerFilterOcclusionStrength);
            float rangeCompensation = desiredDistanceGain / Math.Max(0.05f, vanillaDistanceGain);
            float estimatedGain = CalculateEstimatedGain(continuous, sealedExtra, localAtmosphere, rangeCompensation);
            float estimatedCutoff = BlendCutoff(settings.Filter2Frequency, settings.PlayerFilterBlockMuffledFrequency, finalMuffling);

            V2AuxSourceOcclusionSample sample = new V2AuxSourceOcclusionSample
            {
                UpdatedUtc = now,
                CueName = cueName,
                Kind = kind,
                ClassName = className ?? "physical",
                Score = score,
                SourcePosition = source,
                ListenerPosition = listener,
                FirstBlockedPosition = firstBlockedPosition,
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
                LocalAtmosphere = localAtmosphere
            };
            StoreCalculation(cacheKey, sample);
            return sample;
        }

        private static void StoreCalculation(string cacheKey, V2AuxSourceOcclusionSample sample)
        {
            if (CalculationCache.Count > MaxCalculationCache)
                PurgeCalculationCache();

            CalculationCache[cacheKey] = sample;
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

        private static void ProbePath(Vector3D source, Vector3D listener, RealisticSoundPlusSettings settings, out int open, out int blocked, out float openWeight, out float totalWeight, out float weightedBlockedMeters)
        {
            open = 0;
            blocked = 0;
            openWeight = 0f;
            totalWeight = 0f;
            weightedBlockedMeters = 0f;
            Vector3D path = listener - source;
            double length = path.Length();
            if (length <= 0.5)
            {
                open = 1;
                openWeight = 1f;
                totalWeight = 1f;
                return;
            }

            Vector3D forward = path / length;
            Vector3D right = MyAPIGateway.Session?.Camera?.WorldMatrix.Right ?? Vector3D.Right;
            Vector3D up = MyAPIGateway.Session?.Camera?.WorldMatrix.Up ?? Vector3D.Up;
            ComputeProbeEndpoints(source, listener, out Vector3D fromBase, out Vector3D toBase);
            float blockThicknessScale = Math.Max(0.1f, settings.PlayerFilterBlockStructureThicknessScale);
            float voxelWeight = Math.Max(0f, settings.PlayerFilterVoxelOcclusionWeight);
            Vector3D[] offsets =
            {
                Vector3D.Zero,
                right * 0.35,
                -right * 0.35,
                up * 0.35,
                -up * 0.35
            };

            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3D to = toBase + offsets[i];
                bool rayAvailable = V2PlayerEnvironmentTelemetry.TryRayBlocked(fromBase, to, out bool hit);
                float voxelMeters = voxelWeight > 0.001f
                    ? V2PlayerEnvironmentTelemetry.EstimateVoxelBlockedLength(fromBase, to, 0.75f, 48)
                    : 0f;
                bool voxelHit = voxelMeters > 0.001f;
                if (rayAvailable || voxelHit)
                {
                    totalWeight += 1f;
                    if (hit || voxelHit)
                    {
                        blocked++;
                        float blockedMeters = hit ? V2PlayerEnvironmentTelemetry.EstimateBlockedLength(fromBase, to, 0.5f, 48) : 0f;
                        if (voxelHit)
                            blockedMeters += voxelMeters * voxelWeight;

                        if (blockedMeters <= 0.001f)
                            blockedMeters = Math.Max(0.1f, blockThicknessScale);

                        weightedBlockedMeters += blockedMeters;
                        openWeight += V2PlayerEnvironmentTelemetry.CalculateThicknessTransmission(blockedMeters, blockThicknessScale);
                    }
                    else
                    {
                        open++;
                        openWeight += 1f;
                    }
                }
            }
        }

        private static bool TryFindFirstBlockedPosition(Vector3D source, Vector3D listener, out bool blocked, out Vector3D firstBlockedPosition)
        {
            blocked = false;
            firstBlockedPosition = Vector3D.Zero;

            Vector3D path = listener - source;
            double length = path.Length();
            if (length <= 0.5)
                return false;

            ComputeProbeEndpoints(source, listener, out Vector3D from, out Vector3D to);
            bool rayAvailable = V2PlayerEnvironmentTelemetry.TryRayBlocked(from, to, out bool hit);
            float voxelWeight = Math.Max(0f, SettingsManager.Current?.PlayerFilterVoxelOcclusionWeight ?? 0f);
            float voxelMeters = voxelWeight > 0.001f
                ? V2PlayerEnvironmentTelemetry.EstimateVoxelBlockedLength(from, to, 0.75f, 48)
                : 0f;
            if ((!rayAvailable || !hit) && voxelMeters <= 0.001f)
                return true;

            blocked = true;
            double low = 0.0;
            double high = 1.0;
            for (int i = 0; i < 12; i++)
            {
                double mid = (low + high) * 0.5;
                Vector3D probe = Vector3D.Lerp(from, to, mid);
                if (V2PlayerEnvironmentTelemetry.TryRayBlocked(from, probe, out bool midHit) && midHit)
                    high = mid;
                else
                    low = mid;
            }

            firstBlockedPosition = Vector3D.Lerp(from, to, high);
            return true;
        }

        private static void ComputeProbeEndpoints(Vector3D source, Vector3D listener, out Vector3D from, out Vector3D to)
        {
            from = source;
            to = listener;
            Vector3D path = listener - source;
            double length = path.Length();
            if (length <= 0.05)
                return;

            Vector3D forward = path / length;
            double sourceSkip = Math.Min(2.5, Math.Max(0.75, length * 0.15));
            double listenerSkip = Math.Min(0.35, length * 0.1);
            if (sourceSkip + listenerSkip >= length * 0.85)
            {
                sourceSkip = length * 0.25;
                listenerSkip = length * 0.1;
            }

            from = source + forward * sourceSkip;
            to = listener - forward * listenerSkip;
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

            PurgeCalculationCache();
            V2BlockRangeScaler.Update();
        }

        private static bool TryApplyCustomRange(MyEntity3DSoundEmitter emitter, string cueName, float range, float vanillaRange)
        {
            return V2BlockRangeScaler.TryApplyToEmitter(emitter, cueName, range, vanillaRange, "aux");
        }

        private static void PurgeCalculationCache()
        {
            DateTime now = DateTime.UtcNow;
            List<string> remove = null;
            foreach (KeyValuePair<string, V2AuxSourceOcclusionSample> pair in CalculationCache)
            {
                if (now - pair.Value.UpdatedUtc <= CalculationCacheLifetime && CalculationCache.Count <= MaxCalculationCache)
                    continue;

                if (remove == null)
                    remove = new List<string>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                CalculationCache.Remove(remove[i]);
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

        private static string BuildCalculationKey(string cueName, Vector3D source, Vector3D listener, RealisticSoundPlusSettings settings)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:0.0}:{2:0.0}:{3:0.0}>{4:0.0}:{5:0.0}:{6:0.0}|r{7:0.00}|f{8:0}|c{9:0.00}|o{10:0.00}|t{11:0.00}|bc{12:0.00}|v{13:0.00}|seal{14:0.00}",
                cueName ?? "?",
                source.X,
                source.Y,
                source.Z,
                listener.X,
                listener.Y,
                listener.Z,
                settings?.PlayerFilterBlockRangeScale ?? 1f,
                settings?.PlayerFilterBlockRange ?? 80f,
                settings?.PlayerFilterBlockDistanceCurve ?? 1f,
                settings?.PlayerFilterOcclusionStrength ?? 1f,
                settings?.PlayerFilterBlockStructureThicknessScale ?? 2.5f,
                settings?.PlayerFilterBlockOcclusionCurve ?? 1f,
                settings?.PlayerFilterVoxelOcclusionWeight ?? 2f,
                settings?.PlayerFilterBlockSealedFactor ?? 0f);
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
    }
}
