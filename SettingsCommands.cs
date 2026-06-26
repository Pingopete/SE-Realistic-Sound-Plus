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
            V2DebugLog.WriteEvent("command", "chat " + (string.IsNullOrWhiteSpace(commandText) ? "show" : commandText));

            try
            {
                switch (command)
                {
                    case "help":
                    case "?":
                        Notify("/rsp menu | /rsp show | /rsp sounds | /rsp filters | /rsp catalog | /rsp reverbdiag | /rsp reverbroute world|global|managed | /rsp detail on | /rsp idle off | /rsp state on | /rsp detailgain 2 | /rsp cmdsmooth 2000 | /rsp auxsmooth 1000 | /rsp reverb on | /rsp playerenvray 120 | /rsp save");
                        break;
                    case "show":
                        Notify(SettingsManager.Summary());
                        break;
                    case "menu":
                    case "ui":
                        RspSettingsMenu.Toggle();
                        Notify("Settings menu " + (RspSettingsMenu.IsOpen ? "open" : "closed") + ".");
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
                    case "externalfilter":
                    case "extfilter":
                        SetFilter(parts);
                        break;
                    case "internalfilter":
                    case "intfilter":
                    case "insidefilter":
                        SetInternalFilter(parts);
                        break;
                    case "filter1type":
                    case "filter1mode":
                    case "f1type":
                    case "f1mode":
                    case "enginefiltertype":
                    case "enginefiltermode":
                    case "engfiltertype":
                    case "efiltertype":
                        SetCustomFilterType(parts, true);
                        break;
                    case "filter2type":
                    case "filter2mode":
                    case "f2type":
                    case "f2mode":
                    case "auxfiltertype":
                    case "auxfiltermode":
                    case "afiltertype":
                        SetCustomFilterType(parts, false);
                        break;
                    case "enginefilterdynamic":
                    case "enginedynamic":
                    case "dynamicfilter":
                        SetEngineFilterDynamic(parts);
                        break;
                    case "atmoverride":
                    case "atmosphereoverride":
                    case "externalatmoverride":
                        SetAtmosphereOverride(parts);
                        break;
                    case "playerfilter":
                    case "auxfilterroute":
                        SetPlayerFilter(parts);
                        break;
                    case "envfilter":
                    case "environmentfilter":
                    case "windfilter":
                        SetPlayerFilterEnvironment(parts);
                        break;
                    case "blockfilter":
                    case "auxblockfilter":
                    case "machinefilter":
                        SetPlayerFilterBlock(parts);
                        break;
                    case "localfilter":
                    case "playerlocalfilter":
                    case "playersoundfilter":
                        SetPlayerFilterLocal(parts);
                        break;
                    case "auxpathdebug":
                    case "pathdebug":
                    case "occlusiondebug":
                    case "blockpathdebug":
                        SetPlayerFilterPathDebug(parts);
                        break;
                    case "reverbraydebug":
                    case "roomraydebug":
                    case "envreverbraydebug":
                        SetReverbRayDebug(parts);
                        break;
                    case "envmapdebug":
                    case "envcelldebug":
                        SetEnvMapDebug(parts);
                        break;
                    case "auxatmoverride":
                    case "auxpressureoverride":
                    case "playerfilteratmoverride":
                    case "auxatm":
                    case "auxpressure":
                    case "auxvacuum":
                        SetPlayerFilterAtmosphereOverride(parts);
                        break;
                    case "detail":
                    case "enginedetail":
                        SetV2Detail(parts);
                        break;
                    case "idle":
                    case "detailidle":
                        SetV2DetailIdle(parts);
                        break;
                    case "state":
                    case "enginestate":
                    case "statemachine":
                        SetV2State(parts);
                        break;
                    case "detail2dpos":
                    case "detail2dposition":
                    case "detailpositional2d":
                        SetV2Detail2DPositionalTest(parts);
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
                    case "filters":
                    case "filteroverlay":
                    case "controllers":
                        ToggleFilterOverlay(parts);
                        break;
                    case "catalog":
                    case "soundcatalog":
                    case "voicecatalog":
                        PrintVoiceCatalog();
                        break;
                    case "log":
                    case "debuglog":
                        ToggleDebugLog(parts);
                        break;
                    case "logpath":
                        Notify(V2DebugLog.Path);
                        break;
                    case "reverb":
                    case "globalreverb":
                    case "reverbtest":
                        SetGlobalReverb(parts);
                        break;
                    case "reverbroute":
                    case "reverbmode":
                    case "reverbbus":
                        SetGlobalReverbRoute(parts);
                        break;
                    case "reverbvoices":
                    case "reverbsounds":
                    case "reverbaffected":
                        PrintReverbVoices();
                        break;
                    case "reverbdiag":
                    case "reverbdiagnostic":
                    case "reverbdiagnostics":
                        PrintReverbDiagnostics();
                        break;
                    case "reverbping":
                    case "reverbtestping":
                    case "reverbimpulse":
                    case "dspping":
                    case "dspreverbping":
                        PlayReverbPing();
                        break;
                    case "reverbpreset":
                    case "reverbpresetping":
                    case "reverbnativepreset":
                        PlayReverbPreset(parts);
                        break;
                    case "reverbxapo":
                    case "reverbsimple":
                    case "reverbsimplexapo":
                        PlayReverbXapo();
                        break;
                    case "reverbcue":
                    case "reverbgamecue":
                    case "reverbwetcue":
                    case "dspcue":
                    case "dspreverbcue":
                        PlayReverbCue(parts);
                        break;
                    case "dspdiag":
                    case "dspreverbdiag":
                    case "reverbcuediag":
                        PrintDspCueDiagnostics(parts);
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

        private static void ToggleFilterOverlay(string[] parts)
        {
            if (parts.Length >= 2)
            {
                string value = parts[1].ToLowerInvariant();
                if (value == "on" || value == "1" || value == "true")
                    FilterDebugOverlay.SetEnabled(true);
                else if (value == "off" || value == "0" || value == "false")
                    FilterDebugOverlay.SetEnabled(false);
                else
                {
                    Notify("Usage: /rsp filters [on|off]");
                    return;
                }
            }
            else
            {
                FilterDebugOverlay.Toggle();
            }

            Notify("Filter controller overlay " + (FilterDebugOverlay.Enabled ? "on" : "off") + ".");
        }

        private static void PrintVoiceCatalog()
        {
            string summary = AudioVoiceCatalog.FormatSummary();
            string candidates = AudioVoiceCatalog.FormatCandidates(12).Replace(Environment.NewLine, " | ");
            string top = AudioVoiceCatalog.FormatTop(12).Replace(Environment.NewLine, " | ");
            V2DebugLog.WriteEvent("voice-catalog-command", summary + " | candidates: " + candidates + " | top: " + top);
            Notify(summary + " | " + candidates);
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

        private static void SetGlobalReverb(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp reverb <on|off>");
                return;
            }

            if (!SettingsManager.TrySetGlobalReverb(parts[1]))
            {
                Notify("Usage: /rsp reverb <on|off>");
                return;
            }

            V2DebugLog.WriteEvent("command", "global reverb " + (SettingsManager.Current.GlobalReverbEnabled ? "on" : "off"));
            Notify(SettingsManager.Summary());
        }

        private static void SetGlobalReverbRoute(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp reverbroute <world|global|managed|globalbus|custombus>");
                return;
            }

            if (!SettingsManager.TrySetGlobalReverbRoute(parts[1]))
            {
                Notify("Usage: /rsp reverbroute <world|global|managed|globalbus|custombus>");
                return;
            }

            SettingsManager.Current.GlobalReverbEnabled = true;
            V2DebugLog.WriteEvent("command", "global reverb route " + SettingsManager.Current.GlobalReverbRoute);
            Notify("Reverb route " + SettingsManager.Current.GlobalReverbRoute + ". " + GetReverbStatus());
        }

        private static void PrintReverbVoices()
        {
            string status = GetReverbStatus();
            V2DebugLog.WriteEvent("reverb-status", status);
            Notify(status);
        }

        private static string GetReverbStatus()
        {
            if (SettingsManager.IsGlobalReverbGlobalBusRoute(SettingsManager.Current))
                return V2GlobalReverbRuntime.FormatStatus() + " | " + V2ManagedDspReverbRuntime.FormatStatus();

            return V2ManagedDspReverbRuntime.FormatStatus();
        }

        private static void PrintReverbDiagnostics()
        {
            SettingsManager.Current.GlobalReverbEnabled = true;
            PrintReverbVoices();
            Notify(V2ManagedDspReverbRuntime.LastStatus);
        }

        private static void PlayReverbPing()
        {
            SettingsManager.Current.GlobalReverbEnabled = true;
            string status = V2ManagedDspReverbRuntime.PlayImpulse();
            Notify(status);
        }

        private static void PlayReverbPreset(string[] parts)
        {
            string status = "reverbPreset=legacy-disabled: managed DSP route is active; use /rsp reverbping or /rsp reverbcue [cue]";
            V2DebugLog.WriteEvent("dsp-reverb-preset", status);
            Notify(status);
        }

        private static void PlayReverbXapo()
        {
            string status = V2ReverbDiagnosticPing.PlayXapo();
            Notify(status);
        }

        private static void PlayReverbCue(string[] parts)
        {
            SettingsManager.Current.GlobalReverbEnabled = true;
            string cueName = parts.Length >= 2 ? parts[1] : "ArcPlayStepsMetal";
            string status = V2ManagedDspReverbRuntime.PlayCue(cueName);
            Notify(status);
        }

        private static void PrintDspCueDiagnostics(string[] parts)
        {
            string cueName = parts.Length >= 2 ? parts[1] : "ArcPlayStepsMetal";
            string status = V2ManagedDspReverbRuntime.DiagnoseCue(cueName);
            Notify(status);
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

        private static void SetV2DetailIdle(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp idle <on|off>");
                return;
            }

            if (!SettingsManager.TrySetV2DetailIdle(parts[1]))
            {
                Notify("Usage: /rsp idle <on|off>");
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

        private static void SetV2Detail2DPositionalTest(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp detail2dpos <on|off>");
                return;
            }

            if (!SettingsManager.TrySetV2Detail2DPositionalTest(parts[1]))
            {
                Notify("Usage: /rsp detail2dpos <on|off>");
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

        private static void SetInternalFilter(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp internalfilter <" + SettingsManager.FilterOptions + ">");
                return;
            }

            if (!SettingsManager.TrySetInternalFilter(parts[1]))
            {
                Notify("Unknown internal filter. Options: " + SettingsManager.FilterOptions);
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetCustomFilterType(string[] parts, bool filter1)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp " + (filter1 ? "enginefiltertype" : "auxfiltertype") + " <" + SettingsManager.CustomFilterTypeOptions + ">");
                return;
            }

            bool ok = filter1
                ? SettingsManager.TrySetFilter1Type(parts[1])
                : SettingsManager.TrySetFilter2Type(parts[1]);
            if (!ok)
            {
                Notify("Unknown custom filter type. Options: " + SettingsManager.CustomFilterTypeOptions);
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetEngineFilterDynamic(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp enginefilterdynamic <on|off>");
                return;
            }

            if (!SettingsManager.TrySetEngineFilterDynamic(parts[1]))
            {
                Notify("Usage: /rsp enginefilterdynamic <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetAtmosphereOverride(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp atmoverride <on|off> or /rsp externalatm <0..1>");
                return;
            }

            if (!SettingsManager.TrySetAtmosphereOverrideEnabled(parts[1]))
            {
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    Notify("Usage: /rsp atmoverride <on|off> or /rsp atmoverride <0..1>");
                    return;
                }

                SettingsManager.Current.V2AtmosphereOverrideEnabled = true;
                SettingsManager.TrySet("externalatm", value);
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetPlayerFilter(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetPlayerFilter(parts[1]))
            {
                Notify("Usage: /rsp playerfilter <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetPlayerFilterEnvironment(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetPlayerFilterEnvironment(parts[1]))
            {
                Notify("Usage: /rsp envfilter <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetPlayerFilterBlock(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetPlayerFilterBlock(parts[1]))
            {
                Notify("Usage: /rsp blockfilter <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetPlayerFilterLocal(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetPlayerFilterLocal(parts[1]))
            {
                Notify("Usage: /rsp localfilter <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetPlayerFilterPathDebug(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetPlayerFilterPathDebug(parts[1]))
            {
                Notify("Usage: /rsp auxpathdebug <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetReverbRayDebug(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetReverbRayDebug(parts[1]))
            {
                Notify("Usage: /rsp reverbraydebug <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetEnvMapDebug(string[] parts)
        {
            if (parts.Length < 2 || !SettingsManager.TrySetEnvMapDebug(parts[1]))
            {
                Notify("Usage: /rsp envmapdebug <on|off>");
                return;
            }

            Notify(SettingsManager.Summary());
        }

        private static void SetPlayerFilterAtmosphereOverride(string[] parts)
        {
            if (parts.Length < 2)
            {
                Notify("Usage: /rsp auxatmoverride <on|off> or /rsp auxatm <0..1>");
                return;
            }

            if (!SettingsManager.TrySetPlayerFilterAtmosphereOverride(parts[1]))
            {
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    Notify("Usage: /rsp auxatmoverride <on|off> or /rsp auxatmoverride <0..1>");
                    return;
                }

                SettingsManager.Current.PlayerFilterAtmosphereOverrideEnabled = true;
                SettingsManager.TrySet("auxatm", value);
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
