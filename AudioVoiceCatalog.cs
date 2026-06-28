using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RealisticSoundPlus.AudioEngineV2;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Audio;

namespace RealisticSoundPlus
{
    internal static class AudioVoiceCatalog
    {
        // Re-records every live voice (and thus re-issues V2BlockEmitterReposition.Request for every block
        // emitter) on this cadence - NOT every frame. V2BlockEmitterReposition derives its target-hold windows
        // from this value, so they always exceed the rate at which fresh targets actually arrive.
        internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastPollUtc = DateTime.MinValue;
        private static DateTime _lastLogUtc = DateTime.MinValue;

        public static void ResetRuntimeState()
        {
            Entries.Clear();
            _lastPollUtc = DateTime.MinValue;
            _lastLogUtc = DateTime.MinValue;
        }

        public static void Update()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastPollUtc < PollInterval || MyAudio.Static == null)
                return;

            _lastPollUtc = now;
            MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
            RecordList("S", played.Sound, now);
            RecordList("M", played.Music, now);
            RecordList("H", played.Hud, now);
            LogIfDue(now);
        }

        public static string FormatSummary()
        {
            int env = 0;
            int block = 0;
            int local = 0;
            int engine = 0;
            int physical = 0;

            foreach (Entry entry in Entries.Values)
            {
                if (entry.Physical)
                    physical++;

                switch (entry.Category)
                {
                    case "env": env++; break;
                    case "block": block++; break;
                    case "local": local++; break;
                    case "engine": engine++; break;
                }
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "unique={0} env={1} block={2} local={3} engine={4} physical={5}",
                Entries.Count,
                env,
                block,
                local,
                engine,
                physical);
        }

        public static string FormatTop(int maxLines)
        {
            if (Entries.Count == 0)
                return "No cataloged voices yet.";

            List<Entry> sorted = new List<Entry>(Entries.Values);
            sorted.Sort((left, right) => right.MaxScore.CompareTo(left.MaxScore));

            StringBuilder builder = new StringBuilder();
            builder.Append("kind cat    seen max  cue");
            int count = Math.Min(maxLines, sorted.Count);
            for (int i = 0; i < count; i++)
            {
                Entry entry = sorted[i];
                builder.AppendLine();
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0,-4} {1,-6} {2,4} {3:0.00} {4}",
                    entry.Kind,
                    entry.Category,
                    entry.SeenCount,
                    entry.MaxScore,
                    Trim(entry.CueName, 34));
            }

            return builder.ToString();
        }

        public static string FormatCandidates(int maxLines)
        {
            if (Entries.Count == 0)
                return "No cataloged candidate voices yet.";

            List<Entry> sorted = new List<Entry>();
            foreach (Entry entry in Entries.Values)
            {
                if (entry.Category == "unknown" || entry.Category == "ui")
                    continue;

                sorted.Add(entry);
            }

            if (sorted.Count == 0)
                return "No classified candidate voices yet.";

            sorted.Sort((left, right) => right.MaxScore.CompareTo(left.MaxScore));
            StringBuilder builder = new StringBuilder();
            builder.Append("cat    phys kind max  cue");
            int count = Math.Min(maxLines, sorted.Count);
            for (int i = 0; i < count; i++)
            {
                Entry entry = sorted[i];
                builder.AppendLine();
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0,-6} {1,-4} {2,-4} {3:0.00} {4}",
                    entry.Category,
                    entry.Physical ? "Y" : "N",
                    entry.Kind,
                    entry.MaxScore,
                    Trim(entry.CueName, 38));
            }

            return builder.ToString();
        }

        private static void RecordList(string kind, List<IMySourceVoice> voices, DateTime now)
        {
            if (voices == null)
                return;

            for (int i = 0; i < voices.Count; i++)
                Record(kind, voices[i], now);
        }

        private static void Record(string kind, IMySourceVoice voice, DateTime now)
        {
            if (voice == null || !voice.IsValid || !voice.IsPlaying)
                return;
            if (V2ReverbDiagnosticPing.IsOwnedWetVoice(voice))
                return;

            string cueName = voice.CueEnum.ToString();
            if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty")
                return;

            float score = Math.Max(0f, voice.Volume * voice.VolumeMultiplier);
            V2AuxSourceOcclusionTelemetry.RecordVoice(kind, cueName, voice, score);
            string key = kind + ":" + cueName;
            if (!Entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry
                {
                    Kind = kind,
                    CueName = cueName,
                    Category = Classify(kind, cueName, voice, out bool physical),
                    Physical = physical,
                    FirstSeenUtc = now
                };
            }

            entry.SeenCount++;
            entry.LastSeenUtc = now;
            entry.MaxScore = Math.Max(entry.MaxScore, score);
            Entries[key] = entry;
        }

        private static string Classify(string kind, string cueName, IMySourceVoice voice, out bool physical)
        {
            physical = RspDynamicAudioFilters.TryResolveEmitter(voice, out MyEntity3DSoundEmitter emitter) && emitter != null;
            if (V2AuxCueClassifier.IsNonWorldCue(cueName))
                return "ui";

            if (V2AuxCueClassifier.IsEngineCue(cueName))
                return "engine";

            if (V2AuxCueClassifier.IsControllableActionCue(cueName))
                return physical ? "block" : "local";

            if (V2AuxCueClassifier.IsPlayerLocalCue(cueName))
                return "local";

            if (V2AuxCueClassifier.IsKnownBlockCue(cueName))
                return "block";

            if (V2AuxCueClassifier.IsEnvironmentCue(cueName))
                return "env";

            if (string.Equals(kind, "S", StringComparison.OrdinalIgnoreCase) && physical)
                return "block";

            return "unknown";
        }

        private static void LogIfDue(DateTime now)
        {
            if (!SettingsManager.Current.V2DebugLogEnabled || now - _lastLogUtc < LogInterval)
                return;

            _lastLogUtc = now;
            V2DebugLog.WriteEvent("voice-catalog", FormatSummary() + " | candidates: " + FormatCandidates(16).Replace(Environment.NewLine, "; ") + " | top: " + FormatTop(24).Replace(Environment.NewLine, "; "));
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        }

        private struct Entry
        {
            public string Kind;
            public string CueName;
            public string Category;
            public bool Physical;
            public int SeenCount;
            public float MaxScore;
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
        }
    }
}
