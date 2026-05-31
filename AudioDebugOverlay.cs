using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const int MaxRows = 30;
        private static readonly Color HeaderColor = new Color(120, 220, 255, 255);
        private static readonly Color TextColor = new Color(230, 235, 240, 255);
        private static readonly Color QuietColor = new Color(170, 180, 190, 255);

        public static bool Enabled { get; private set; }

        public static void Toggle()
        {
            Enabled = !Enabled;
        }

        public static void SetEnabled(bool enabled)
        {
            Enabled = enabled;
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

                int shown = Math.Min(rows.Count, MaxRows);
                Vector2 viewportSize = GetViewportSize();
                float centerX = viewportSize.X * 0.5f;
                float rowHeight = 22f;
                float startY = Math.Max(80f, viewportSize.Y * 0.12f);

                DrawLine(0, "Realistic Sound+ audio debug  |  /rsp sounds off", HeaderColor, 0.68f, centerX, startY, rowHeight);
                DrawLine(1, "type  eng  amb  count  volume  cue", HeaderColor, 0.58f, centerX, startY, rowHeight);

                if (rows.Count == 0)
                {
                    DrawLine(3, "No currently playing source voices reported.", QuietColor, 0.56f, centerX, startY, rowHeight);
                    return;
                }

                for (int i = 0; i < shown; i++)
                {
                    Row row = rows[i];
                    string text = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}    {1}    {2}   x{3,2}   {4:0.00}   {5}",
                        row.Kind,
                        row.EngineCandidate ? "*" : "-",
                        row.AmbientCandidate ? "*" : "-",
                        row.Count,
                        row.Score,
                        row.CueName);
                    DrawLine(i + 3, text, row.Score > 0.05f ? TextColor : QuietColor, 0.54f, centerX, startY, rowHeight);
                }

                if (rows.Count > shown)
                    DrawLine(shown + 4, "+ " + (rows.Count - shown).ToString(CultureInfo.InvariantCulture) + " more", QuietColor, 0.5f, centerX, startY, rowHeight);
            }
            catch (Exception ex)
            {
                Enabled = false;
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
                        EngineCandidate = EngineAudioClassifier.IsKnownEngineCue(cueName),
                        AmbientCandidate = EngineAudioClassifier.IsKnownAmbientCue(cueName)
                    };
                    byCue[key] = row;
                }

                row.Count++;
                row.Score += Math.Max(0f, voice.Volume * voice.VolumeMultiplier);
            }

            foreach (Row row in byCue.Values)
            {
                if (row.Count > 0)
                    row.Score /= row.Count;
                rows.Add(row);
            }
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
            MyRenderProxy.DebugDrawText2D(
                new Vector2(centerX, startY + row * rowHeight),
                text,
                color,
                scale,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP,
                false);
        }

        private sealed class Row
        {
            public string Kind;
            public string CueName;
            public int Count;
            public float Score;
            public bool EngineCandidate;
            public bool AmbientCandidate;
        }
    }
}