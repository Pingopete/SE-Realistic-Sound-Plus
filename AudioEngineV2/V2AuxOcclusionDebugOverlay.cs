using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2AuxOcclusionDebugOverlay
    {
        private static readonly Color ClearColor = new Color(40, 255, 90, 230);
        private static readonly Color BlockedColor = new Color(255, 50, 45, 240);
        private static readonly Color TextColor = new Color(170, 235, 255, 255);
        private const int MaxDebugPaths = 24;

        public static void Draw()
        {
            if (!SettingsManager.Current.PlayerFilterPathDebugEnabled)
                return;

            if (MyAPIGateway.Session?.Camera == null)
                return;

            List<V2AuxSourceOcclusionSample> samples = V2AuxSourceOcclusionTelemetry.GetRecentSamples(MaxDebugPaths);
            for (int i = 0; i < samples.Count; i++)
                DrawSample(samples[i]);
        }

        private static void DrawSample(V2AuxSourceOcclusionSample sample)
        {
            Vector3D source = sample.SourcePosition;
            Vector3D listener = sample.ListenerPosition;
            if (source == Vector3D.Zero || listener == Vector3D.Zero)
                return;

            if (sample.MainRayBlocked && sample.FirstBlockedPosition != Vector3D.Zero)
            {
                DrawLine(source, sample.FirstBlockedPosition, ClearColor);
                DrawLine(sample.FirstBlockedPosition, listener, BlockedColor);
                MyRenderProxy.DebugDrawSphere(sample.FirstBlockedPosition, 0.18f, BlockedColor, 0.85f, false, false, false, false);
            }
            else
            {
                DrawLine(source, listener, ClearColor);
            }

            string value = string.Format(
                CultureInfo.InvariantCulture,
                "m{0:0.00} g{1:0.00} {2:0}Hz t{3:0.0}m",
                sample.FinalMuffling,
                sample.EstimatedGain,
                sample.EstimatedCutoff,
                sample.EstimatedBlockedLength);

            DrawText(source, value);
        }

        private static void DrawLine(Vector3D from, Vector3D to, Color color)
        {
            MyRenderProxy.DebugDrawLine3D(from, to, color, color, false, false);
        }

        private static void DrawText(Vector3D position, string text)
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

            MyRenderProxy.DebugDrawText3D(position + Vector3D.Up * 0.35, text, TextColor, 0.55f, false);
        }
    }
}
