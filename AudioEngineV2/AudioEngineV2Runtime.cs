using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class AudioEngineV2Runtime
    {
        private static bool _loggedEnabled;
        private static V2AudioListenerState _listener;

        public static V2AudioListenerState Listener => _listener;

        public static void ResetForSession(string reason)
        {
            _loggedEnabled = false;
            _listener = default(V2AudioListenerState);
            VanillaShipEnvironment.Reset();
            V2AudioDebugState.Reset();

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] V2 audio runtime reset: " + reason);
        }

        public static void Update()
        {
            _listener = V2AudioListenerState.Capture();
            V2AudioDebugState.Update(_listener);

            if (!SettingsManager.Current.AudioEngineV2Enabled)
                return;

            if (!_loggedEnabled)
            {
                _loggedEnabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Audio Engine V2 is enabled. Emitter routes are scaffolded only; no replacement sounds are spawned yet.");
            }
        }

        public static string FormatDebugLine()
        {
            return V2AudioDebugState.Format();
        }

        public static void DrawDebugMarkers()
        {
            // Source marker drawing lands with the first live emitter model.
        }
    }
}
