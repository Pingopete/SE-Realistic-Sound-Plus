using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticSoundPlus.AudioEngineV2;
using RealisticSoundPlus.Patches;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace RealisticSoundPlus
{
    internal static class AudioDebugOverlay
    {
        private const int MaxRows = 22;
        private static readonly Color HeaderColor = new Color(120, 220, 255, 255);
        private static readonly Color TextColor = new Color(230, 235, 240, 255);
        private static readonly Color QuietColor = new Color(170, 180, 190, 255);
        private static DateTime _lastVoiceLogUtc = DateTime.MinValue;

        public static bool Enabled => SettingsManager.Current.AudioOverlayEnabled;

        public static void Toggle()
        {
            SetEnabled(!Enabled);
        }

        public static void SetEnabled(bool enabled)
        {
            SettingsManager.Current.AudioOverlayEnabled = enabled;
        }

        public static void Draw()
        {
            if (!Enabled || MyAudio.Static == null)
                return;

            try
            {
                MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
                List<Row> rows = new List<Row>();
                AddRows(rows, "S", played.Sound);
                AddRows(rows, "M", played.Music);
                AddRows(rows, "H", played.Hud);

                rows.Sort((left, right) => right.Score.CompareTo(left.Score));
                V2AuxSourceOcclusionTelemetry.LogIfDue();
                LogTopRows(rows);

                int shown = Math.Min(rows.Count, MaxRows);
                Vector2 viewportSize = GetViewportSize();
                float centerX = viewportSize.X * 0.5f;
                float statusX = Math.Max(24f, centerX - 470f);
                float rowHeight = 22f;
                float startY = Math.Max(55f, viewportSize.Y * 0.075f);

                int rowIndex = 0;
                DrawText(statusX, startY + rowIndex++ * rowHeight, "Realistic Sound+ audio debug  |  /rsp sounds off  |  /rsp filters", HeaderColor, 0.66f, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
                string[] globalLines = AudioDiagnostics.FormatGlobalLines();
                for (int i = 0; i < globalLines.Length; i++)
                    DrawText(statusX, startY + rowIndex++ * rowHeight, globalLines[i], HeaderColor, 0.48f, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
                string[] v2Lines = V2AudioDebugState.FormatCompactLines();
                for (int i = 0; i < v2Lines.Length; i++)
                    DrawText(statusX, startY + rowIndex++ * rowHeight, v2Lines[i], HeaderColor, 0.44f, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);

                rowIndex++;
                DrawLine(rowIndex++, "type  eng  count  volume  cue  | route tr sc base fin d p", HeaderColor, 0.50f, centerX, startY, rowHeight);
                AudioEngineV2Runtime.DrawDebugMarkers();

                if (rows.Count == 0)
                {
                    DrawLine(rowIndex + 1, "No currently playing source voices reported.", QuietColor, 0.56f, centerX, startY, rowHeight);
                    return;
                }

                for (int i = 0; i < shown; i++)
                {
                    Row row = rows[i];
                    string diagnostic = AudioDiagnostics.TryGetCueSnapshot(row.CueName, out AudioDiagnostics.CueSnapshot snapshot)
                        ? AudioDiagnostics.FormatCue(snapshot)
                        : (row.EngineCandidate ? " UNCONTROLLED" : string.Empty);
                    string text = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}    {1}   x{2,2}   {3:0.00}   {4}{5}",
                        row.Kind,
                        row.EngineCandidate ? "*" : "-",
                        row.Count,
                        row.Score,
                        row.CueName,
                        diagnostic);
                    DrawLine(rowIndex + i, text, row.Score > 0.05f ? TextColor : QuietColor, 0.46f, centerX, startY, rowHeight);
                }

                if (rows.Count > shown)
                    DrawLine(rowIndex + shown + 1, "+ " + (rows.Count - shown).ToString(CultureInfo.InvariantCulture) + " more", QuietColor, 0.5f, centerX, startY, rowHeight);
            }
            catch (Exception ex)
            {
                SetEnabled(false);
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling audio debug overlay after error: " + ex);
            }
        }

        private static void AddRows(List<Row> rows, string kind, List<IMySourceVoice> voices)
        {
            if (voices == null || voices.Count == 0)
                return;

            Dictionary<string, Row> byCue = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (IMySourceVoice voice in voices)
            {
                if (voice == null || !voice.IsValid || !voice.IsPlaying)
                    continue;

                string cueName = voice.CueEnum.ToString();
                if (string.IsNullOrWhiteSpace(cueName))
                    cueName = "<unknown>";

                string key = kind + ":" + cueName;
                if (!byCue.TryGetValue(key, out Row row))
                {
                    row = new Row
                    {
                        Kind = kind,
                        CueName = cueName,
                        EngineCandidate = EngineAudioClassifier.IsKnownEngineCue(cueName)
                    };
                    byCue[key] = row;
                }

                row.Count++;
                float score = Math.Max(0f, voice.Volume * voice.VolumeMultiplier);
                row.Score += score;
                V2AuxSourceOcclusionTelemetry.RecordVoice(kind, cueName, voice, score);
            }

            foreach (Row row in byCue.Values)
            {
                if (row.Count > 0)
                    row.Score /= row.Count;
                rows.Add(row);
            }
        }

        private static void LogTopRows(List<Row> rows)
        {
            if (!SettingsManager.Current.V2DebugLogEnabled || rows == null)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastVoiceLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastVoiceLogUtc = now;
            int count = Math.Min(rows.Count, 12);
            if (count <= 0)
            {
                V2DebugLog.WriteEvent("voices", "none");
                return;
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                Row row = rows[i];
                if (i > 0)
                    builder.Append("; ");
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0}{1} x{2} {3:0.00} {4}",
                    row.Kind,
                    row.EngineCandidate ? "*" : "-",
                    row.Count,
                    row.Score,
                    row.CueName);
            }

            V2DebugLog.WriteEvent("voices", builder.ToString());
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

        private static void DrawLine(int row, string text, Color color, float scale, float centerX, float startY, float rowHeight)
        {
            DrawText(centerX, startY + row * rowHeight, text, color, scale, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP);
        }

        internal static void DrawText(float x, float y, string text, Color color, float scale, MyGuiDrawAlignEnum align)
        {
            MyRenderProxy.DebugDrawText2D(new Vector2(x, y), text, color, scale, align, false);
        }

        private sealed class Row
        {
            public string Kind;
            public string CueName;
            public int Count;
            public float Score;
            public bool EngineCandidate;
        }
    }
}
