using System;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using RealisticSoundPlus.Patches;
using Sandbox.ModAPI;
using VRage.Plugins;
using VRage.Utils;

namespace RealisticSoundPlus
{
    public sealed class Plugin : IPlugin, IDisposable
    {
        public const string HarmonyId = "pete.realistic-sound-plus";

        private Harmony _harmony;
        private bool _disposed;
        private int _settingsPollFrame;
        private object _lastSession;

        public void Init(object gameInstance)
        {
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Loading realistic-audio correction plugin.");

            SettingsManager.LoadOrCreate();
            AudioPatchRuntime.ResetForSession("plugin init");

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Loaded. Baseline mode: preserve vanilla realistic audio and vacuum silence.");
        }

        public void Update()
        {
            ResetAudioRuntimeIfSessionChanged();
            SettingsCommands.TryRegister();
            AudioEngineV2Runtime.Update();
            V2DebugLog.Update();
            AudioDebugOverlay.Draw();

            if (++_settingsPollFrame >= 300)
            {
                _settingsPollFrame = 0;
                SettingsManager.ReloadIfChanged();
            }
        }

        private void ResetAudioRuntimeIfSessionChanged()
        {
            object session = MyAPIGateway.Session;
            if (ReferenceEquals(session, _lastSession))
                return;

            _lastSession = session;
            AudioPatchRuntime.ResetForSession(session == null ? "session cleared" : "session started");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                SettingsCommands.Unregister();
                _harmony?.UnpatchAll(HarmonyId);
                AudioPatchRuntime.ResetForSession("plugin dispose");
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Unloaded.");
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[RealisticSoundPlus] Error during unload: " + ex);
            }
        }
    }
}
