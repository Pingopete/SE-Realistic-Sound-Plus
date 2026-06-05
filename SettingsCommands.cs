using System;
using System.Globalization;
using RealisticSoundPlus.AudioEngineV2;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace RealisticSoundPlus
{
    internal static class SettingsCommands
    {
        private const string Prefix = "/rsp";
        private static bool _registered;

        public static void TryRegister()
        {
            if (_registered || MyAPIGateway.Utilities == null)
                return;

            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;
            _registered = true;
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Chat commands registered. Use /rsp help.");
        }

        public static void Unregister()
        {
            if (!_registered || MyAPIGateway.Utilities == null)
                return;

            MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;
            _registered = false;
        }

        private static void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(messageText) || !messageText.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                return;

            sendToOthers = false;
            Handle(messageText.Substring(Prefix.Length).Trim());
        }

        private static void Handle(string commandText)
        {
            string[] parts = commandText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts.Length == 0 ? "show" : parts[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "help":
                    case "?":
                        Notify("/rsp show | /rsp detail on | /rsp state on | /rsp detailgain 2 | /rsp stategain 2 | /rsp dist 200 | /rsp distcurve 1 | /rsp state2dpos off | /rsp filter deep | /rsp sounds | /rsp log | /rsp logpath | /rsp save | /rsp reload");
                        break;
                    case "show":
                        Notify(SettingsManager.Summary());
                        break;
                    case "save":
                        SettingsManager.Save();
                        Notify("Saved. " + SettingsManager.Summary());
                        break;
                    case "reload":
                        SettingsManager.Load();
                        Notify("Reloaded. " + SettingsManager.Summary());
                        break;
                    case "filter":
                        SetFilter(parts);
                        break;
                    case "detail":
                    case "enginedetail":
                        SetV2Detail(parts);
                        break;
                    case "state":
                    case "enginestate":
                    case "statemachine":
                        SetV2State(parts);
                        break;
                    case "state2dpos":
                    case "state2dposition":
                    case "positional2d":
                        SetV2State2DPositionalTest(parts);
                        break;
                    case "sounds":
                    case "audio":
                        ToggleAudioOverlay(parts);
                        break;
                    case "log":
                    case "debuglog":
                        ToggleDebugLog(parts);
                        break;
                    case "logpath":
                        Notify(V2DebugLog.Path);
                        break;
                    default:
                        SetValue(command, parts);
                        break;
                }
            }
            catch (Exception ex)
            {
                Notify("Command failed: " + ex.Message);
                MyLog.Default.WriteLine("[RealisticSoundPlus] Command failed: " + ex);
            }
        }

        private static void ToggleAudioOverlay(string[] parts)
        {
            if (parts.Length >= 2)
            {
                string value = parts[1].ToLowerInvariant();
                if (value == "on" || value == "1" || value == "true")
                    AudioDebugOverlay.SetEnabled(true);
                else if (value == "off" || value == "0" || value == "false")
                    AudioDebugOverlay.SetEnabled(false);
                else
                {
                    Notify("Usage: /rsp sounds [on|off]");
                    return;
                }
            }
            else
            {
                AudioDebugOverlay.Toggle();
            }

            Notify("Audio debug overlay " + (AudioDebugOverlay.Enabled ? "on" : "off") + ".");
        }

        private static void ToggleDebugLog(string[] parts)
        {
            if (parts.Length >= 2)
            {
                if (!SettingsManager.TrySetV2DebugLog(parts[1]))
                {
                    Notify("Usage: /rsp log [on|off]");
                    return;
                }
            }
            else
            {
                SettingsManager.Current.V2DebugLogEnabled = !SettingsManager.Current.V2DebugLogEnabled;
            }

            V2DebugLog.WriteEvent("command", "debug log " + (SettingsManager.Current.V2DebugLogEnabled ? "on" : "off"));
            Notify("V2 debug log " + (SettingsManager.Current.V2DebugLogEnabled ? "on" : "off") + ": " + V2DebugLog.Path);
        }

        private static void SetV2Detail(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp detail <on|off>");
                return;
            }

            if (!SettingsManager.TrySetV2Detail(parts[1]))
            {
                Notify("Usage: /rsp detail <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetV2State(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp state <on|off>");
                return;
            }

            if (!SettingsManager.TrySetV2State(parts[1]))
            {
                Notify("Usage: /rsp state <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetV2State2DPositionalTest(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp state2dpos <on|off>");
                return;
            }

            if (!SettingsManager.TrySetV2State2DPositionalTest(parts[1]))
            {
                Notify("Usage: /rsp state2dpos <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetFilter(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp filter <" + SettingsManager.FilterOptions + ">");
                return;
            }

            if (!SettingsManager.TrySetFilter(parts[1]))
            {
                Notify("Unknown filter. Options: " + SettingsManager.FilterOptions);
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetValue(string name, string[] parts)
        {
            if (parts.Length < 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                Notify("Usage: /rsp " + name + " <number>");
                return;
            }

            if (!SettingsManager.TrySet(name, value))
            {
                Notify("Unknown setting: " + name + ". Use /rsp help.");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void Notify(string message)
        {
            if (MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.ShowMessage("Realistic Sound+", message);
        }
    }
}
