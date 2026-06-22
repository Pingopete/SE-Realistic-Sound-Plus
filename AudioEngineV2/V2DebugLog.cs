using System;
using System.Globalization;
using System.IO;
using RealisticSoundPlus.Patches;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2DebugLog
    {
        private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(1);
        private const long MaxLogBytes = 2 * 1024 * 1024;

        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static bool _disabled;

        public static string Path => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SettingsManager.ConfigPath), "RealisticSoundPlus-v2-debug.log");

        public static void ResetForSession(string reason)
        {
            _lastWriteUtc = DateTime.MinValue;
            WriteEvent("session", reason);
        }

        public static void Update()
        {
            if (_disabled || !SettingsManager.Current.V2DebugLogEnabled)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastWriteUtc < WriteInterval)
                return;

            _lastWriteUtc = now;
            WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:o} | {1} | {2} | engineFilter={3} | playerEnv={4} | playerFilter={5} | envAmbience={6} | {7}",
                now,
                AudioDiagnostics.FormatGlobal(),
                AudioEngineV2Runtime.FormatDebugLine(),
                V2EngineFilterTelemetry.FormatSummary(),
                V2PlayerEnvironmentTelemetry.FormatSummary(),
                V2PlayerFilterRuntime.FormatSummary(),
                EnvironmentAmbiencePatch.FormatSummary(),
                SettingsManager.Summary()));
        }

        public static void WriteEvent(string kind, string message)
        {
            if (_disabled || !SettingsManager.Current.V2DebugLogEnabled)
                return;

            WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:o} | event={1} | {2}",
                DateTime.UtcNow,
                kind ?? "unknown",
                message ?? string.Empty));
        }

        private static void WriteLine(string line)
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                RotateIfNeeded();
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling V2 debug log after error: " + ex);
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(Path))
                return;

            FileInfo info = new FileInfo(Path);
            if (info.Length < MaxLogBytes)
                return;

            string oldPath = Path + ".old";
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(Path, oldPath);
        }
    }
}
