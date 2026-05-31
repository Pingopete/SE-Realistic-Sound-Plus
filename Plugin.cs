using System;
using HarmonyLib;
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

        public void Init(object gameInstance)
        {
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Loading realistic-audio correction plugin.");

            SettingsManager.LoadOrCreate();

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Loaded. Baseline mode: preserve vanilla realistic audio and vacuum silence.");
        }

        public void Update()
        {
            SettingsCommands.TryRegister();

            if (++_settingsPollFrame >= 300)
            {
                _settingsPollFrame = 0;
                SettingsManager.ReloadIfChanged();
            }
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
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Unloaded.");
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[RealisticSoundPlus] Error during unload: " + ex);
            }
        }
    }
}