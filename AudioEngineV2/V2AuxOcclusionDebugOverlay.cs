using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    // Draws the live occlusion rays from the listener to each discovered non-engine block/world sound
    // source, colour-coded to show where the path picks up its thickness value:
    //   green  = open / transmitting (no thickness)
    //   orange = structure (grid blocks counted by the physics hit list)
    //   amber  = voxel terrain (counted by the per-segment content probe)
    //   dim    = near-source clearance + near-listener stretches the probe deliberately skips
    // The thickness geometry is throttled per source; the ray endpoints follow the live sample so the
    // lines track the listener in real time. Toggle: /rsp auxpathdebug (PlayerFilterPathDebugEnabled).
    internal static class V2AuxOcclusionDebugOverlay
    {
        private static readonly Color OpenColor = new Color(55, 230, 110, 210);
        private static readonly Color StructureColor = new Color(255, 125, 35, 240);
        private static readonly Color VoxelColor = new Color(240, 205, 60, 235);
        private static readonly Color SkippedColor = new Color(120, 120, 130, 110);
        private static readonly Color SourceMarkerColor = new Color(60, 220, 255, 235);
        private static readonly Color AirRouteColor = new Color(80, 200, 255, 230);
        private static readonly Color TextColor = new Color(175, 235, 255, 255);
        private static readonly Color LegendHeaderColor = new Color(205, 240, 255, 255);
        private static readonly Color SkippedLegendColor = new Color(150, 150, 160, 255);
        private const float LegendScale = 0.6f;

        private const int MaxDebugPaths = 24;
        private const int MaxCacheEntries = 64;
        private const float VoxelWeightThreshold = 0.05f;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

        private static readonly Dictionary<string, CachedPath> Cache = new Dictionary<string, CachedPath>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<ThicknessSegment> SegmentBuffer = new List<ThicknessSegment>(48);
        private static readonly List<float> PointBuffer = new List<float>(64);
        private static readonly List<string> StaleKeys = new List<string>(16);

        public static void Draw()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.PlayerFilterPathDebugEnabled)
                return;

            if (MyAPIGateway.Session?.Camera == null)
                return;

            DateTime now = DateTime.UtcNow;
            PurgeCache(now);

            bool includeVoxels = settings.PlayerFilterBlockVoxelOcclusionWeight >= VoxelWeightThreshold;
            List<V2AuxSourceOcclusionSample> samples = V2AuxSourceOcclusionTelemetry.GetRecentSamples(MaxDebugPaths);
            for (int i = 0; i < samples.Count; i++)
                DrawSample(samples[i], includeVoxels, now);

            // Portal symbol at each repositioned emitter's actual smoothed position (magenta) - where you hear
            // it. The air-path route line per sample ends near here; together they show the around-corner path.
            V2BlockEmitterReposition.DrawActive();

            DrawLegend(includeVoxels);
        }

        private static void DrawLegend(bool includeVoxels)
        {
            const float x = 0.012f;
            float y = 0.305f;
            DrawText2D(x, y, "Block occlusion rays - thickness source", LegendHeaderColor);
            DrawText2D(x, y + 0.024f, "open / transmitting", OpenColor);
            DrawText2D(x, y + 0.046f, "structure (grid blocks)", StructureColor);
            DrawText2D(x, y + 0.068f, includeVoxels ? "voxel terrain" : "voxel terrain (weight 0 - off)", includeVoxels ? VoxelColor : SkippedLegendColor);
            DrawText2D(x, y + 0.090f, "skipped near source / listener", SkippedLegendColor);
        }

        private static void DrawText2D(float normalizedX, float normalizedY, string text, Color color)
        {
            Vector2 screen = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(new Vector2(normalizedX, normalizedY), false);
            MyRenderProxy.DebugDrawText2D(screen, text, color, LegendScale, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, false);
        }

        private static void DrawSample(V2AuxSourceOcclusionSample sample, bool includeVoxels, DateTime now)
        {
            Vector3D source = sample.SourcePosition;
            Vector3D listener = sample.ListenerPosition;
            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return;

            // ProbeFrom/ProbeTo are the exact stretch the thickness estimate measured (source clearance and
            // listener skips removed); fall back to the raw endpoints when they were not computed.
            Vector3D from = sample.ProbeFrom != Vector3D.Zero ? sample.ProbeFrom : source;
            Vector3D to = sample.ProbeTo != Vector3D.Zero ? sample.ProbeTo : listener;

            CachedPath cached = GetOrRefresh(BuildKey(sample), from, to, includeVoxels, now);
            BuildSegments(from, to, cached.Structure, cached.Voxel, SegmentBuffer);

            // Skipped end caps: drawn dim so they read as "present on the path but not measured".
            if ((from - source).LengthSquared() > 0.04)
                DrawLine(source, from, SkippedColor);
            if ((listener - to).LengthSquared() > 0.04)
                DrawLine(to, listener, SkippedColor);

            float structureMeters = 0f;
            float voxelMeters = 0f;
            if (SegmentBuffer.Count == 0)
            {
                DrawLine(from, to, OpenColor);
            }
            else
            {
                for (int i = 0; i < SegmentBuffer.Count; i++)
                {
                    ThicknessSegment segment = SegmentBuffer[i];
                    DrawLine(segment.From, segment.To, ColorFor(segment.Kind));
                    if (segment.Kind == ThicknessSegmentKind.Structure)
                        structureMeters += segment.Meters;
                    else if (segment.Kind == ThicknessSegmentKind.Voxel)
                        voxelMeters += segment.Meters;
                }
            }

            MyRenderProxy.DebugDrawSphere(source, 0.16f, SourceMarkerColor, 0.85f, false, false, false, false);

            // Headline thickness (sample.EstimatedBlockedLength) is the weighted value the filter consumes;
            // str/vox are the raw measured lengths behind the coloured segments. A gap between them and t
            // is itself a clue (voxel weighting, range clamps, temporal smoothing), so they are shown apart
            // rather than as an equation.
            string detail = string.Format(
                CultureInfo.InvariantCulture,
                "t{0:0.0}m str{1:0.0} vox{2:0.0}  muf{3:0.00} g{4:0.00} {5:0}Hz",
                sample.EstimatedBlockedLength,
                structureMeters,
                voxelMeters,
                sample.FinalMuffling,
                sample.EstimatedGain,
                sample.EstimatedCutoff);

            DrawText(source, Trim(sample.CueName, 26), 0.5);
            DrawText(source, detail, 0.28);

            // Air-diffraction leg: when a flood-fill open-air detour was found, show its length and whether it
            // actually brightened the source (the cascade's around-the-corner path).
            if (sample.AirPathAvailable)
                DrawText(source, string.Format(CultureInfo.InvariantCulture, "air {0:0.0}m{1}", sample.AirPathLength, sample.MergedFromAirPath ? "  *merged*" : ""), 0.06);

            // Surface air-path route: the actual line the sound travels around corners / up the stairwell, from
            // the source to the listener side. The portal symbol (drawn by the reposition manager) sits at the
            // smoothed emitter position - i.e. where you actually hear it - so the two together read as
            // "straight line through blocks (the coloured ray above) + surface air path to the portal".
            if (sample.AirRoute != null && sample.AirRoute.Count >= 2)
            {
                for (int i = 0; i < sample.AirRoute.Count - 1; i++)
                    DrawLine(sample.AirRoute[i], sample.AirRoute[i + 1], AirRouteColor);
            }
            else if (sample.PortalValid && sample.PortalWorld != Vector3D.Zero && sample.ListenerPosition != Vector3D.Zero)
            {
                DrawLine(sample.ListenerPosition, sample.PortalWorld, AirRouteColor);
            }
        }

        private static CachedPath GetOrRefresh(string key, Vector3D from, Vector3D to, bool includeVoxels, DateTime now)
        {
            if (!Cache.TryGetValue(key, out CachedPath cached))
            {
                cached = new CachedPath();
                Cache[key] = cached;
                Refresh(cached, from, to, includeVoxels, now);
            }
            else if (now - cached.UpdatedUtc >= RefreshInterval)
            {
                Refresh(cached, from, to, includeVoxels, now);
            }

            return cached;
        }

        private static void Refresh(CachedPath cached, Vector3D from, Vector3D to, bool includeVoxels, DateTime now)
        {
            V2PlayerEnvironmentTelemetry.TryProbeThicknessIntervals(from, to, includeVoxels, cached.Structure, cached.Voxel);
            cached.UpdatedUtc = now;
        }

        // Sweep the (fractional) structure and voxel intervals into contiguous, classified world-space
        // segments. Structure wins where it overlaps voxel terrain. Adjacent same-kind spans coalesce so a
        // run of probe segments draws as a single line.
        private static void BuildSegments(Vector3D from, Vector3D to, List<ThicknessInterval> structure, List<ThicknessInterval> voxel, List<ThicknessSegment> output)
        {
            output.Clear();
            double length = (to - from).Length();
            if (length <= 0.001)
                return;

            PointBuffer.Clear();
            PointBuffer.Add(0f);
            PointBuffer.Add(1f);
            AddBounds(structure, PointBuffer);
            AddBounds(voxel, PointBuffer);
            PointBuffer.Sort();

            const float epsilon = 0.0005f;
            float previous = PointBuffer[0];
            for (int i = 1; i < PointBuffer.Count; i++)
            {
                float point = PointBuffer[i];
                if (point - previous < epsilon)
                    continue;

                float mid = (previous + point) * 0.5f;
                ThicknessSegmentKind kind = Covered(structure, mid)
                    ? ThicknessSegmentKind.Structure
                    : Covered(voxel, mid)
                        ? ThicknessSegmentKind.Voxel
                        : ThicknessSegmentKind.Open;

                AppendSegment(output, from, to, length, previous, point, kind);
                previous = point;
            }
        }

        private static void AppendSegment(List<ThicknessSegment> output, Vector3D from, Vector3D to, double length, float startFraction, float endFraction, ThicknessSegmentKind kind)
        {
            float meters = (float)((endFraction - startFraction) * length);
            if (output.Count > 0)
            {
                ThicknessSegment last = output[output.Count - 1];
                if (last.Kind == kind)
                {
                    last.To = Vector3D.Lerp(from, to, endFraction);
                    last.Meters += meters;
                    output[output.Count - 1] = last;
                    return;
                }
            }

            output.Add(new ThicknessSegment
            {
                From = Vector3D.Lerp(from, to, startFraction),
                To = Vector3D.Lerp(from, to, endFraction),
                Kind = kind,
                Meters = meters
            });
        }

        private static void AddBounds(List<ThicknessInterval> intervals, List<float> points)
        {
            for (int i = 0; i < intervals.Count; i++)
            {
                points.Add(Clamp01(intervals[i].Start));
                points.Add(Clamp01(intervals[i].End));
            }
        }

        private static bool Covered(List<ThicknessInterval> intervals, float fraction)
        {
            for (int i = 0; i < intervals.Count; i++)
            {
                if (fraction >= intervals[i].Start && fraction <= intervals[i].End)
                    return true;
            }

            return false;
        }

        private static void PurgeCache(DateTime now)
        {
            if (Cache.Count == 0)
                return;

            StaleKeys.Clear();
            foreach (KeyValuePair<string, CachedPath> pair in Cache)
            {
                if (now - pair.Value.UpdatedUtc > CacheLifetime)
                    StaleKeys.Add(pair.Key);
            }

            for (int i = 0; i < StaleKeys.Count; i++)
                Cache.Remove(StaleKeys[i]);

            if (Cache.Count > MaxCacheEntries)
                Cache.Clear();
        }

        private static Color ColorFor(ThicknessSegmentKind kind)
        {
            switch (kind)
            {
                case ThicknessSegmentKind.Structure:
                    return StructureColor;
                case ThicknessSegmentKind.Voxel:
                    return VoxelColor;
                default:
                    return OpenColor;
            }
        }

        private static void DrawLine(Vector3D from, Vector3D to, Color color)
        {
            MyRenderProxy.DebugDrawLine3D(from, to, color, color, false, false);
        }

        private static void DrawText(Vector3D position, string text, double upOffset)
        {
            Vector3D camera = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (camera != Vector3D.Zero)
            {
                Vector3D offset = position - camera;
                if (offset.LengthSquared() > 1.0)
                {
                    offset.Normalize();
                    position -= offset * 0.15;
                }
            }

            MyRenderProxy.DebugDrawText3D(position + Vector3D.Up * upOffset, text, TextColor, 0.55f, false);
        }

        private static string BuildKey(V2AuxSourceOcclusionSample sample)
        {
            Vector3D source = sample.SourcePosition;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:0}:{2:0}:{3:0}",
                sample.CueName ?? "?",
                source.X,
                source.Y,
                source.Z);
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

        private sealed class CachedPath
        {
            public readonly List<ThicknessInterval> Structure = new List<ThicknessInterval>(8);
            public readonly List<ThicknessInterval> Voxel = new List<ThicknessInterval>(8);
            public DateTime UpdatedUtc;
        }
    }
}
