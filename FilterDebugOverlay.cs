using System;
using System.Globalization;
using RealisticSoundPlus.AudioEngineV2;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus
{
    internal static class FilterDebugOverlay
    {
        private static readonly Color HeaderColor = new Color(120, 220, 255, 255);
        private static readonly Color TextColor = new Color(225, 235, 240, 255);
        private static readonly Color DimColor = new Color(165, 180, 190, 255);

        public static bool Enabled => SettingsManager.Current.FilterOverlayEnabled;

        public static void Toggle()
        {
            SetEnabled(!Enabled);
        }

        public static void SetEnabled(bool enabled)
        {
            SettingsManager.Current.FilterOverlayEnabled = enabled;
        }

        public static void Draw()
        {
            if (!Enabled)
                return;

            try
            {
                Vector2 viewportSize = GetViewportSize();
                float x = Math.Max(24f, viewportSize.X - 650f);
                float y = Math.Max(60f, viewportSize.Y * 0.075f);
                float row = 20f;
                int line = 0;

                Draw(x, y + line++ * row, "Realistic Sound+ filter controllers  |  /rsp filters off", HeaderColor, 0.62f);
                line++;

                RealisticSoundPlusSettings s = SettingsManager.Current;
                Draw(x, y + line++ * row, "ENGINE FILTER", HeaderColor, 0.50f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "routes: external={0} internal={1} dynamic={2}", s.EngineFilter, s.InternalEngineFilter, s.EngineFilterDynamic ? "on" : "off"), TextColor, 0.43f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "engine atm override: {0}", s.V2AtmosphereOverrideEnabled ? s.V2AtmosphereOverride.ToString("0.00", CultureInfo.InvariantCulture) : "real"), TextColor, 0.43f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "air: near={0:0}Hz far={1:0}Hz range={2:0}m curve={3:0.00} q={4:0.00} interiorBlend={5:0.00}", s.EngineFilterAirNearFrequency, s.EngineFilterAirFarFrequency, s.EngineFilterAirRange, s.EngineFilterAirDistanceCurve, s.EngineFilterAirQ, s.EngineFilterInteriorAirWeight), TextColor, 0.40f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "hull: near={0:0}Hz far={1:0}Hz range={2:0}m curve={3:0.00} q={4:0.00}", s.EngineFilterHullNearFrequency, s.EngineFilterHullFarFrequency, s.EngineFilterHullRange, s.EngineFilterHullDistanceCurve, s.EngineFilterHullQ), TextColor, 0.40f);
                line = DrawMultiline(x, y, row, line, V2EngineFilterTelemetry.FormatEnvironment(), TextColor, 0.40f, 2);
                line = DrawMultiline(x, y, row, line, V2EngineFilterTelemetry.FormatEmitters(4), DimColor, 0.36f, 5);
                line++;

                Draw(x, y + line++ * row, "PLAYER / AUX FILTER", HeaderColor, 0.50f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "routes: master={0} env={1} block={2} local={3}", s.PlayerFilterEnabled ? "on" : "off", s.PlayerFilterEnvironmentEnabled ? "on" : "off", s.PlayerFilterBlockEnabled ? "on" : "off", s.PlayerFilterLocalEnabled ? "on" : "off"), TextColor, 0.43f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "aux atm override: {0}", s.PlayerFilterAtmosphereOverrideEnabled ? s.PlayerFilterAtmosphereOverride.ToString("0.00", CultureInfo.InvariantCulture) : "real"), TextColor, 0.43f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "aux shape: {0} clear={1:0}Hz muffled={2:0}Hz q={3:0.00}", s.Filter2Type, s.Filter2Frequency, s.PlayerFilterMuffledFrequency, s.Filter2Q), TextColor, 0.43f);
                Draw(x, y + line++ * row, string.Format(CultureInfo.InvariantCulture, "block distance: fallback={0:0}m scale={1:0.00} curve={2:0.00}", s.PlayerFilterBlockRange, s.PlayerFilterBlockRangeScale, s.PlayerFilterBlockDistanceCurve), TextColor, 0.43f);
                line = DrawMultiline(x, y, row, line, V2BlockRangeScaler.FormatStatus(), TextColor, 0.38f, 2);
                line = DrawMultiline(x, y, row, line, V2PlayerEnvironmentTelemetry.FormatSummary(), TextColor, 0.38f, 2);
                line = DrawMultiline(x, y, row, line, V2PlayerEnvironmentTelemetry.FormatReverbDebugSummary(), TextColor, 0.38f, 2);
                line = DrawMultiline(x, y, row, line, V2PlayerFilterRuntime.FormatSummary(), TextColor, 0.40f, 2);
                line = DrawMultiline(x, y, row, line, V2PlayerFilterRuntime.FormatSources(6), DimColor, 0.36f, 7);
                line++;

                Draw(x, y + line++ * row, "AUX SOURCE CANDIDATES", HeaderColor, 0.50f);
                line = DrawMultiline(x, y, row, line, V2AuxSourceOcclusionTelemetry.FormatSummary(), TextColor, 0.38f, 2);
                line = DrawMultiline(x, y, row, line, V2AuxSourceOcclusionTelemetry.FormatSources(5), DimColor, 0.36f, 6);
                line++;

                Draw(x, y + line++ * row, "SESSION VOICE CATALOG", HeaderColor, 0.50f);
                line = DrawMultiline(x, y, row, line, AudioVoiceCatalog.FormatSummary(), TextColor, 0.38f, 2);
                DrawMultiline(x, y, row, line, AudioVoiceCatalog.FormatCandidates(8), DimColor, 0.36f, 9);
            }
            catch (Exception ex)
            {
                SetEnabled(false);
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling filter debug overlay after error: " + ex);
            }
        }

        private static int DrawMultiline(float x, float y, float row, int line, string text, Color color, float scale, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text))
                return line;

            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            int count = Math.Min(maxLines, lines.Length);
            for (int i = 0; i < count; i++)
                Draw(x, y + line++ * row, lines[i], color, scale);

            return line;
        }

        private static void Draw(float x, float y, string text, Color color, float scale)
        {
            MyRenderProxy.DebugDrawText2D(
                new Vector2(x, y),
                text ?? string.Empty,
                color,
                scale,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                false);
        }

        private static Vector2 GetViewportSize()
        {
            Vector2 viewportSize = Vector2.Zero;
            if (MyAPIGateway.Session?.Camera != null)
                viewportSize = MyAPIGateway.Session.Camera.ViewportSize;

            if (viewportSize.X < 100f || viewportSize.Y < 100f)
                viewportSize = new Vector2(1920f, 1080f);

            return viewportSize;
        }
    }
}
