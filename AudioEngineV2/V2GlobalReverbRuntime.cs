using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using VRage.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2GlobalReverbRuntime
    {
        private static readonly BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ReverbEffectIndex = 0;
        private const int SourceReverbEffectIndex = 0;
        private const float MinLateFieldSeedDb = -40f;
        private const float GlobalBusWetVolumeScale = 0.35f;
        private const double GlobalBusFailureRetrySeconds = 5.0;
        private static readonly bool CompatGameSubmixRoutingEnabled = false;
        private static readonly bool SourceReverbVoiceRoutingEnabled = false;
        private static readonly bool GlobalBusDirectSourceRoutingEnabled = false;

        private static Type _audioType;
        private static Type _myAudioType;
        private static Type _submixVoiceType;
        private static Type _submixVoiceFlagsType;
        private static Type _voiceSendDescriptorType;
        private static Type _reverbParametersType;
        private static Type _sharpReverbType;
        private static Type _effectDescriptorType;
        private static PropertyInfo _applyReverbProperty;
        private static PropertyInfo _enableReverbProperty;
        private static PropertyInfo _currentOutputVoicesProperty;
        private static MethodInfo _setReverbParametersMethod;
        private static MethodInfo _setEffectParametersMethod;
        private static MethodInfo _getEffectParametersMethod;
        private static MethodInfo _isEffectEnabledMethod;
        private static FieldInfo _gameAudioVoiceField;
        private static FieldInfo _gameAudioVoiceDescField;
        private static FieldInfo _hudAudioVoiceField;
        private static FieldInfo _musicAudioVoiceField;
        private static FieldInfo _audioEngineField;
        private static FieldInfo _masterVoiceField;
        private static FieldInfo _reverbField;
        private static FieldInfo _reverbSetField;
        private static FieldInfo _applyReverbField;
        private static FieldInfo _enableReverbField;
        private static FieldInfo _maxSampleRateField;
        private static FieldInfo _voiceSendOutputVoiceField;
        private static readonly Dictionary<IMySourceVoice, SourceReverbState> SourceReverbStates = new Dictionary<IMySourceVoice, SourceReverbState>();
        private static readonly Dictionary<Type, FieldInfo> SourceVoiceDeviceFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, PropertyInfo> SourceVoiceDetailsProperties = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, MethodInfo> SourceSetEffectChainMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> SourceSetEffectParameterMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> SourceEnableEffectMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> SourceDisableEffectMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> SourceSetOutputVoicesMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<string, DateTime> SourceReverbFailedSignatures = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, DateTime> SourceReverbRouteFailedSignatures = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, SourceReverbBusState> SourceReverbBuses = new Dictionary<string, SourceReverbBusState>();
        private static readonly Dictionary<IMySourceVoice, GlobalBusSourceRouteState> GlobalBusSourceRoutes = new Dictionary<IMySourceVoice, GlobalBusSourceRouteState>();
        private static bool _resolved;
        private static bool _captureAttempted;
        private static bool _capturedApplyReverb;
        private static bool _capturedEnableReverb;
        private static bool _capturedReverbParameters;
        private static bool _wrapperParametersNoOp;
        private static bool _baseApplyReverb;
        private static bool _baseEnableReverb;
        private static bool _lastEnabled;
        private static bool _lastAppliedEnabled;
        private static bool _loggedUnavailable;
        private static bool _compatSubmixInstalled;
        private static bool _sourceVoiceFallbackActive;
        private static bool _directReverbInvalidForSession;
        private static bool _sourceReverbUnavailableForSession;
        private static float _lastDiffusion = -1f;
        private static float _lastRoomSize = -1f;
        private static string _lastReverbSignature = string.Empty;
        private static object _baseReverbParameters;
        private static object _baseGameAudioVoice;
        private static object _baseGameAudioVoiceDesc;
        private static object _compatGameAudioVoice;
        private static object _compatGameAudioVoiceDesc;
        private static object _globalBusVoice;
        private static object _globalBusReverbEffect;
        private static object _globalBusOriginalOutputVoices;
        private static object _globalBusRoutedGameVoice;
        private static object _globalBusOriginalGameAudioVoiceDesc;
        private static object _globalBusPatchedGameAudioVoiceDesc;
        private static object _customInlineVoice;
        private static object _customInlineEffect;
        private static string _customInlineSignature = string.Empty;
        private static DateTime _lastCompatRerouteLogUtc = DateTime.MinValue;
        private static DateTime _lastGlobalBusLogUtc = DateTime.MinValue;
        private static string _lastStatus = "not initialized";
        private static string _lastParameterStatus = "direct=disabled-stability";
        private static string _lastEffectStatus = "effect=?";
        private static string _lastChainStatus = "chain=?";
        private static string _lastSampleRateStatus = "rate=?";
        private static string _lastObservedSummary = "voices=?";
        private static string _lastAffectedVoices = "No live game-audio voices observed yet.";
        private static string _lastSourceReverbSummary = "sourceReverb=0";
        private static string _lastGlobalBusStatus = "globalbus=off";
        private static string _lastGlobalBusSourceStatus = "srcRoute=0";
        private static string _globalBusSignature = string.Empty;
        private static string _globalBusRouteKey = string.Empty;
        private static string _globalBusFailureSignature = string.Empty;
        private static string _globalBusFailureStatus = string.Empty;
        private static DateTime _globalBusNextRetryUtc = DateTime.MinValue;
        private static DateTime _lastSourceReverbLogUtc = DateTime.MinValue;

        public static void Update()
        {
            object audio = MyAudio.Static;
            if (audio == null)
                return;

            Resolve(audio.GetType());
            CaptureBaseState(audio);

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (!settings.GlobalReverbEnabled)
            {
                ClearSourceReverbTargets("disabled");
                ClearSourceReverbBuses("disabled");
                if (_lastEnabled)
                    RestoreVanillaState("disabled");
                _lastEnabled = false;
                return;
            }

            if (!CanApply)
            {
                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    string status = "global reverb unavailable: " + FormatCapabilityStatus();
                    _lastStatus = status;
                    MyLog.Default.WriteLine("[RealisticSoundPlus] " + status);
                    V2DebugLog.WriteEvent("global-reverb", status);
                }

                _lastEnabled = true;
                return;
            }

            float diffusion = Clamp01(settings.GlobalReverbDiffusion);
            float roomSize = Clamp01(settings.GlobalReverbRoomSize);
            string reverbSignature = BuildSourceReverbSignature(settings);
            bool parameterChanged = !string.Equals(reverbSignature, _lastReverbSignature, StringComparison.Ordinal);
            bool enabling = !_lastEnabled || !_lastAppliedEnabled;

            try
            {
                if (!EnsureGameSubmixReverbChain(audio, out _lastChainStatus))
                {
                    bool sourceFallbackAvailable = _sourceVoiceFallbackActive && SourceReverbVoiceRoutingEnabled;
                    if (_sourceVoiceFallbackActive && !SourceReverbVoiceRoutingEnabled)
                    {
                        ClearSourceReverbTargets("route-disabled");
                        ClearSourceReverbBuses("route-disabled");
                        _lastSourceReverbSummary = "sourceReverb=route-disabled wet-bus-only";
                    }

                    _lastEnabled = true;
                    _lastAppliedEnabled = false;
                    _lastEffectStatus = _sourceVoiceFallbackActive
                        ? (sourceFallbackAvailable ? "effect=source-voices" : "effect=wet-bus-only")
                        : DescribeGameVoiceEffect(audio);
                    if (_sourceVoiceFallbackActive)
                        _lastParameterStatus = sourceFallbackAvailable ? "direct=source-voice" : "direct=wet-bus-only";
                    PurgeStaleSourceReverbTargets();
                    RefreshObservedVoices(audio);
                    _lastStatus = _sourceVoiceFallbackActive
                        ? string.Format(
                            CultureInfo.InvariantCulture,
                            "{2} diff={0:0.00} room={1:0.00}",
                            diffusion,
                            roomSize,
                            sourceFallbackAvailable ? "on source-xaudio" : "on wet-bus-only")
                        : string.Format(
                            CultureInfo.InvariantCulture,
                            "blocked real-xaudio diff={0:0.00} room={1:0.00}",
                            diffusion,
                            roomSize);
                    if (enabling || parameterChanged)
                        V2DebugLog.WriteEvent("global-reverb", _lastStatus + " " + _lastChainStatus + " " + _lastEffectStatus + " " + _lastSampleRateStatus);
                    _lastDiffusion = diffusion;
                    _lastRoomSize = roomSize;
                    _lastReverbSignature = reverbSignature;
                    _lastAppliedEnabled = true;
                    return;
                }

                if (enabling)
                {
                    if (_compatSubmixInstalled)
                    {
                        SetBoolField(audio, _enableReverbField, true);
                        SetBoolField(audio, _applyReverbField, true);
                    }
                    else
                    {
                        SetBool(audio, _enableReverbProperty, true);
                        SetBool(audio, _applyReverbProperty, true);
                    }

                    EnableGameSubmixEffect(audio, true);
                }

                if (enabling || parameterChanged)
                {
                    if (!_compatSubmixInstalled)
                        _setReverbParametersMethod.Invoke(audio, new object[] { diffusion, roomSize });
                    TryApplyDirectReverbParameters(audio, settings, out _lastParameterStatus);
                    _lastDiffusion = diffusion;
                    _lastRoomSize = roomSize;
                    _lastReverbSignature = reverbSignature;
                }

                _lastEnabled = true;
                _lastAppliedEnabled = true;
                _lastEffectStatus = DescribeGameVoiceEffect(audio);
                RefreshObservedVoices(audio);
                _lastStatus = string.Format(
                    CultureInfo.InvariantCulture,
                    "on real-xaudio diff={0:0.00} room={1:0.00}",
                    diffusion,
                    roomSize);

                if (enabling)
                    V2DebugLog.WriteEvent("global-reverb", _lastStatus + " " + _lastChainStatus + " " + _lastParameterStatus + " " + _lastEffectStatus + " " + _lastSampleRateStatus + " " + _lastObservedSummary);
            }
            catch (Exception ex)
            {
                _lastStatus = "apply failed: " + ex.GetType().Name;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Global reverb apply failed: " + ex);
                V2DebugLog.WriteEvent("global-reverb", _lastStatus);
                _lastEnabled = true;
                _lastAppliedEnabled = false;
            }
        }

        public static void UpdateGlobalBusRoute()
        {
            object audio = MyAudio.Static;
            if (audio == null)
                return;

            Resolve(audio.GetType());
            CaptureBaseState(audio);

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.GlobalReverbEnabled || !SettingsManager.IsGlobalReverbGlobalBusRoute(settings))
            {
                RestoreGlobalBusRoute("disabled");
                return;
            }

            try
            {
                bool wasActive = _globalBusVoice != null;
                string routeStatus;
                if (!EnsureGlobalBusRoute(audio, settings, out routeStatus))
                {
                    bool statusChanged = !string.Equals(_lastGlobalBusStatus, routeStatus, StringComparison.Ordinal);
                    _lastEnabled = true;
                    _lastAppliedEnabled = false;
                    _lastStatus = "globalbus blocked";
                    _lastGlobalBusStatus = routeStatus;
                    RefreshObservedVoices(audio);
                    LogGlobalBusStatus(statusChanged);
                    return;
                }

                _lastEnabled = true;
                _lastAppliedEnabled = true;
                _lastStatus = "on globalbus";
                _lastGlobalBusStatus = routeStatus;
                RefreshObservedVoices(audio);
                LogGlobalBusStatus(!wasActive);
            }
            catch (Exception ex)
            {
                _lastEnabled = true;
                _lastAppliedEnabled = false;
                _lastStatus = "globalbus failed: " + ex.GetType().Name;
                _lastGlobalBusStatus = _lastStatus;
                V2DebugLog.WriteEvent("global-reverb-bus", _lastStatus + " " + ex.Message);
            }
        }

        public static void RestoreVanillaState(string reason)
        {
            object audio = MyAudio.Static;
            if (audio == null)
                return;

            Resolve(audio.GetType());
            if (!_captureAttempted)
                CaptureBaseState(audio);

            try
            {
                RestoreGlobalBusRoute(reason);

                if (!_sourceVoiceFallbackActive || _compatSubmixInstalled)
                {
                    EnableGameSubmixEffect(audio, false);
                    RestoreBaseReverbParameters(audio);
                }

                if (_compatSubmixInstalled)
                {
                    if (_capturedApplyReverb)
                        SetBoolField(audio, _applyReverbField, _baseApplyReverb);
                    if (_capturedEnableReverb)
                        SetBoolField(audio, _enableReverbField, _baseEnableReverb);
                }
                else
                {
                    if (_capturedApplyReverb && _applyReverbProperty != null)
                        SetBool(audio, _applyReverbProperty, _baseApplyReverb);
                    if (_capturedEnableReverb && _enableReverbProperty != null)
                        SetBool(audio, _enableReverbProperty, _baseEnableReverb);
                }

                RestoreBaseGameSubmixRoute(audio);

                _lastAppliedEnabled = false;
                _lastEnabled = false;
                _lastStatus = "restored: " + reason;
                _lastEffectStatus = DescribeGameVoiceEffect(audio);
                RefreshObservedVoices(audio);
                V2DebugLog.WriteEvent("global-reverb", _lastStatus);
            }
            catch (Exception ex)
            {
                _lastStatus = "restore failed: " + ex.GetType().Name;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Global reverb restore failed: " + ex);
                V2DebugLog.WriteEvent("global-reverb", _lastStatus);
            }
        }

        public static void RestoreGlobalBusRoute(string reason)
        {
            object audio = MyAudio.Static;
            object routedGameVoice = _globalBusRoutedGameVoice;
            object originalOutputs = _globalBusOriginalOutputVoices;
            bool hadRoute = routedGameVoice != null
                || _globalBusVoice != null
                || _globalBusReverbEffect != null
                || _globalBusPatchedGameAudioVoiceDesc != null
                || _customInlineEffect != null;

            RestoreCustomInlineRoute(reason);
            RestoreGlobalBusFutureVoiceRoute(audio, reason);

            if (routedGameVoice != null && originalOutputs != null)
            {
                try
                {
                    MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(routedGameVoice.GetType());
                    setOutputVoices?.Invoke(routedGameVoice, new[] { originalOutputs });
                    V2DebugLog.WriteEvent("global-reverb-bus", "route-restored " + reason);
                }
                catch (Exception ex)
                {
                    V2DebugLog.WriteEvent("global-reverb-bus", "route-restore-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            try
            {
                if (_globalBusVoice != null)
                {
                    ResolveSourceDisableEffectMethod(_globalBusVoice.GetType())?.Invoke(_globalBusVoice, new object[] { SourceReverbEffectIndex });
                    MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(_globalBusVoice.GetType());
                    setEffectChain?.Invoke(_globalBusVoice, new object[] { null });
                }
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-bus", "effect-clear-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
            }

            DisposeComObject(_globalBusReverbEffect);
            DisposeComObject(_globalBusVoice);
            ClearGlobalBusSourceRoutes("restore " + reason);
            _globalBusVoice = null;
            _globalBusReverbEffect = null;
            _globalBusOriginalOutputVoices = null;
            _globalBusRoutedGameVoice = null;
            _globalBusOriginalGameAudioVoiceDesc = null;
            _globalBusPatchedGameAudioVoiceDesc = null;
            _customInlineVoice = null;
            _customInlineEffect = null;
            _customInlineSignature = string.Empty;
            _globalBusSignature = string.Empty;
            _globalBusRouteKey = string.Empty;
            _globalBusFailureSignature = string.Empty;
            _globalBusFailureStatus = string.Empty;
            _globalBusNextRetryUtc = DateTime.MinValue;
            _lastGlobalBusStatus = hadRoute ? "globalbus=restored " + reason : "globalbus=off";

            if (hadRoute && audio != null)
                RefreshObservedVoices(audio);
        }

        public static void ResetRuntimeState()
        {
            RestoreVanillaState("runtime reset");
            ClearSourceReverbTargets("runtime reset");
            ClearSourceReverbBuses("runtime reset");
            _audioType = null;
            _myAudioType = null;
            _submixVoiceType = null;
            _voiceSendDescriptorType = null;
            _reverbParametersType = null;
            _sharpReverbType = null;
            _effectDescriptorType = null;
            _applyReverbProperty = null;
            _enableReverbProperty = null;
            _currentOutputVoicesProperty = null;
            _setReverbParametersMethod = null;
            _setEffectParametersMethod = null;
            _getEffectParametersMethod = null;
            _isEffectEnabledMethod = null;
            _gameAudioVoiceField = null;
            _gameAudioVoiceDescField = null;
            _hudAudioVoiceField = null;
            _musicAudioVoiceField = null;
            _audioEngineField = null;
            _masterVoiceField = null;
            _reverbField = null;
            _reverbSetField = null;
            _applyReverbField = null;
            _enableReverbField = null;
            _maxSampleRateField = null;
            _voiceSendOutputVoiceField = null;
            SourceVoiceDeviceFields.Clear();
            SourceVoiceDetailsProperties.Clear();
            SourceSetEffectChainMethods.Clear();
            SourceSetEffectParameterMethods.Clear();
            SourceEnableEffectMethods.Clear();
            SourceDisableEffectMethods.Clear();
            SourceSetOutputVoicesMethods.Clear();
            SourceReverbFailedSignatures.Clear();
            SourceReverbRouteFailedSignatures.Clear();
            SourceReverbBuses.Clear();
            GlobalBusSourceRoutes.Clear();
            _resolved = false;
            _captureAttempted = false;
            _capturedApplyReverb = false;
            _capturedEnableReverb = false;
            _capturedReverbParameters = false;
            _wrapperParametersNoOp = false;
            _baseApplyReverb = false;
            _baseEnableReverb = false;
            _lastEnabled = false;
            _lastAppliedEnabled = false;
            _loggedUnavailable = false;
            _compatSubmixInstalled = false;
            _sourceVoiceFallbackActive = false;
            _directReverbInvalidForSession = false;
            _sourceReverbUnavailableForSession = false;
            _lastDiffusion = -1f;
            _lastRoomSize = -1f;
            _lastReverbSignature = string.Empty;
            _baseReverbParameters = null;
            _baseGameAudioVoice = null;
            _baseGameAudioVoiceDesc = null;
            _compatGameAudioVoice = null;
            _compatGameAudioVoiceDesc = null;
            _globalBusVoice = null;
            _globalBusReverbEffect = null;
            _globalBusOriginalOutputVoices = null;
            _globalBusRoutedGameVoice = null;
            _globalBusOriginalGameAudioVoiceDesc = null;
            _globalBusPatchedGameAudioVoiceDesc = null;
            _customInlineVoice = null;
            _customInlineEffect = null;
            _customInlineSignature = string.Empty;
            _lastCompatRerouteLogUtc = DateTime.MinValue;
            _lastGlobalBusLogUtc = DateTime.MinValue;
            _lastStatus = "reset";
            _lastParameterStatus = "direct=disabled-stability";
            _lastEffectStatus = "effect=?";
            _lastChainStatus = "chain=?";
            _lastSampleRateStatus = "rate=?";
            _lastObservedSummary = "voices=?";
            _lastAffectedVoices = "No live game-audio voices observed yet.";
            _lastSourceReverbSummary = "sourceReverb=0";
            _lastGlobalBusStatus = "globalbus=off";
            _lastGlobalBusSourceStatus = "srcRoute=0";
            _globalBusSignature = string.Empty;
            _globalBusRouteKey = string.Empty;
            _globalBusFailureSignature = string.Empty;
            _globalBusFailureStatus = string.Empty;
            _globalBusNextRetryUtc = DateTime.MinValue;
            _lastSourceReverbLogUtc = DateTime.MinValue;
        }

        public static string FormatStatus()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} | api={1} apply={2} enable={3} wrapperParams={4} {5} {6} {7} {8} {9} {10} {11} {12} {13}",
                _lastStatus,
                _audioType == null ? "?" : _audioType.Name,
                _applyReverbProperty == null ? "missing" : "ok",
                _enableReverbProperty == null ? "missing" : "ok",
                _setReverbParametersMethod == null ? "missing" : (_wrapperParametersNoOp ? "noop" : "ok"),
                _lastParameterStatus,
                _lastEffectStatus,
                _lastChainStatus,
                _lastSampleRateStatus,
                _lastObservedSummary,
                _lastSourceReverbSummary,
                _lastGlobalBusStatus,
                _lastGlobalBusSourceStatus,
                DescribeGlobalBusEffect());
        }

        public static string FormatAffectedVoices(int maxLines)
        {
            object audio = MyAudio.Static;
            if (audio != null)
            {
                Resolve(audio.GetType());
                RefreshObservedVoices(audio);
            }

            return _lastAffectedVoices;
        }

        public static void SetSourceVoiceTarget(IMySourceVoice voice, bool target, string category, string cueName)
        {
            if (voice == null)
                return;

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (!target || settings == null || !settings.GlobalReverbEnabled)
            {
                ClearSourceVoiceTarget(voice, target ? "disabled" : "not-room-target");
                return;
            }

            object audio = MyAudio.Static;
            if (audio != null)
                Resolve(audio.GetType());

            if (!ShouldUseSourceVoiceFallback(audio))
            {
                ClearSourceVoiceTarget(voice, "global-submix-route");
                return;
            }

            if (!SourceReverbVoiceRoutingEnabled)
            {
                if (SourceReverbStates.TryGetValue(voice, out SourceReverbState forcedState) && forcedState.ForceRouted)
                {
                    forcedState.LastTouchedUtc = DateTime.UtcNow;
                    forcedState.Category = category ?? "?";
                    forcedState.CueName = cueName ?? "?";
                    UpdateSourceReverbSummary("forced " + forcedState.Category + "/" + Trim(forcedState.CueName, 28));
                    return;
                }

                ClearSourceVoiceTarget(voice, "route-disabled");
                _lastSourceReverbSummary = "sourceReverb=route-disabled wet-bus-only";
                return;
            }

            if (!TryApplySourceVoiceReverb(voice, settings, category, cueName, false, out string status))
            {
                LogSourceReverbStatus(status + " " + Trim(category + "/" + cueName, 44), false);
                if (status.IndexOf("xapo-disabled", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("types-missing", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ClearSourceVoiceTarget(voice, status);
                }
            }
        }

        public static void ClearSourceVoiceTarget(IMySourceVoice voice, string reason)
        {
            if (voice == null)
                return;

            if (!SourceReverbStates.TryGetValue(voice, out SourceReverbState state))
                return;

            ClearSourceVoiceReverb(voice, state, reason);
            SourceReverbStates.Remove(voice);
            UpdateSourceReverbSummary("clear " + reason);
        }

        public static bool TryApplyLiveSourceReverbSend(IMySourceVoice voice, string category, string cueName, out string status)
        {
            status = "source=live-send-disabled-invalid-call";
            return false;
        }

        // ===================================================================================================
        // Block dry/wet split (experimental, default OFF — gated on settings.BlockDryWetSplitEnabled)
        // ---------------------------------------------------------------------------------------------------
        // Goal: pull a block sound's DRY path onto an RSP-owned submix so we can attenuate it independently of
        // the reverb (so an occluded jukebox upstairs reads mostly as its reverb tail).
        //
        // What two probes taught us about the engine:
        //   * A single-destination REPLACE does NOT crash (the old APPEND did) — crash-safe.
        //   * The engine re-asserts a 3D voice's output every frame from the MANAGED descriptor cache
        //     (MySourceVoice.m_currentDescriptor), directly — NOT by calling SetOutputVoices (a postfix on that
        //     method never fired). So routing via the NATIVE SourceVoice left m_currentDescriptor=game and the
        //     engine kept overriding us back (continuous native re-routing fought it = the toggle glitch; a one-
        //     time native route lost outright = no effect).
        //   * Fix: route via the MANAGED MySourceVoice.SetOutputVoices, which updates m_currentDescriptor itself.
        //     Then the engine re-asserts SPLIT every frame for free — no fight, no glitch — and we can READ
        //     m_currentDescriptor (CurrentOutputVoices) to verify it stuck and only re-assert if it ever reverts.
        // Scoped to SoundBlock/jukebox cues for now; still routes the split submix to master (the game submix
        // rejected the send on the processing-stage rule), so block dry currently bypasses the inline reverb.
        // ===================================================================================================
        private static readonly Dictionary<IMySourceVoice, BlockSplitRoute> BlockSplitRoutes = new Dictionary<IMySourceVoice, BlockSplitRoute>();
        private static object _blockSplitSubmix;   // block voices route here (1 hop) -> master; carries the dry/wet reverb XAPO
        private static object _blockReverbEffect;  // V2LiveReverbPocProcessor on _blockSplitSubmix; dry/wet via SetBlockDryWet
        private static string _blockSplitGraph = "?";
        private static bool _blockSplitDisabledForSession;
        private static string _blockSplitStatus = "blocksplit=off";
        private static DateTime _lastBlockSplitLogUtc = DateTime.MinValue;
        private static long _blockSplitReheals;   // total times we had to re-assert after the engine reset m_currentDescriptor

        private sealed class BlockSplitRoute
        {
            public MethodInfo ManagedSetOutput;   // MySourceVoice.SetOutputVoices(VoiceSendDescriptor[]) — updates m_currentDescriptor
            public object[] SplitArgs;            // cached { split VoiceSendDescriptor[] } -> routes the dry to the split submix
            public object[] OriginalArgs;         // cached { original VoiceSendDescriptor[] } -> restores the game route on stop/disable
            public DateTime LastSeenUtc;
            public int Reheals;
            public string CueName;
        }

        public static string FormatBlockSplitStatus()
        {
            return _blockSplitStatus;
        }

        private static string TypeName(object o)
        {
            return o == null ? "null" : o.GetType().Name;
        }

        public static void UpdateBlockDryWetSplit()
        {
            object audio = MyAudio.Static;
            if (audio == null)
                return;

            Resolve(audio.GetType());

            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings == null || !settings.BlockDryWetSplitEnabled || _blockSplitDisabledForSession)
            {
                if (BlockSplitRoutes.Count > 0 || _blockSplitSubmix != null)
                    RestoreBlockSplit(_blockSplitDisabledForSession ? "session-disabled" : "disabled");
                return;
            }

            if (_submixVoiceType == null || _voiceSendDescriptorType == null)
            {
                _blockSplitStatus = "blocksplit=types-missing";
                return;
            }

            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
            object masterVoice = _masterVoiceField?.GetValue(audio);
            object device = ResolveAudioEngine(audio) ?? ResolveSourceVoiceDevice(gameVoice) ?? ResolveSourceVoiceDevice(masterVoice);
            if (gameVoice == null || device == null)
            {
                _blockSplitStatus = "blocksplit=device-missing";
                return;
            }

            if (!EnsureBlockSplitSubmix(device, gameVoice, masterVoice, out string ensureStatus))
            {
                _blockSplitStatus = ensureStatus;
                return;
            }

            // Independent dry/wet is done INSIDE the reverb XAPO on the block's own submix (no fan-out): the
            // processor mixes BlockDryLevel*dry + BlockWetLevel*wet. Occluded -> dry down / wet up = mostly reverb.
            V2LiveReverbPocProcessor blockVerb = _blockReverbEffect as V2LiveReverbPocProcessor;
            if (blockVerb != null)
            {
                blockVerb.UpdateFromSettings(settings); // keep the room character matched to the global reverb
                blockVerb.SetBlockDryWet(Clamp(settings.BlockDryLevel, 0f, 1f), Clamp(settings.BlockWetLevel, 0f, 2f));
            }

            MaintainBlockSplitTargets(gameVoice);
        }

        // Scan: adopt SoundBlock voices and route their DRY to the split submix via the MANAGED SetOutputVoices
        // (the only call that updates m_currentDescriptor, which the engine re-asserts from each frame). For voices
        // already adopted, READ m_currentDescriptor and re-assert ONLY if it has drifted back to the game submix —
        // so there is no per-frame mutation when the route is holding (the expected case), and "reheal" measures
        // whether the engine ever resets it.
        private static void MaintainBlockSplitTargets(object gameVoice)
        {
            if (_blockSplitSubmix == null || MyAudio.Static == null)
                return;

            DateTime now = DateTime.UtcNow;
            int added = 0, active = 0, onSplit = 0, reheal = 0;
            string lastFail = null;

            try
            {
                MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
                List<IMySourceVoice> voices = played.Sound;

                if (voices != null)
                {
                    for (int i = 0; i < voices.Count; i++)
                    {
                        IMySourceVoice voice = voices[i];
                        if (voice == null || !voice.IsValid || !voice.IsPlaying)
                            continue;

                        string cue = voice.CueEnum.ToString();
                        if (!V2AuxCueClassifier.IsSoundBlockCue(cue))
                            continue;

                        object[] descriptors = ResolveOutputVoiceDescriptors(voice);

                        if (BlockSplitRoutes.TryGetValue(voice, out BlockSplitRoute route))
                        {
                            route.LastSeenUtc = now;
                            active++;

                            // Is the managed descriptor cache still pointed at our split submix?
                            bool stillSplit = descriptors != null && DescriptorsContainOutputVoice(descriptors, _blockSplitSubmix);
                            if (stillSplit)
                            {
                                onSplit++;
                            }
                            else if (route.ManagedSetOutput != null)
                            {
                                // Engine drifted it back to the game submix — re-assert (rare if the hypothesis holds).
                                try { route.ManagedSetOutput.Invoke(voice, route.SplitArgs); route.Reheals++; reheal++; }
                                catch { BlockSplitRoutes.Remove(voice); }
                            }
                            continue;
                        }

                        if (descriptors == null)
                            continue;

                        // Only adopt voices currently on the game submix; leave HUD/music/foreign routes alone.
                        if (!DescriptorsContainOutputVoice(descriptors, gameVoice))
                            continue;

                        // Resolve the MANAGED SetOutputVoices on the wrapper itself (voice.GetType() == MySourceVoice),
                        // NOT on the native SourceVoice — only the managed call updates m_currentDescriptor.
                        MethodInfo managedSetOutput = ResolveSourceSetOutputVoicesMethod(voice.GetType());
                        if (managedSetOutput == null)
                        {
                            lastFail = "no-managed-setoutput";
                            continue;
                        }

                        object original = CreateVoiceSendDescriptorArrayFromDescriptors(descriptors);
                        object split = CreateVoiceSendDescriptorArray(_blockSplitSubmix);
                        if (original == null || split == null)
                        {
                            lastFail = "desc-build";
                            continue;
                        }

                        object[] splitArgs = new[] { split };
                        try
                        {
                            managedSetOutput.Invoke(voice, splitArgs);
                        }
                        catch (Exception ex)
                        {
                            Exception inner = (ex as TargetInvocationException)?.InnerException ?? ex.InnerException ?? ex;
                            lastFail = "route:" + inner.GetType().Name;
                            V2DebugLog.WriteEvent("block-split", "initial-route-failed " + Trim(cue, 28) + " " + inner.GetType().Name + ": " + inner.Message);
                            continue;
                        }

                        BlockSplitRoutes[voice] = new BlockSplitRoute
                        {
                            ManagedSetOutput = managedSetOutput,
                            SplitArgs = splitArgs,
                            OriginalArgs = new[] { original },
                            LastSeenUtc = now,
                            CueName = cue
                        };
                        added++;
                        active++;
                        onSplit++;
                        V2DebugLog.WriteEvent("block-split", "initial-route " + Trim(cue, 28) + " -> split");
                    }
                }

                PurgeStaleBlockSplitRoutes(now);
                _blockSplitReheals += reheal;
                _blockSplitStatus = string.Format(
                    CultureInfo.InvariantCulture,
                    "blocksplit=on [{0}] active={1} onSplit={2} added={3} reheal={4} totalReheal={5}{6}",
                    _blockSplitGraph,
                    active,
                    onSplit,
                    added,
                    reheal,
                    _blockSplitReheals,
                    lastFail == null ? string.Empty : " lastFail=" + lastFail);

                if (added > 0 || reheal > 0 || now - _lastBlockSplitLogUtc > TimeSpan.FromSeconds(2))
                {
                    _lastBlockSplitLogUtc = now;
                    V2DebugLog.WriteEvent("block-split", _blockSplitStatus);
                }
            }
            catch (Exception ex)
            {
                _blockSplitStatus = "blocksplit=scan-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("block-split", _blockSplitStatus + " " + ex.Message);
            }
        }

        // Build the block reverb submix once: block voices route here (1 hop) and it carries a dry/wet reverb XAPO,
        // then -> master. The dry/wet split happens INSIDE the XAPO (SetBlockDryWet), so there's no fan-out. A source
        // CAN feed an XAPO submix when it's the direct target (the inline reverb proves this); the earlier fan-out
        // failed only because a *submix* feeding an XAPO child returned XAUDIO2_E_INVALID_CALL on source-connect.
        private static bool EnsureBlockSplitSubmix(object device, object gameVoice, object masterVoice, out string status)
        {
            if (_blockSplitSubmix != null)
            {
                status = "blocksplit=graph-ready [" + _blockSplitGraph + "]";
                return true;
            }

            if (masterVoice == null)
            {
                status = "blocksplit=master-missing";
                return false;
            }

            object submix = null, reverbEffect = null;
            try
            {
                int channels = Math.Max(1, ResolveSourceInputChannelCount(gameVoice));
                int sampleRate = ResolveVoiceInputSampleRate(gameVoice);
                if (sampleRate <= 0)
                    sampleRate = ResolveVoiceInputSampleRate(masterVoice);
                if (sampleRate <= 0)
                    sampleRate = 48000;
                sampleRate = Math.Max(8000, sampleRate);

                submix = CreateSubmixAtStage(device, channels, sampleRate, 0);
                if (submix == null)
                {
                    status = "blocksplit=submix-create-failed";
                    V2DebugLog.WriteEvent("block-split", status);
                    return false;
                }

                // dry+wet reverb XAPO (NOT wet-only — it mixes dry passthrough + reverb, the dry/wet balance is set
                // per occlusion via SetBlockDryWet). Attached the way the working inline reverb is: descriptor with
                // the channel count, then enable.
                string verbStatus = "verb=skip";
                MethodInfo setChain = ResolveSourceSetEffectChainMethod(submix.GetType());
                if (setChain != null && _effectDescriptorType != null)
                {
                    try
                    {
                        V2LiveReverbPocProcessor processor = new V2LiveReverbPocProcessor(channels, sampleRate, false);
                        processor.UpdateFromSettings(SettingsManager.Current);
                        object descriptor = CreateSourceReverbDescriptor(processor, channels);
                        Array descriptors = Array.CreateInstance(_effectDescriptorType, 1);
                        descriptors.SetValue(descriptor, 0);
                        setChain.Invoke(submix, new object[] { descriptors });
                        ResolveSourceEnableEffectMethod(submix.GetType())?.Invoke(submix, new object[] { SourceReverbEffectIndex });
                        reverbEffect = processor;
                        verbStatus = "verb=on";
                    }
                    catch (Exception ex)
                    {
                        Exception inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                        verbStatus = "verb=fail:" + inner.GetType().Name;
                        V2DebugLog.WriteEvent("block-split", "reverb-attach-failed " + inner.GetType().Name + ": " + inner.Message);
                        DisposeComObject(reverbEffect);
                        reverbEffect = null;
                    }
                }

                bool toMaster = TrySetSubmixOutputs(submix, masterVoice);

                _blockSplitSubmix = submix;
                _blockReverbEffect = reverbEffect;
                _blockSplitGraph = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}ch/{1}Hz submix->m={2} {3}",
                    channels, sampleRate, toMaster ? "Y" : "N", verbStatus);
                status = "blocksplit=graph-built [" + _blockSplitGraph + "]";
                V2DebugLog.WriteEvent("block-split", status + " master=" + TypeName(masterVoice) + " submix=" + TypeName(submix));
                return true;
            }
            catch (Exception ex)
            {
                DisposeComObject(reverbEffect); DisposeComObject(submix);
                _blockSplitDisabledForSession = true;
                status = "blocksplit=graph-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("block-split", status + " " + ex.Message);
                return false;
            }
        }

        private static object CreateSubmixAtStage(object device, int channels, int sampleRate, int stage)
        {
            channels = Math.Max(1, channels);
            sampleRate = Math.Max(8000, sampleRate);
            if (stage > 0 && _submixVoiceFlagsType != null)
            {
                try
                {
                    object flagsNone = Enum.ToObject(_submixVoiceFlagsType, 0);
                    return Activator.CreateInstance(_submixVoiceType, device, channels, sampleRate, flagsNone, stage);
                }
                catch (Exception ex)
                {
                    V2DebugLog.WriteEvent("block-split", "staged-submix-failed stage=" + stage + " " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            try { return Activator.CreateInstance(_submixVoiceType, device, channels, sampleRate); }
            catch { return null; }
        }

        private static bool TrySetSubmixOutputs(object submix, params object[] targets)
        {
            if (submix == null || targets == null || targets.Length == 0)
                return false;

            MethodInfo setOutput = ResolveSourceSetOutputVoicesMethod(submix.GetType());
            if (setOutput == null)
                return false;

            try
            {
                object descriptorArray = CreateVoiceSendDescriptorArray(targets);
                if (descriptorArray == null)
                    return false;

                setOutput.Invoke(submix, new[] { descriptorArray });
                return true;
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("block-split", "submix-output-failed " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void PurgeStaleBlockSplitRoutes(DateTime now)
        {
            if (BlockSplitRoutes.Count == 0)
                return;

            List<IMySourceVoice> remove = null;
            foreach (KeyValuePair<IMySourceVoice, BlockSplitRoute> pair in BlockSplitRoutes)
            {
                IMySourceVoice voice = pair.Key;
                if (voice == null || !voice.IsValid || !voice.IsPlaying || now - pair.Value.LastSeenUtc > TimeSpan.FromSeconds(2))
                {
                    if (remove == null)
                        remove = new List<IMySourceVoice>();
                    remove.Add(voice);
                }
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                RestoreBlockSplitVoice(remove[i], "stale");
        }

        private static void RestoreBlockSplitVoice(IMySourceVoice voice, string reason)
        {
            if (voice == null || !BlockSplitRoutes.TryGetValue(voice, out BlockSplitRoute route))
                return;

            BlockSplitRoutes.Remove(voice);

            // Only touch the live voice while it is still valid; a stopped voice has already been recycled.
            if (voice.IsValid && route.ManagedSetOutput != null && route.OriginalArgs != null)
            {
                try
                {
                    route.ManagedSetOutput.Invoke(voice, route.OriginalArgs);
                }
                catch (Exception ex)
                {
                    V2DebugLog.WriteEvent("block-split", "restore-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public static void RestoreBlockSplit(string reason)
        {
            if (BlockSplitRoutes.Count > 0)
            {
                List<IMySourceVoice> voices = new List<IMySourceVoice>(BlockSplitRoutes.Keys);
                for (int i = 0; i < voices.Count; i++)
                    RestoreBlockSplitVoice(voices[i], reason);
            }

            // Voices are restored to the game submix above; now tear down the block reverb submix + its XAPO.
            DisposeComObject(_blockSplitSubmix);
            DisposeComObject(_blockReverbEffect);
            _blockSplitSubmix = null;
            _blockReverbEffect = null;
            _blockSplitGraph = "?";
            _blockSplitStatus = "blocksplit=restored:" + reason;
            V2DebugLog.WriteEvent("block-split", _blockSplitStatus);
        }

        private static bool CanApply => _applyReverbProperty != null && _enableReverbProperty != null && _setReverbParametersMethod != null;

        private static void Resolve(Type type)
        {
            if (_resolved && _audioType == type)
                return;

            _audioType = type;
            _applyReverbProperty = FindBoolProperty(type, "ApplyReverb");
            _enableReverbProperty = FindBoolProperty(type, "EnableReverb");
            _setReverbParametersMethod = type.GetMethod("SetReverbParameters", InstanceMembers, null, new[] { typeof(float), typeof(float) }, null);
            _wrapperParametersNoOp = IsNoOp(_setReverbParametersMethod);
            _gameAudioVoiceField = type.GetField("m_gameAudioVoice", InstanceMembers);
            _gameAudioVoiceDescField = type.GetField("m_gameAudioVoiceDesc", InstanceMembers);
            _hudAudioVoiceField = type.GetField("m_hudAudioVoice", InstanceMembers);
            _musicAudioVoiceField = type.GetField("m_musicAudioVoice", InstanceMembers);
            _audioEngineField = type.GetField("m_audioEngine", InstanceMembers);
            _masterVoiceField = type.GetField("m_masterVoice", InstanceMembers);
            _reverbField = type.GetField("m_reverb", InstanceMembers);
            _reverbSetField = type.GetField("m_reverbSet", InstanceMembers);
            _applyReverbField = type.GetField("m_applyReverb", InstanceMembers);
            _enableReverbField = type.GetField("m_enableReverb", InstanceMembers);
            _submixVoiceType = ResolveType("SharpDX.XAudio2.SubmixVoice");
            _submixVoiceFlagsType = ResolveType("SharpDX.XAudio2.SubmixVoiceFlags");
            _voiceSendDescriptorType = ResolveType("SharpDX.XAudio2.VoiceSendDescriptor");
            _reverbParametersType = ResolveType("SharpDX.XAudio2.Fx.ReverbParameters");
            _sharpReverbType = ResolveType("SharpDX.XAudio2.Fx.Reverb");
            _effectDescriptorType = ResolveType("SharpDX.XAudio2.EffectDescriptor");
            _myAudioType = ResolveType("VRage.Audio.MyAudio");
            _maxSampleRateField = _myAudioType?.GetField("MAX_SAMPLE_RATE", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _resolved = true;
            _loggedUnavailable = false;
        }

        private static PropertyInfo FindBoolProperty(Type type, string name)
        {
            PropertyInfo direct = type.GetProperty(name, InstanceMembers);
            if (direct != null && direct.PropertyType == typeof(bool) && direct.GetSetMethod(true) != null)
                return direct;

            foreach (PropertyInfo property in type.GetProperties(InstanceMembers))
            {
                if (property.PropertyType == typeof(bool)
                    && property.GetSetMethod(true) != null
                    && property.Name.EndsWith(name, StringComparison.Ordinal))
                {
                    return property;
                }
            }

            return null;
        }

        private static void CaptureBaseState(object audio)
        {
            if (_captureAttempted)
                return;

            _captureAttempted = true;
            _capturedApplyReverb = TryGetBool(audio, _applyReverbProperty, out _baseApplyReverb);
            _capturedEnableReverb = TryGetBool(audio, _enableReverbProperty, out _baseEnableReverb);
        }

        private static bool TryGetBool(object target, PropertyInfo property, out bool value)
        {
            value = false;
            if (target == null || property == null || property.GetGetMethod(true) == null)
                return false;

            try
            {
                value = (bool)property.GetValue(target, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SetBool(object target, PropertyInfo property, bool value)
        {
            if (property == null)
                return;
            property.SetValue(target, value, null);
        }

        private static bool EnsureGameSubmixReverbChain(object audio, out string status)
        {
            status = "chain=unavailable";
            _sourceVoiceFallbackActive = false;
            _lastSampleRateStatus = "rate=?";
            if (audio == null)
                return false;

            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
            object masterVoice = _masterVoiceField?.GetValue(audio);
            object audioEngine = _audioEngineField?.GetValue(audio);
            if (gameVoice == null)
            {
                status = "chain=gameVoice-missing";
                return false;
            }

            int masterRate = ResolveVoiceInputSampleRate(masterVoice);
            int gameRate = ResolveVoiceInputSampleRate(gameVoice);
            int maxRate = ResolveMaxReverbSampleRate();
            int masterChannels = ResolveSourceInputChannelCount(masterVoice);
            int gameChannels = ResolveSourceInputChannelCount(gameVoice);
            _lastSampleRateStatus = string.Format(
                CultureInfo.InvariantCulture,
                "rate master={0}Hz/{1}ch game={2}Hz/{3}ch max={4}Hz",
                masterRate,
                masterChannels,
                gameRate,
                gameChannels,
                maxRate);

            if (gameRate > maxRate && CompatGameSubmixRoutingEnabled)
            {
                int compatChannels = gameChannels > 0 ? gameChannels : masterChannels;
                if (TryInstallCompatibleGameSubmix(audio, audioEngine, compatChannels, maxRate, gameVoice, out gameVoice, out status))
                {
                    gameRate = ResolveVoiceInputSampleRate(gameVoice);
                    gameChannels = ResolveSourceInputChannelCount(gameVoice);
                    _lastSampleRateStatus = string.Format(
                        CultureInfo.InvariantCulture,
                        "rate master={0}Hz/{1}ch game={2}Hz/{3}ch max={4}Hz compat=Y",
                        masterRate,
                        masterChannels,
                        gameRate,
                        gameChannels,
                        maxRate);
                }
                else
                {
                    _sourceVoiceFallbackActive = true;
                    status = string.Format(
                        CultureInfo.InvariantCulture,
                        "chain=source-voice-fallback master={0}Hz game={1}Hz max={2}Hz compat={3}",
                        masterRate,
                        gameRate,
                        maxRate,
                        status);
                    return false;
                }
            }

            if (gameRate > maxRate)
            {
                _sourceVoiceFallbackActive = true;
                status = string.Format(
                    CultureInfo.InvariantCulture,
                    "chain=source-voice-fallback master={0}Hz game={1}Hz max={2}Hz",
                    masterRate,
                    gameRate,
                    maxRate);
                return false;
            }

            bool reverbSet = TryGetBoolField(audio, _reverbSetField, out bool currentSet) && currentSet;
            if (!reverbSet && !_compatSubmixInstalled && _enableReverbProperty != null)
            {
                SetBool(audio, _enableReverbProperty, true);
                reverbSet = TryGetBoolField(audio, _reverbSetField, out currentSet) && currentSet;
                if (reverbSet)
                {
                    SetBoolField(audio, _enableReverbField, true);
                    status = "chain=vanilla-attached";
                    return true;
                }
            }

            if (reverbSet)
            {
                SetBoolField(audio, _enableReverbField, true);
                status = _compatSubmixInstalled ? "chain=compat-return-attached" : "chain=already-attached";
                return true;
            }

            if (_sharpReverbType == null || _effectDescriptorType == null)
            {
                status = "chain=types-missing";
                return false;
            }

            if (audioEngine == null)
            {
                status = "chain=audioEngine-missing";
                return false;
            }

            if (!TryAttachGameSubmixReverbManually(audio, audioEngine, gameVoice, masterChannels, out status))
                return false;

            _directReverbInvalidForSession = false;
            return true;
        }

        private static bool TryInstallCompatibleGameSubmix(object audio, object audioEngine, int channels, int sampleRate, object oldGameVoice, out object gameVoice, out string status)
        {
            gameVoice = null;
            status = "chain=compat-unavailable";
            if (audio == null || audioEngine == null)
                return false;

            if (_compatSubmixInstalled && _compatGameAudioVoice != null)
            {
                gameVoice = _compatGameAudioVoice;
                if (_gameAudioVoiceField != null && !ReferenceEquals(_gameAudioVoiceField.GetValue(audio), _compatGameAudioVoice))
                    _gameAudioVoiceField.SetValue(audio, _compatGameAudioVoice);
                if (_gameAudioVoiceDescField != null && !ReferenceEquals(_gameAudioVoiceDescField.GetValue(audio), _compatGameAudioVoiceDesc))
                    _gameAudioVoiceDescField.SetValue(audio, _compatGameAudioVoiceDesc);
                status = "chain=compat-return-existing";
                return true;
            }

            if (_submixVoiceType == null || _voiceSendDescriptorType == null || _gameAudioVoiceField == null || _gameAudioVoiceDescField == null)
            {
                status = "chain=compat-types-missing";
                return false;
            }

            try
            {
                int safeChannels = Math.Max(1, channels);
                int safeRate = Math.Max(8000, Math.Min(48000, sampleRate));
                object newGameVoice = Activator.CreateInstance(_submixVoiceType, audioEngine, safeChannels, safeRate);
                object descriptorArray = CreateVoiceSendDescriptorArray(newGameVoice);
                if (descriptorArray == null)
                {
                    DisposeComObject(newGameVoice);
                    status = "chain=compat-desc-failed";
                    return false;
                }

                _baseGameAudioVoice = _baseGameAudioVoice ?? oldGameVoice;
                _baseGameAudioVoiceDesc = _baseGameAudioVoiceDesc ?? _gameAudioVoiceDescField.GetValue(audio);
                _compatGameAudioVoice = newGameVoice;
                _compatGameAudioVoiceDesc = descriptorArray;
                _compatSubmixInstalled = true;
                _gameAudioVoiceField.SetValue(audio, newGameVoice);
                _gameAudioVoiceDescField.SetValue(audio, descriptorArray);
                gameVoice = newGameVoice;
                status = string.Format(CultureInfo.InvariantCulture, "chain=compat-return {0}Hz/{1}ch future-voices", safeRate, safeChannels);
                V2DebugLog.WriteEvent("global-reverb-chain", status);
                return true;
            }
            catch (Exception ex)
            {
                status = "chain=compat-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-chain", status + " " + ex.Message);
                return false;
            }
        }

        private static bool ShouldUseSourceVoiceFallback(object audio)
        {
            if (_sourceVoiceFallbackActive)
                return true;

            if (audio == null)
                return false;

            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
            object masterVoice = _masterVoiceField?.GetValue(audio);
            int maxRate = ResolveMaxReverbSampleRate();
            int masterRate = ResolveVoiceInputSampleRate(masterVoice);
            int gameRate = ResolveVoiceInputSampleRate(gameVoice);
            return masterRate > maxRate || gameRate > maxRate;
        }

        private static bool EnsureGlobalBusRoute(object audio, RealisticSoundPlusSettings settings, out string status)
        {
            status = "globalbus=unavailable";
            if (audio == null)
                return false;

            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
            object masterVoice = _masterVoiceField?.GetValue(audio);
            object device = ResolveAudioEngine(audio) ?? ResolveSourceVoiceDevice(gameVoice) ?? ResolveSourceVoiceDevice(masterVoice);
            if (gameVoice == null)
            {
                status = "globalbus=gameVoice-missing";
                return false;
            }

            if (device == null)
            {
                status = "globalbus=device-missing";
                return false;
            }

            if (SettingsManager.IsGlobalReverbCustomMasterRoute(settings))
            {
                if (masterVoice == null)
                {
                    status = "custommaster=masterVoice-missing";
                    return false;
                }

                return EnsureCustomInlineRoute(audio, settings, masterVoice, gameVoice, masterVoice, device, "master", out status);
            }

            if (SettingsManager.IsGlobalReverbCustomInlineRoute(settings))
                return EnsureCustomInlineRoute(audio, settings, gameVoice, gameVoice, masterVoice, device, "game", out status);

            if (_submixVoiceType == null || _voiceSendDescriptorType == null || _sharpReverbType == null || _effectDescriptorType == null || _reverbParametersType == null)
            {
                status = "globalbus=types-missing";
                return false;
            }

            int masterRate = ResolveVoiceInputSampleRate(masterVoice);
            int masterChannels = ResolveSourceInputChannelCount(masterVoice);
            int gameRate = ResolveVoiceInputSampleRate(gameVoice);
            int gameChannels = ResolveSourceInputChannelCount(gameVoice);
            int maxRate = ResolveMaxReverbSampleRate();
            int safeRate = gameRate > 0 ? gameRate : (masterRate > 0 ? masterRate : 48000);
            int busChannels = Math.Max(1, Math.Min(2, gameChannels > 0 ? gameChannels : masterChannels));
            bool customBus = SettingsManager.IsGlobalReverbCustomBusRoute(settings);
            string routeKey = customBus ? "custom" : "stock";
            _lastSampleRateStatus = string.Format(
                CultureInfo.InvariantCulture,
                "rate master={0}Hz/{1}ch game={2}Hz/{3}ch bus={4}Hz/{5}ch stockMax={6}Hz mode=future-desc/{7}",
                masterRate,
                masterChannels,
                gameRate,
                gameChannels,
                safeRate,
                busChannels,
                maxRate,
                routeKey);

            if (_globalBusRoutedGameVoice != null && !ReferenceEquals(_globalBusRoutedGameVoice, gameVoice))
                RestoreGlobalBusRoute("gameVoice-changed");
            if (_globalBusVoice != null && !string.Equals(_globalBusRouteKey, routeKey, StringComparison.Ordinal))
                RestoreGlobalBusRoute("route-changed");

            string attemptSignature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}Hz/{2}ch:{3}",
                routeKey,
                safeRate,
                busChannels,
                BuildSourceReverbSignature(settings));

            if (_globalBusVoice == null)
            {
                if (IsGlobalBusFailureCoolingDown(attemptSignature, out status))
                    return false;

                if (!TryCreateGlobalBus(device, busChannels, safeRate, settings, routeKey, out status))
                {
                    RecordGlobalBusFailure(attemptSignature, status);
                    return false;
                }
            }

            if (!TryUpdateGlobalBusParameters(settings, out string paramStatus))
            {
                status = paramStatus;
                RecordGlobalBusFailure(attemptSignature, status);
                return false;
            }

            TrySetVoiceVolume(_globalBusVoice, Clamp((settings?.GlobalReverbWetSend ?? 1f) * GlobalBusWetVolumeScale, 0f, 4f), out string volumeStatus);

            if (!TryPatchGameFutureVoiceRoute(audio, gameVoice, out string routeStatus))
            {
                status = routeStatus;
                return false;
            }

            string sourceRouteStatus = "srcRoute=future-only";
            if (customBus && GlobalBusDirectSourceRoutingEnabled)
                sourceRouteStatus = RouteCurrentlyPlayingSourcesToGlobalBus(audio, gameVoice);
            else if (customBus)
            {
                _lastGlobalBusSourceStatus = "srcRoute=disabled-setmatrix-crash";
                sourceRouteStatus = _lastGlobalBusSourceStatus;
            }

            status = string.Format(
                CultureInfo.InvariantCulture,
                "globalbus=active {0} {1} {2} {3}",
                routeStatus,
                paramStatus,
                volumeStatus,
                sourceRouteStatus);
            ClearGlobalBusFailure(attemptSignature);
            return true;
        }

        private static string RouteCurrentlyPlayingSourcesToGlobalBus(object audio, object gameVoice)
        {
            if (audio == null || gameVoice == null || _globalBusVoice == null || MyAudio.Static == null)
                return "srcRoute=unavailable";

            int seen = 0;
            int routed = 0;
            int already = 0;
            int skipped = 0;
            int failed = 0;
            string lastFailure = string.Empty;
            DateTime now = DateTime.UtcNow;

            try
            {
                MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
                List<IMySourceVoice> voices = played.Sound;
                if (voices == null || voices.Count == 0)
                {
                    PurgeGlobalBusSourceRoutes(now);
                    _lastGlobalBusSourceStatus = "srcRoute=0";
                    return _lastGlobalBusSourceStatus;
                }

                for (int i = 0; i < voices.Count; i++)
                {
                    IMySourceVoice voice = voices[i];
                    if (voice == null || !voice.IsValid || !voice.IsPlaying)
                        continue;

                    object[] descriptors = ResolveOutputVoiceDescriptors(voice);
                    if (!DescriptorsContainOutputVoice(descriptors, gameVoice))
                    {
                        skipped++;
                        continue;
                    }

                    seen++;
                    if (DescriptorsContainOutputVoice(descriptors, _globalBusVoice))
                    {
                        already++;
                        if (GlobalBusSourceRoutes.TryGetValue(voice, out GlobalBusSourceRouteState existing))
                            existing.LastTouchedUtc = now;
                        continue;
                    }

                    if (!TryRouteSourceVoiceToGlobalBus(voice, descriptors, now, out string routeStatus))
                    {
                        failed++;
                        lastFailure = routeStatus;
                        continue;
                    }

                    routed++;
                }

                PurgeGlobalBusSourceRoutes(now);
                _lastGlobalBusSourceStatus = string.Format(
                    CultureInfo.InvariantCulture,
                    "srcRoute seen={0} routed={1} already={2} active={3} skip={4} fail={5}{6}",
                    seen,
                    routed,
                    already,
                    GlobalBusSourceRoutes.Count,
                    skipped,
                    failed,
                    string.IsNullOrEmpty(lastFailure) ? string.Empty : " last=" + Trim(lastFailure, 36));

                if (routed > 0 || failed > 0)
                    V2DebugLog.WriteEvent("global-reverb-bus", _lastGlobalBusSourceStatus);

                return _lastGlobalBusSourceStatus;
            }
            catch (Exception ex)
            {
                _lastGlobalBusSourceStatus = "srcRoute=failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", _lastGlobalBusSourceStatus + " " + DescribeException(ex));
                return _lastGlobalBusSourceStatus;
            }
        }

        private static bool TryRouteSourceVoiceToGlobalBus(IMySourceVoice voice, object[] descriptors, DateTime now, out string status)
        {
            status = "srcRoute=failed";
            if (voice == null || descriptors == null || descriptors.Length == 0 || _globalBusVoice == null)
                return false;

            if (!RspDynamicAudioFilters.TryResolveNativeSourceVoice(voice, out object sourceVoice) || sourceVoice == null)
            {
                status = "srcRoute=native-missing";
                return false;
            }

            MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(sourceVoice.GetType());
            if (setOutputVoices == null)
            {
                status = "srcRoute=set-output-missing";
                return false;
            }

            object originalOutputVoices = CreateVoiceSendDescriptorArrayFromDescriptors(descriptors);
            object descriptorArray = AppendVoiceSendDescriptor(descriptors, _globalBusVoice);
            if (originalOutputVoices == null || descriptorArray == null)
            {
                status = "srcRoute=desc-build-failed";
                return false;
            }

            try
            {
                setOutputVoices.Invoke(sourceVoice, new[] { descriptorArray });
                GlobalBusSourceRoutes[voice] = new GlobalBusSourceRouteState
                {
                    NativeSourceVoice = sourceVoice,
                    OriginalOutputVoices = originalOutputVoices,
                    LastTouchedUtc = now,
                    CueName = voice.CueEnum.ToString()
                };
                status = "srcRoute=appended";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "srcRoute=append-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(inner));
                return false;
            }
            catch (Exception ex)
            {
                status = "srcRoute=append-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static void PurgeGlobalBusSourceRoutes(DateTime now)
        {
            if (GlobalBusSourceRoutes.Count == 0)
                return;

            List<IMySourceVoice> remove = null;
            foreach (KeyValuePair<IMySourceVoice, GlobalBusSourceRouteState> pair in GlobalBusSourceRoutes)
            {
                IMySourceVoice voice = pair.Key;
                if (voice == null || !voice.IsValid || !voice.IsPlaying || now - pair.Value.LastTouchedUtc > TimeSpan.FromSeconds(2))
                {
                    if (remove == null)
                        remove = new List<IMySourceVoice>();
                    remove.Add(voice);
                }
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                ClearGlobalBusSourceRoute(remove[i], "stale");
        }

        private static void ClearGlobalBusSourceRoutes(string reason)
        {
            if (GlobalBusSourceRoutes.Count == 0)
                return;

            List<IMySourceVoice> voices = new List<IMySourceVoice>(GlobalBusSourceRoutes.Keys);
            for (int i = 0; i < voices.Count; i++)
                ClearGlobalBusSourceRoute(voices[i], reason);
        }

        private static void ClearGlobalBusSourceRoute(IMySourceVoice voice, string reason)
        {
            if (voice == null || !GlobalBusSourceRoutes.TryGetValue(voice, out GlobalBusSourceRouteState state))
                return;

            GlobalBusSourceRoutes.Remove(voice);
            try
            {
                MethodInfo setOutputVoices = state.NativeSourceVoice == null ? null : ResolveSourceSetOutputVoicesMethod(state.NativeSourceVoice.GetType());
                if (setOutputVoices != null && state.OriginalOutputVoices != null)
                    setOutputVoices.Invoke(state.NativeSourceVoice, new[] { state.OriginalOutputVoices });
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-bus", "srcRoute-restore-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsGlobalBusFailureCoolingDown(string signature, out string status)
        {
            status = null;
            if (string.IsNullOrEmpty(signature)
                || !string.Equals(signature, _globalBusFailureSignature, StringComparison.Ordinal)
                || DateTime.UtcNow >= _globalBusNextRetryUtc)
            {
                return false;
            }

            double remaining = Math.Max(0.1, (_globalBusNextRetryUtc - DateTime.UtcNow).TotalSeconds);
            status = string.Format(
                CultureInfo.InvariantCulture,
                "{0} retry={1:0.0}s",
                string.IsNullOrEmpty(_globalBusFailureStatus) ? "globalbus=retry-wait" : _globalBusFailureStatus,
                remaining);
            return true;
        }

        private static void RecordGlobalBusFailure(string signature, string status)
        {
            if (string.IsNullOrEmpty(signature))
                return;

            _globalBusFailureSignature = signature;
            _globalBusFailureStatus = string.IsNullOrEmpty(status) ? "globalbus=failed" : Trim(status, 80);
            _globalBusNextRetryUtc = DateTime.UtcNow.AddSeconds(GlobalBusFailureRetrySeconds);
        }

        private static void ClearGlobalBusFailure(string signature)
        {
            if (!string.IsNullOrEmpty(signature) && !string.Equals(signature, _globalBusFailureSignature, StringComparison.Ordinal))
                return;

            _globalBusFailureSignature = string.Empty;
            _globalBusFailureStatus = string.Empty;
            _globalBusNextRetryUtc = DateTime.MinValue;
        }

        private static bool TryPatchGameFutureVoiceRoute(object audio, object gameVoice, out string status)
        {
            status = "globalbus=future-route-failed";
            if (audio == null || gameVoice == null || _globalBusVoice == null)
                return false;

            if (_gameAudioVoiceDescField == null)
            {
                status = "globalbus=future-desc-missing";
                return false;
            }

            try
            {
                object currentDescriptorArray = _gameAudioVoiceDescField.GetValue(audio);
                if (_globalBusPatchedGameAudioVoiceDesc != null && ReferenceEquals(currentDescriptorArray, _globalBusPatchedGameAudioVoiceDesc))
                {
                    object[] existing = ResolveVoiceSendDescriptorArray(currentDescriptorArray);
                    status = string.Format(CultureInfo.InvariantCulture, "globalbus=future-desc-existing desc={0}", existing == null ? 0 : existing.Length);
                    return true;
                }

                object[] descriptors = ResolveVoiceSendDescriptorArray(currentDescriptorArray);
                if (descriptors == null || descriptors.Length == 0)
                    descriptors = CreateVoiceSendDescriptors(gameVoice);

                if (descriptors == null || descriptors.Length == 0)
                {
                    status = "globalbus=future-desc-empty";
                    return false;
                }

                if (DescriptorsContainOutputVoice(descriptors, _globalBusVoice))
                {
                    _globalBusPatchedGameAudioVoiceDesc = currentDescriptorArray;
                    status = string.Format(CultureInfo.InvariantCulture, "globalbus=future-desc-existing desc={0}", descriptors.Length);
                    return true;
                }

                object patchedDescriptorArray = AppendVoiceSendDescriptor(descriptors, _globalBusVoice);
                if (patchedDescriptorArray == null)
                {
                    status = "globalbus=future-desc-build-failed";
                    return false;
                }

                _globalBusOriginalGameAudioVoiceDesc = currentDescriptorArray;
                _globalBusPatchedGameAudioVoiceDesc = patchedDescriptorArray;
                _gameAudioVoiceDescField.SetValue(audio, patchedDescriptorArray);
                status = string.Format(CultureInfo.InvariantCulture, "globalbus=future-desc-appended desc={0}->{1}", descriptors.Length, descriptors.Length + 1);
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeDescriptorOutputs(descriptors));
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "globalbus=future-desc-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(inner));
                return false;
            }
            catch (Exception ex)
            {
                status = "globalbus=future-desc-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static void RestoreGlobalBusFutureVoiceRoute(object audio, string reason)
        {
            if (audio == null || _gameAudioVoiceDescField == null || _globalBusVoice == null)
                return;

            try
            {
                object currentDescriptorArray = _gameAudioVoiceDescField.GetValue(audio);
                object[] currentDescriptors = ResolveVoiceSendDescriptorArray(currentDescriptorArray);
                bool currentContainsBus = DescriptorsContainOutputVoice(currentDescriptors, _globalBusVoice);

                if (_globalBusOriginalGameAudioVoiceDesc != null
                    && (ReferenceEquals(currentDescriptorArray, _globalBusPatchedGameAudioVoiceDesc) || currentContainsBus))
                {
                    _gameAudioVoiceDescField.SetValue(audio, _globalBusOriginalGameAudioVoiceDesc);
                    V2DebugLog.WriteEvent("global-reverb-bus", "future-desc-restored " + reason);
                    return;
                }

                if (currentContainsBus)
                {
                    object restoredDescriptorArray = RemoveVoiceSendDescriptor(currentDescriptors, _globalBusVoice);
                    if (restoredDescriptorArray != null)
                    {
                        _gameAudioVoiceDescField.SetValue(audio, restoredDescriptorArray);
                        V2DebugLog.WriteEvent("global-reverb-bus", "future-desc-removed " + reason);
                    }
                }
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-bus", "future-desc-restore-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryCreateGlobalBus(object device, int channels, int sampleRate, RealisticSoundPlusSettings settings, string routeKey, out string status)
        {
            status = "globalbus=create-failed";
            object busVoice = null;
            object reverbEffect = null;
            try
            {
                busVoice = Activator.CreateInstance(_submixVoiceType, device, Math.Max(1, channels), Math.Max(8000, sampleRate));
                MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(busVoice.GetType());
                if (setEffectChain == null)
                {
                    status = "globalbus=set-chain-missing";
                    DisposeComObject(busVoice);
                    return false;
                }

                reverbEffect = CreateGlobalBusEffect(device, channels, sampleRate, settings, routeKey, out string effectStatus);
                if (!TrySetSourceReverbEffectChain(busVoice, setEffectChain, reverbEffect, out string chainStatus))
                {
                    status = "globalbus=" + chainStatus;
                    DisposeComObject(reverbEffect);
                    DisposeComObject(busVoice);
                    return false;
                }

                _globalBusVoice = busVoice;
                _globalBusReverbEffect = reverbEffect;
                _globalBusRouteKey = routeKey ?? string.Empty;
                status = string.Format(CultureInfo.InvariantCulture, "globalbus=created-{0} {1}Hz/{2}ch {3} {4}", _globalBusRouteKey, sampleRate, channels, effectStatus, chainStatus);
                V2DebugLog.WriteEvent("global-reverb-bus", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "globalbus=create-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(inner));
                DisposeComObject(reverbEffect);
                DisposeComObject(busVoice);
                return false;
            }
            catch (Exception ex)
            {
                status = "globalbus=create-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(ex));
                DisposeComObject(reverbEffect);
                DisposeComObject(busVoice);
                return false;
            }
        }

        private static bool EnsureCustomInlineRoute(object audio, RealisticSoundPlusSettings settings, object targetVoice, object gameVoice, object masterVoice, object device, string targetName, out string status)
        {
            string routeName = string.Equals(targetName, "master", StringComparison.OrdinalIgnoreCase) ? "custommaster" : "custominline";
            status = routeName + "=unavailable";
            if (audio == null || targetVoice == null || device == null)
                return false;

            if (_effectDescriptorType == null)
            {
                status = routeName + "=types-missing";
                return false;
            }

            if (_globalBusVoice != null || _globalBusPatchedGameAudioVoiceDesc != null || _globalBusRoutedGameVoice != null)
                RestoreGlobalBusRoute("custominline-route");

            int masterRate = ResolveVoiceInputSampleRate(masterVoice);
            int masterChannels = ResolveSourceInputChannelCount(masterVoice);
            int gameRate = ResolveVoiceInputSampleRate(gameVoice);
            int gameChannels = ResolveSourceInputChannelCount(gameVoice);
            int maxRate = ResolveMaxReverbSampleRate();
            int targetRate = ResolveVoiceInputSampleRate(targetVoice);
            int targetChannels = ResolveSourceInputChannelCount(targetVoice);
            int safeRate = targetRate > 0 ? targetRate : (gameRate > 0 ? gameRate : (masterRate > 0 ? masterRate : 48000));
            int channels = Math.Max(1, Math.Min(2, targetChannels > 0 ? targetChannels : (gameChannels > 0 ? gameChannels : masterChannels)));
            _lastSampleRateStatus = string.Format(
                CultureInfo.InvariantCulture,
                "rate master={0}Hz/{1}ch game={2}Hz/{3}ch {4}={5}Hz/{6}ch stockMax={7}Hz mode=custom-inline",
                masterRate,
                masterChannels,
                gameRate,
                gameChannels,
                targetName,
                safeRate,
                channels,
                maxRate);

            if (_customInlineVoice != null && !ReferenceEquals(_customInlineVoice, targetVoice))
                RestoreCustomInlineRoute(targetName + "-voice-changed");

            string signature = string.Format(
                CultureInfo.InvariantCulture,
                "inline:{0}:{1}Hz/{2}ch:{3}",
                targetName,
                safeRate,
                channels,
                BuildSourceReverbSignature(settings));

            if (_customInlineEffect == null)
            {
                if (IsGlobalBusFailureCoolingDown(signature, out status))
                    return false;

                if (!TryAttachCustomInlineEffect(targetVoice, channels, safeRate, settings, routeName, out status))
                {
                    RecordGlobalBusFailure(signature, status);
                    return false;
                }
            }

            if (!TryUpdateCustomInlineParameters(settings, signature, out string paramStatus))
            {
                status = paramStatus;
                RecordGlobalBusFailure(signature, status);
                return false;
            }

            _lastGlobalBusSourceStatus = string.Equals(targetName, "master", StringComparison.OrdinalIgnoreCase)
                ? "srcRoute=inline-master"
                : "srcRoute=inline-submix";
            _lastGlobalBusStatus = string.Format(
                CultureInfo.InvariantCulture,
                "globalbus=inline-active {0}",
                paramStatus);
            status = _lastGlobalBusStatus + " " + _lastGlobalBusSourceStatus;
            ClearGlobalBusFailure(signature);
            return true;
        }

        private static bool TryAttachCustomInlineEffect(object targetVoice, int channels, int sampleRate, RealisticSoundPlusSettings settings, string routeName, out string status)
        {
            status = routeName + "=attach-failed";
            if (targetVoice == null)
                return false;

            MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(targetVoice.GetType());
            if (setEffectChain == null)
            {
                status = routeName + "=set-chain-missing";
                return false;
            }

            V2LiveReverbPocProcessor processor = null;
            try
            {
                processor = new V2LiveReverbPocProcessor(Math.Max(1, channels), Math.Max(8000, sampleRate), false);
                processor.UpdateFromSettings(settings);
                object descriptor = CreateSourceReverbDescriptor(processor, Math.Max(1, channels));
                Array descriptors = Array.CreateInstance(_effectDescriptorType, 1);
                descriptors.SetValue(descriptor, 0);
                setEffectChain.Invoke(targetVoice, new object[] { descriptors });
                ResolveSourceEnableEffectMethod(targetVoice.GetType())?.Invoke(targetVoice, new object[] { SourceReverbEffectIndex });
                _customInlineVoice = targetVoice;
                _customInlineEffect = processor;
                _customInlineSignature = string.Empty;
                status = string.Format(CultureInfo.InvariantCulture, "{0}=attached {1}", routeName, processor.Status);
                V2DebugLog.WriteEvent("global-reverb-bus", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = routeName + "=attach-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(inner));
                DisposeComObject(processor);
                return false;
            }
            catch (Exception ex)
            {
                status = routeName + "=attach-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(ex));
                DisposeComObject(processor);
                return false;
            }
        }

        private static bool TryUpdateCustomInlineParameters(RealisticSoundPlusSettings settings, string signature, out string status)
        {
            V2LiveReverbPocProcessor processor = _customInlineEffect as V2LiveReverbPocProcessor;
            if (processor == null)
            {
                status = "custominline=processor-missing";
                return false;
            }

            processor.UpdateFromSettings(settings);
            if (string.Equals(_customInlineSignature, signature, StringComparison.Ordinal))
            {
                status = "custominline=params-live " + processor.DiagnosticStatus;
                return true;
            }

            _customInlineSignature = signature;
            status = "custominline=params " + processor.DiagnosticStatus;
            V2DebugLog.WriteEvent("global-reverb-bus", status);
            return true;
        }

        private static void RestoreCustomInlineRoute(string reason)
        {
            object voice = _customInlineVoice;
            object effect = _customInlineEffect;
            if (voice == null && effect == null)
                return;

            try
            {
                if (voice != null)
                {
                    ResolveSourceDisableEffectMethod(voice.GetType())?.Invoke(voice, new object[] { SourceReverbEffectIndex });
                    MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(voice.GetType());
                    setEffectChain?.Invoke(voice, new object[] { null });
                }
                V2DebugLog.WriteEvent("global-reverb-bus", "custominline-restored " + reason);
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-bus", "custominline-restore-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
            }

            DisposeComObject(effect);
            _customInlineVoice = null;
            _customInlineEffect = null;
            _customInlineSignature = string.Empty;
        }

        private static object CreateGlobalBusEffect(object device, int channels, int sampleRate, RealisticSoundPlusSettings settings, string routeKey, out string status)
        {
            if (string.Equals(routeKey, "custom", StringComparison.OrdinalIgnoreCase))
            {
                V2LiveReverbPocProcessor processor = new V2LiveReverbPocProcessor(Math.Max(1, channels), Math.Max(8000, sampleRate));
                processor.UpdateFromSettings(settings);
                status = processor.Status;
                return processor;
            }

            status = "stock-xaudio";
            return CreateReverbEffect(device);
        }

        private static bool TryUpdateGlobalBusParameters(RealisticSoundPlusSettings settings, out string status)
        {
            status = "globalbus=params";
            if (_globalBusVoice == null)
            {
                status = "globalbus=bus-missing";
                return false;
            }

            string signature = BuildSourceReverbSignature(settings);
            if (string.Equals(_globalBusSignature, signature, StringComparison.Ordinal))
            {
                status = "globalbus=params-existing";
                return true;
            }

            V2LiveReverbPocProcessor customProcessor = _globalBusReverbEffect as V2LiveReverbPocProcessor;
            if (customProcessor != null)
            {
                customProcessor.UpdateFromSettings(settings);
                _globalBusSignature = signature;
                status = "globalbus=custom-params " + customProcessor.Status;
                V2DebugLog.WriteEvent("global-reverb-bus", status);
                return true;
            }

            MethodInfo setParameters = ResolveSourceSetEffectParametersMethod(_globalBusVoice.GetType());
            MethodInfo enableEffect = ResolveSourceEnableEffectMethod(_globalBusVoice.GetType());
            if (setParameters == null)
            {
                status = "globalbus=set-params-missing";
                return false;
            }

            try
            {
                object parameters = CreateReverbParameters(settings, true);
                setParameters.Invoke(_globalBusVoice, new object[] { SourceReverbEffectIndex, parameters });
                enableEffect?.Invoke(_globalBusVoice, new object[] { SourceReverbEffectIndex });
                _globalBusSignature = signature;
                status = "globalbus=params-applied " + DescribeReverbParameters(parameters);
                V2DebugLog.WriteEvent("global-reverb-bus", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "globalbus=params-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(inner));
                return false;
            }
            catch (Exception ex)
            {
                status = "globalbus=params-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static bool TryRouteGameSubmixToGlobalBus(object audio, object gameVoice, object masterVoice, out string status)
        {
            status = "globalbus=route-failed";
            if (gameVoice == null || _globalBusVoice == null)
                return false;

            object[] descriptors = ResolveOutputVoiceDescriptorsFromVoiceObject(gameVoice);
            if ((descriptors == null || descriptors.Length == 0) && masterVoice != null)
                descriptors = CreateVoiceSendDescriptors(masterVoice);

            if (descriptors == null || descriptors.Length == 0)
            {
                status = "globalbus=no-dry-route";
                return false;
            }

            if (DescriptorsContainOutputVoice(descriptors, _globalBusVoice))
            {
                status = "globalbus=route-existing";
                return true;
            }

            MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(gameVoice.GetType());
            if (setOutputVoices == null)
            {
                status = "globalbus=set-output-missing";
                return false;
            }

            object originalOutputVoices = CreateVoiceSendDescriptorArrayFromDescriptors(descriptors);
            if (originalOutputVoices == null)
            {
                status = "globalbus=dry-route-copy-failed";
                return false;
            }

            object descriptorArray = AppendVoiceSendDescriptor(descriptors, _globalBusVoice);
            if (descriptorArray == null)
            {
                status = "globalbus=desc-failed";
                return false;
            }

            try
            {
                setOutputVoices.Invoke(gameVoice, new[] { descriptorArray });
                _globalBusOriginalOutputVoices = originalOutputVoices;
                _globalBusRoutedGameVoice = gameVoice;
                status = "globalbus=send-appended";
                V2DebugLog.WriteEvent("global-reverb-bus", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "globalbus=route-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(inner));
                return false;
            }
            catch (Exception ex)
            {
                status = "globalbus=route-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-bus", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static object[] CreateVoiceSendDescriptors(params object[] outputVoices)
        {
            if (outputVoices == null || _voiceSendDescriptorType == null)
                return null;

            List<object> descriptors = new List<object>();
            for (int i = 0; i < outputVoices.Length; i++)
            {
                object outputVoice = outputVoices[i];
                if (outputVoice == null)
                    continue;

                try
                {
                    descriptors.Add(Activator.CreateInstance(_voiceSendDescriptorType, outputVoice));
                }
                catch
                {
                    return null;
                }
            }

            return descriptors.Count == 0 ? null : descriptors.ToArray();
        }

        private static object[] ResolveVoiceSendDescriptorArray(object descriptorArray)
        {
            IEnumerable raw = descriptorArray as IEnumerable;
            if (raw == null)
                return null;

            try
            {
                List<object> descriptors = new List<object>();
                foreach (object descriptor in raw)
                {
                    if (descriptor != null && (_voiceSendDescriptorType == null || _voiceSendDescriptorType.IsInstanceOfType(descriptor)))
                        descriptors.Add(descriptor);
                }

                return descriptors.Count == 0 ? null : descriptors.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeDescriptorOutputs(object[] descriptors)
        {
            if (descriptors == null || descriptors.Length == 0)
                return "outputs=none";

            StringBuilder builder = new StringBuilder();
            builder.Append("outputs=");
            for (int i = 0; i < descriptors.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');

                object output = ResolveOutputVoice(descriptors[i]);
                builder.Append(output == null ? "null" : Trim(output.GetType().Name, 24));
            }

            return builder.ToString();
        }

        private static bool TrySetVoiceVolume(object voice, float volume, out string status)
        {
            status = string.Format(CultureInfo.InvariantCulture, "vol={0:0.00}", volume);
            if (voice == null)
            {
                status = "vol=voice-missing";
                return false;
            }

            MethodInfo setVolume = ResolveSetVoiceVolumeMethod(voice.GetType());
            if (setVolume == null)
            {
                status = "vol=set-missing";
                return false;
            }

            try
            {
                ParameterInfo[] parameters = setVolume.GetParameters();
                object[] args = parameters.Length == 2
                    ? new object[] { volume, 0 }
                    : new object[] { volume };
                setVolume.Invoke(voice, args);
                return true;
            }
            catch (Exception ex)
            {
                status = "vol=failed:" + ex.GetType().Name;
                return false;
            }
        }

        private static MethodInfo ResolveSetVoiceVolumeMethod(Type voiceType)
        {
            if (voiceType == null)
                return null;

            foreach (MethodInfo method in voiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(method.Name, "SetVolume", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(float) && parameters[1].ParameterType == typeof(int))
                    return method;
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(float))
                    return method;
            }

            return null;
        }

        private static void LogGlobalBusStatus(bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastGlobalBusLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastGlobalBusLogUtc = now;
            V2DebugLog.WriteEvent("global-reverb-bus", _lastGlobalBusStatus + " " + _lastSampleRateStatus + " " + _lastObservedSummary + " " + DescribeGlobalBusEffect());
        }

        private static string DescribeGlobalBusEffect()
        {
            V2LiveReverbPocProcessor custom = _globalBusReverbEffect as V2LiveReverbPocProcessor;
            if (custom != null)
                return custom.DiagnosticStatus;

            if (_globalBusReverbEffect != null)
                return "effect=stock-xaudio";

            return "effect=none";
        }

        private static object CreateVoiceSendDescriptorArray(params object[] outputVoices)
        {
            if (outputVoices == null || outputVoices.Length == 0 || _voiceSendDescriptorType == null)
                return null;

            try
            {
                List<object> descriptorList = new List<object>();
                for (int i = 0; i < outputVoices.Length; i++)
                {
                    object outputVoice = outputVoices[i];
                    if (outputVoice == null)
                        continue;

                    object descriptor = Activator.CreateInstance(_voiceSendDescriptorType, outputVoice);
                    descriptorList.Add(descriptor);
                }

                if (descriptorList.Count == 0)
                    return null;

                Array descriptors = Array.CreateInstance(_voiceSendDescriptorType, descriptorList.Count);
                for (int i = 0; i < descriptorList.Count; i++)
                    descriptors.SetValue(descriptorList[i], i);

                return descriptors;
            }
            catch
            {
                return null;
            }
        }

        private static object AppendVoiceSendDescriptor(object[] existingDescriptors, object outputVoice)
        {
            if (existingDescriptors == null || outputVoice == null || _voiceSendDescriptorType == null)
                return null;

            try
            {
                object extraDescriptor = Activator.CreateInstance(_voiceSendDescriptorType, outputVoice);
                List<object> descriptorList = new List<object>();
                for (int i = 0; i < existingDescriptors.Length; i++)
                {
                    object descriptor = existingDescriptors[i];
                    if (descriptor != null && _voiceSendDescriptorType.IsInstanceOfType(descriptor))
                        descriptorList.Add(descriptor);
                }

                descriptorList.Add(extraDescriptor);
                Array descriptors = Array.CreateInstance(_voiceSendDescriptorType, descriptorList.Count);
                for (int i = 0; i < descriptorList.Count; i++)
                    descriptors.SetValue(descriptorList[i], i);

                return descriptors;
            }
            catch
            {
                return null;
            }
        }

        private static object CreateVoiceSendDescriptorArrayFromDescriptors(object[] descriptors)
        {
            if (descriptors == null || descriptors.Length == 0 || _voiceSendDescriptorType == null)
                return null;

            try
            {
                List<object> descriptorList = new List<object>();
                for (int i = 0; i < descriptors.Length; i++)
                {
                    object descriptor = descriptors[i];
                    if (descriptor != null && _voiceSendDescriptorType.IsInstanceOfType(descriptor))
                        descriptorList.Add(descriptor);
                }

                if (descriptorList.Count == 0)
                    return null;

                Array result = Array.CreateInstance(_voiceSendDescriptorType, descriptorList.Count);
                for (int i = 0; i < descriptorList.Count; i++)
                    result.SetValue(descriptorList[i], i);

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static object RemoveVoiceSendDescriptor(object[] existingDescriptors, object outputVoice)
        {
            if (existingDescriptors == null || outputVoice == null || _voiceSendDescriptorType == null)
                return null;

            try
            {
                List<object> descriptorList = new List<object>();
                for (int i = 0; i < existingDescriptors.Length; i++)
                {
                    object descriptor = existingDescriptors[i];
                    if (descriptor == null || !_voiceSendDescriptorType.IsInstanceOfType(descriptor))
                        continue;

                    object descriptorOutput = ResolveOutputVoice(descriptor);
                    if (ReferenceEquals(descriptorOutput, outputVoice))
                        continue;

                    descriptorList.Add(descriptor);
                }

                if (descriptorList.Count == 0)
                    return null;

                Array descriptors = Array.CreateInstance(_voiceSendDescriptorType, descriptorList.Count);
                for (int i = 0; i < descriptorList.Count; i++)
                    descriptors.SetValue(descriptorList[i], i);

                return descriptors;
            }
            catch
            {
                return null;
            }
        }

        private static bool DescriptorsContainOutputVoice(object[] descriptors, object outputVoice)
        {
            if (descriptors == null || outputVoice == null)
                return false;

            for (int i = 0; i < descriptors.Length; i++)
            {
                object descriptorOutput = ResolveOutputVoice(descriptors[i]);
                if (ReferenceEquals(descriptorOutput, outputVoice))
                    return true;
            }

            return false;
        }

        private static void RerouteCurrentlyPlayedGameVoices(object dryGameVoice, object reverbReturnVoice)
        {
            if (dryGameVoice == null || reverbReturnVoice == null || MyAudio.Static == null)
                return;

            try
            {
                MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
                List<IMySourceVoice> voices = played.Sound;
                if (voices == null)
                    return;

                int rerouted = 0;
                object hudVoice = _hudAudioVoiceField?.GetValue(MyAudio.Static);
                object musicVoice = _musicAudioVoiceField?.GetValue(MyAudio.Static);
                for (int i = 0; i < voices.Count; i++)
                {
                    IMySourceVoice voice = voices[i];
                    if (voice == null || !voice.IsValid || !voice.IsPlaying)
                        continue;

                    object[] descriptors = ResolveOutputVoiceDescriptors(voice);
                    if (!DescriptorsContainOutputVoice(descriptors, dryGameVoice) || DescriptorsContainOutputVoice(descriptors, reverbReturnVoice))
                        continue;

                    string route = ResolveOutputRoute(descriptors, dryGameVoice, hudVoice, musicVoice);
                    if (!string.Equals(route, "game", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!RspDynamicAudioFilters.TryResolveNativeSourceVoice(voice, out object sourceVoice) || sourceVoice == null)
                        continue;

                    MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(sourceVoice.GetType());
                    if (setOutputVoices == null)
                        continue;

                    object descriptorArray = AppendVoiceSendDescriptor(descriptors, reverbReturnVoice);
                    if (descriptorArray == null)
                        continue;

                    setOutputVoices.Invoke(sourceVoice, new[] { descriptorArray });
                    rerouted++;
                }

                DateTime now = DateTime.UtcNow;
                if (rerouted > 0 || now - _lastCompatRerouteLogUtc > TimeSpan.FromSeconds(1))
                {
                    _lastCompatRerouteLogUtc = now;
                    V2DebugLog.WriteEvent("global-reverb-chain", "compat-return-rerouted=" + rerouted.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-chain", "compat-return-reroute-failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RestoreBaseGameSubmixRoute(object audio)
        {
            if (audio == null)
                return;

            try
            {
                if (_compatSubmixInstalled && _compatGameAudioVoice != null)
                    RemoveReverbReturnFromCurrentlyPlayedVoices(_compatGameAudioVoice);

                if (_baseGameAudioVoice != null && ReferenceEquals(_gameAudioVoiceField?.GetValue(audio), _compatGameAudioVoice))
                    _gameAudioVoiceField?.SetValue(audio, _baseGameAudioVoice);
                if (_baseGameAudioVoiceDesc != null && ReferenceEquals(_gameAudioVoiceDescField?.GetValue(audio), _compatGameAudioVoiceDesc))
                    _gameAudioVoiceDescField?.SetValue(audio, _baseGameAudioVoiceDesc);

                _lastChainStatus = "chain=base-route-restored";
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-chain", "base-route-restore-failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RemoveReverbReturnFromCurrentlyPlayedVoices(object reverbReturnVoice)
        {
            if (reverbReturnVoice == null || MyAudio.Static == null)
                return;

            try
            {
                MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
                List<IMySourceVoice> voices = played.Sound;
                if (voices == null)
                    return;

                int restored = 0;
                for (int i = 0; i < voices.Count; i++)
                {
                    IMySourceVoice voice = voices[i];
                    if (voice == null || !voice.IsValid)
                        continue;

                    object[] descriptors = ResolveOutputVoiceDescriptors(voice);
                    if (!DescriptorsContainOutputVoice(descriptors, reverbReturnVoice))
                        continue;

                    if (!RspDynamicAudioFilters.TryResolveNativeSourceVoice(voice, out object sourceVoice) || sourceVoice == null)
                        continue;

                    MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(sourceVoice.GetType());
                    if (setOutputVoices == null)
                        continue;

                    object descriptorArray = RemoveVoiceSendDescriptor(descriptors, reverbReturnVoice);
                    if (descriptorArray == null)
                        continue;

                    setOutputVoices.Invoke(sourceVoice, new[] { descriptorArray });
                    restored++;
                }

                V2DebugLog.WriteEvent("global-reverb-chain", "compat-return-restored=" + restored.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-chain", "compat-return-restore-failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryAttachGameSubmixReverbManually(object audio, object audioEngine, object gameVoice, int outputChannels, out string status)
        {
            status = "chain=manual-failed";
            object reverbEffect = null;
            try
            {
                MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(gameVoice.GetType());
                if (setEffectChain == null)
                {
                    status = "chain=set-chain-missing";
                    return false;
                }

                reverbEffect = Activator.CreateInstance(_sharpReverbType, new[] { audioEngine });
                object descriptor = CreateSourceReverbDescriptor(reverbEffect, outputChannels);
                Array descriptors = Array.CreateInstance(_effectDescriptorType, 1);
                descriptors.SetValue(descriptor, 0);
                setEffectChain.Invoke(gameVoice, new object[] { descriptors });
                ResolveSourceDisableEffectMethod(gameVoice.GetType())?.Invoke(gameVoice, new object[] { ReverbEffectIndex });
                _reverbField?.SetValue(audio, reverbEffect);
                SetBoolField(audio, _reverbSetField, true);
                SetBoolField(audio, _enableReverbField, true);
                status = "chain=manual-attached";
                V2DebugLog.WriteEvent("global-reverb-chain", status + " " + _lastSampleRateStatus);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "chain=manual-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-chain", status + " " + inner.Message);
                DisposeComObject(reverbEffect);
                return false;
            }
            catch (Exception ex)
            {
                status = "chain=manual-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("global-reverb-chain", status + " " + ex.Message);
                DisposeComObject(reverbEffect);
                return false;
            }
        }

        private static void EnableGameSubmixEffect(object audio, bool enabled)
        {
            object gameVoice = ResolveReverbEffectVoice(audio);
            if (gameVoice == null)
                return;

            try
            {
                if (enabled)
                    ResolveSourceEnableEffectMethod(gameVoice.GetType())?.Invoke(gameVoice, new object[] { ReverbEffectIndex });
                else
                    ResolveSourceDisableEffectMethod(gameVoice.GetType())?.Invoke(gameVoice, new object[] { ReverbEffectIndex });
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("global-reverb-chain", "effect-toggle-failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static object ResolveReverbEffectVoice(object audio)
        {
            if (_compatSubmixInstalled && _compatGameAudioVoice != null)
                return _compatGameAudioVoice;

            return _gameAudioVoiceField?.GetValue(audio);
        }

        private static bool TryGetBoolField(object target, FieldInfo field, out bool value)
        {
            value = false;
            if (target == null || field == null)
                return false;

            try
            {
                value = (bool)field.GetValue(target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SetBoolField(object target, FieldInfo field, bool value)
        {
            if (target == null || field == null)
                return;

            try
            {
                field.SetValue(target, value);
            }
            catch
            {
            }
        }

        private static int ResolveMaxReverbSampleRate()
        {
            try
            {
                object value = _maxSampleRateField?.GetValue(null);
                if (value != null)
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
            }

            return 48000;
        }

        private static int ResolveVoiceInputSampleRate(object voice)
        {
            object details = ResolveVoiceDetails(voice);
            if (details == null)
                return 0;

            FieldInfo field = details.GetType().GetField("InputSampleRate", InstanceMembers);
            if (field == null)
                return 0;

            try
            {
                return Convert.ToInt32(field.GetValue(details), CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryApplyDirectReverbParameters(object audio, RealisticSoundPlusSettings settings, out string status)
        {
            status = "direct=unavailable";
            if (audio == null)
                return false;

            if (_directReverbInvalidForSession)
            {
                status = "direct=invalid-call-disabled";
                return false;
            }

            object gameVoice = ResolveReverbEffectVoice(audio);
            if (gameVoice == null)
            {
                status = "direct=gameVoice-missing";
                return false;
            }

            if (_reverbParametersType == null)
            {
                status = "direct=params-type-missing";
                return false;
            }

            MethodInfo setParameters = ResolveSetEffectParametersMethod(gameVoice.GetType());
            if (setParameters == null)
            {
                status = "direct=set-method-missing";
                return false;
            }

            try
            {
                CaptureBaseReverbParameters(gameVoice);
                object parameters = CreateReverbParameters(settings, false);
                setParameters.Invoke(gameVoice, new object[] { ReverbEffectIndex, parameters });

                object readback;
                string readbackStatus = TryGetEffectParameters(gameVoice, out readback)
                    ? DescribeReverbParameters(readback)
                    : "readback-missing";

                status = "direct=applied " + readbackStatus;
                V2DebugLog.WriteEvent("global-reverb-direct", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                bool invalidCall = IsInvalidXAudioCall(inner);
                if (invalidCall)
                    _directReverbInvalidForSession = true;
                status = invalidCall ? "direct=invalid-call-disabled" : "direct=failed:" + inner.GetType().Name;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Direct reverb parameter apply failed: " + inner);
                V2DebugLog.WriteEvent("global-reverb-direct", status + " " + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                bool invalidCall = IsInvalidXAudioCall(ex);
                if (invalidCall)
                    _directReverbInvalidForSession = true;
                status = invalidCall ? "direct=invalid-call-disabled" : "direct=failed:" + ex.GetType().Name;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Direct reverb parameter apply failed: " + ex);
                V2DebugLog.WriteEvent("global-reverb-direct", status + " " + ex.Message);
                return false;
            }
        }

        private static bool IsInvalidXAudioCall(Exception ex)
        {
            string text = ex?.ToString() ?? string.Empty;
            return text.IndexOf("XAUDIO2_E_INVALID_CALL", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("InvalidCall", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("0x88960001", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsXapoCreationFailed(Exception ex)
        {
            string text = ex?.ToString() ?? string.Empty;
            return text.IndexOf("XAUDIO2_E_XAPO_CREATION_FAILED", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("XapoCreationFailed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("0x88960003", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null)
                return "unknown";

            string message = ex.Message ?? string.Empty;
            string text = ex.ToString();
            int hresult = ex.HResult;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} hr=0x{1:X8} msg={2} text={3}",
                ex.GetType().Name,
                hresult,
                Trim(message, 120),
                Trim(text, 240));
        }

        private static bool TryApplySourceVoiceReverb(IMySourceVoice voice, RealisticSoundPlusSettings settings, string category, string cueName, bool forceRoute, out string status)
        {
            status = "source=unavailable";
            if (_sourceReverbUnavailableForSession)
            {
                status = "source=xapo-disabled";
                return false;
            }

            if (voice == null || !voice.IsValid || !voice.IsPlaying)
            {
                status = "source=not-playing";
                return false;
            }

            object audio = MyAudio.Static;
            if (audio != null)
                Resolve(audio.GetType());

            if (_sharpReverbType == null || _effectDescriptorType == null || _reverbParametersType == null)
            {
                status = "source=types-missing";
                return false;
            }

            if (!RspDynamicAudioFilters.TryResolveNativeSourceVoice(voice, out object sourceVoice) || sourceVoice == null)
            {
                status = "source=native-missing";
                return false;
            }

            int sourceRate = ResolveVoiceInputSampleRate(sourceVoice);
            int maxRate = ResolveMaxReverbSampleRate();
            if (sourceRate > maxRate)
            {
                status = string.Format(CultureInfo.InvariantCulture, "source=rate-blocked:{0}>{1}", sourceRate, maxRate);
                return false;
            }

            if (!SourceReverbVoiceRoutingEnabled && !forceRoute)
            {
                status = "source=route-disabled-game3d";
                _lastSourceReverbSummary = "sourceReverb=route-disabled game3d-safe";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            string signature = BuildSourceReverbSignature(settings);
            if (!SourceReverbStates.TryGetValue(voice, out SourceReverbState state))
            {
                if (!TryEnsureSourceReverbBus(audio, sourceVoice, settings, now, out SourceReverbBusState bus, out status))
                    return false;

                if (!TryRouteSourceVoiceToReverbBus(voice, sourceVoice, bus, now, out object originalOutputVoices, out status))
                    return false;

                state = new SourceReverbState
                {
                    NativeSourceVoice = sourceVoice,
                    OriginalOutputVoices = originalOutputVoices,
                    ReverbBusKey = bus.Key,
                    ReverbBusVoice = bus.BusVoice,
                    Category = category ?? "?",
                    CueName = cueName ?? "?",
                    ForceRouted = forceRoute
                };

                SourceReverbStates[voice] = state;
            }
            else if (!string.IsNullOrEmpty(state.ReverbBusKey) && SourceReverbBuses.TryGetValue(state.ReverbBusKey, out SourceReverbBusState bus))
            {
                if (!TryUpdateSourceReverbBusParameters(bus, settings, out status))
                    return false;
            }

            if (!string.Equals(state.Signature, signature, StringComparison.Ordinal))
            {
                state.Signature = signature;
            }

            state.LastTouchedUtc = now;
            state.Category = category ?? "?";
            state.CueName = cueName ?? "?";
            state.ForceRouted = state.ForceRouted || forceRoute;
            status = "source=bus-routed";
            UpdateSourceReverbSummary("target " + state.Category + "/" + Trim(state.CueName, 28));
            LogSourceReverbStatus(_lastSourceReverbSummary, false);
            return true;
        }

        private static bool TryEnsureSourceReverbBus(object audio, object sourceVoice, RealisticSoundPlusSettings settings, DateTime now, out SourceReverbBusState bus, out string status)
        {
            bus = null;
            status = "source=bus-unavailable";
            if (_submixVoiceType == null || _sharpReverbType == null || _effectDescriptorType == null || _reverbParametersType == null)
            {
                status = "source=bus-types-missing";
                return false;
            }

            object device = ResolveAudioEngine(audio) ?? ResolveSourceVoiceDevice(sourceVoice);
            if (device == null)
            {
                status = "source=bus-device-missing";
                return false;
            }

            int sourceRate = ResolveVoiceInputSampleRate(sourceVoice);
            int channels = Math.Max(1, Math.Min(2, ResolveSourceInputChannelCount(sourceVoice)));
            string busKey = BuildSourceReverbBusKey(sourceRate, channels, device);
            if (IsSourceReverbFailureSuppressed(busKey, now))
            {
                status = "source=xapo-suppressed:" + Trim(busKey, 48);
                _lastSourceReverbSummary = "sourceReverb=xapo-suppressed " + Trim(busKey, 32);
                return false;
            }

            if (SourceReverbBuses.TryGetValue(busKey, out bus))
            {
                bus.LastTouchedUtc = now;
                return TryUpdateSourceReverbBusParameters(bus, settings, out status);
            }

            object busVoice = null;
            object reverbEffect = null;
            try
            {
                busVoice = Activator.CreateInstance(_submixVoiceType, device, channels, sourceRate);
                MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(busVoice.GetType());
                if (setEffectChain == null)
                {
                    status = "source=bus-set-chain-missing";
                    DisposeComObject(busVoice);
                    return false;
                }

                reverbEffect = CreateReverbEffect(device);
                if (!TrySetSourceReverbEffectChain(busVoice, setEffectChain, reverbEffect, out status))
                {
                    RegisterSourceReverbFailure(busKey, now, status);
                    DisposeComObject(reverbEffect);
                    DisposeComObject(busVoice);
                    return false;
                }

                bus = new SourceReverbBusState
                {
                    Key = busKey,
                    BusVoice = busVoice,
                    ReverbEffect = reverbEffect,
                    Channels = channels,
                    SampleRate = sourceRate,
                    LastTouchedUtc = now
                };

                if (!TryUpdateSourceReverbBusParameters(bus, settings, out status))
                {
                    RegisterSourceReverbFailure(busKey, now, status);
                    DisposeComObject(reverbEffect);
                    DisposeComObject(busVoice);
                    return false;
                }

                SourceReverbBuses[busKey] = bus;
                status = "source=bus-created:" + busKey;
                V2DebugLog.WriteEvent("source-reverb", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "source=bus-failed:" + inner.GetType().Name;
                RegisterSourceReverbFailure(busKey, now, status);
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(inner));
                DisposeComObject(reverbEffect);
                DisposeComObject(busVoice);
                return false;
            }
            catch (Exception ex)
            {
                status = "source=bus-failed:" + ex.GetType().Name;
                RegisterSourceReverbFailure(busKey, now, status);
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(ex));
                DisposeComObject(reverbEffect);
                DisposeComObject(busVoice);
                return false;
            }
        }

        private static bool TryUpdateSourceReverbBusParameters(SourceReverbBusState bus, RealisticSoundPlusSettings settings, out string status)
        {
            status = "source=bus-params";
            if (bus == null || bus.BusVoice == null)
            {
                status = "source=bus-missing";
                return false;
            }

            string signature = BuildSourceReverbSignature(settings);
            if (string.Equals(bus.Signature, signature, StringComparison.Ordinal))
            {
                status = "source=bus-existing";
                return true;
            }

            MethodInfo setParameters = ResolveSourceSetEffectParametersMethod(bus.BusVoice.GetType());
            MethodInfo enableEffect = ResolveSourceEnableEffectMethod(bus.BusVoice.GetType());
            if (setParameters == null)
            {
                status = "source=bus-set-params-missing";
                return false;
            }

            try
            {
                object parameters = CreateReverbParameters(settings, true);
                setParameters.Invoke(bus.BusVoice, new object[] { SourceReverbEffectIndex, parameters });
                enableEffect?.Invoke(bus.BusVoice, new object[] { SourceReverbEffectIndex });
                bus.Signature = signature;
                status = "source=bus-params-applied " + DescribeReverbParameters(parameters);
                V2DebugLog.WriteEvent("source-reverb-params", status);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "source=bus-params-failed:" + inner.GetType().Name;
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(inner));
                return false;
            }
            catch (Exception ex)
            {
                status = "source=bus-params-failed:" + ex.GetType().Name;
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static bool TryRouteSourceVoiceToReverbBus(IMySourceVoice voice, object sourceVoice, SourceReverbBusState bus, DateTime now, out object originalOutputVoices, out string status)
        {
            originalOutputVoices = null;
            status = "source=route-failed";
            if (voice == null || sourceVoice == null || bus == null || bus.BusVoice == null)
                return false;

            string routeKey = BuildSourceReverbRouteKey(sourceVoice, bus);
            if (IsSourceReverbRouteFailureSuppressed(routeKey, now))
            {
                status = "source=route-suppressed:" + Trim(routeKey, 48);
                _lastSourceReverbSummary = "sourceReverb=route-suppressed " + Trim(routeKey, 32);
                return false;
            }

            object[] descriptors = ResolveOutputVoiceDescriptors(voice);
            if (DescriptorsContainOutputVoice(descriptors, bus.BusVoice))
            {
                status = "source=bus-route-existing";
                return true;
            }

            MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(sourceVoice.GetType());
            if (setOutputVoices == null)
            {
                status = "source=set-output-missing";
                return false;
            }

            if (descriptors == null || descriptors.Length == 0)
            {
                status = "source=no-dry-route";
                return false;
            }

            originalOutputVoices = CreateVoiceSendDescriptorArrayFromDescriptors(descriptors);
            if (originalOutputVoices == null)
            {
                status = "source=dry-route-copy-failed";
                return false;
            }

            object descriptorArray = AppendVoiceSendDescriptor(descriptors, bus.BusVoice);
            if (descriptorArray == null)
            {
                status = "source=bus-desc-failed";
                return false;
            }

            try
            {
                setOutputVoices.Invoke(sourceVoice, new[] { descriptorArray });
                status = "source=bus-send-appended";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "source=bus-route-failed:" + inner.GetType().Name;
                RegisterSourceReverbRouteFailure(routeKey, now, status);
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(inner));
                return false;
            }
            catch (Exception ex)
            {
                status = "source=bus-route-failed:" + ex.GetType().Name;
                RegisterSourceReverbRouteFailure(routeKey, now, status);
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(ex));
                return false;
            }
        }

        private static bool TryCreateAndAttachSourceReverb(object audio, object sourceVoice, out object reverbEffect, out string status)
        {
            reverbEffect = null;
            status = "source=attach-failed";
            object device = ResolveAudioEngine(audio) ?? ResolveSourceVoiceDevice(sourceVoice);
            if (device == null)
            {
                status = "source=device-missing";
                return false;
            }

            MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(sourceVoice.GetType());
            if (setEffectChain == null)
            {
                status = "source=set-chain-missing";
                return false;
            }

            try
            {
                reverbEffect = CreateReverbEffect(device);
                if (!TrySetSourceReverbEffectChain(sourceVoice, setEffectChain, reverbEffect, out string chainStatus))
                {
                    status = chainStatus;
                    DisposeComObject(reverbEffect);
                    reverbEffect = null;
                    return false;
                }

                status = "source=chain-attached";
                V2DebugLog.WriteEvent("source-reverb", status + " rate=" + ResolveVoiceInputSampleRate(sourceVoice).ToString(CultureInfo.InvariantCulture) + "ch/" + ResolveSourceInputChannelCount(sourceVoice).ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                status = "source=chain-failed:" + inner.GetType().Name;
                if (IsXapoCreationFailed(inner))
                {
                    _lastSourceReverbSummary = "sourceReverb=xapo-failed";
                    V2DebugLog.WriteEvent("source-reverb", "source XAPO attach failed: " + DescribeException(inner));
                }
                else
                {
                    V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(inner));
                }
                DisposeComObject(reverbEffect);
                reverbEffect = null;
                return false;
            }
            catch (Exception ex)
            {
                status = "source=chain-failed:" + ex.GetType().Name;
                if (IsXapoCreationFailed(ex))
                {
                    _lastSourceReverbSummary = "sourceReverb=xapo-failed";
                    V2DebugLog.WriteEvent("source-reverb", "source XAPO attach failed: " + DescribeException(ex));
                }
                else
                {
                    V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(ex));
                }
                DisposeComObject(reverbEffect);
                reverbEffect = null;
                return false;
            }
        }

        private static object CreateSourceReverbDescriptor(object reverbEffect, int outputChannels)
        {
            try
            {
                return Activator.CreateInstance(_effectDescriptorType, reverbEffect, Math.Max(1, outputChannels));
            }
            catch
            {
                return Activator.CreateInstance(_effectDescriptorType, reverbEffect);
            }
        }

        private static object CreateReverbEffect(object device)
        {
            try
            {
                return Activator.CreateInstance(_sharpReverbType, device, false);
            }
            catch
            {
            }

            try
            {
                return Activator.CreateInstance(_sharpReverbType, device, true);
            }
            catch
            {
            }

            return Activator.CreateInstance(_sharpReverbType, new[] { device });
        }

        private static bool TrySetSourceReverbEffectChain(object sourceVoice, MethodInfo setEffectChain, object reverbEffect, out string status)
        {
            status = "source=chain-failed";
            int inputChannels = ResolveSourceInputChannelCount(sourceVoice);
            int[] channelAttempts = inputChannels == 2 ? new[] { 2, 1, 0 } : new[] { inputChannels, 2, 1, 0 };
            Exception last = null;

            for (int i = 0; i < channelAttempts.Length; i++)
            {
                int channels = channelAttempts[i];
                try
                {
                    object descriptor = channels > 0
                        ? CreateSourceReverbDescriptor(reverbEffect, channels)
                        : Activator.CreateInstance(_effectDescriptorType, reverbEffect);
                    Array descriptors = Array.CreateInstance(_effectDescriptorType, 1);
                    descriptors.SetValue(descriptor, 0);
                    setEffectChain.Invoke(sourceVoice, new object[] { descriptors });
                    status = "source=chain-attached/ch" + channels.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    last = ex.InnerException ?? ex;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            status = "source=chain-set-failed:" + (last == null ? "unknown" : last.GetType().Name);
            if (last != null)
                V2DebugLog.WriteEvent("source-reverb", status + " " + DescribeException(last));
            return false;
        }

        private static bool IsSourceReverbAttachFailure(string status)
        {
            if (string.IsNullOrEmpty(status))
                return false;

            return status.IndexOf("chain-set-failed", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("chain-failed", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("xapo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildSourceReverbFailureSignature(object audio, object sourceVoice)
        {
            object device = ResolveAudioEngine(audio) ?? ResolveSourceVoiceDevice(sourceVoice);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}Hz/{2}ch:{3}",
                sourceVoice == null ? "source?" : sourceVoice.GetType().Name,
                ResolveVoiceInputSampleRate(sourceVoice),
                ResolveSourceInputChannelCount(sourceVoice),
                device == null ? "device?" : device.GetType().Name);
        }

        private static string BuildSourceReverbBusKey(int sampleRate, int channels, object device)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "bus:{0}Hz/{1}ch:{2}",
                sampleRate,
                channels,
                device == null ? "device?" : device.GetType().Name);
        }

        private static string BuildSourceReverbRouteKey(object sourceVoice, SourceReverbBusState bus)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "route:{0}:{1}Hz/{2}ch->{3}",
                sourceVoice == null ? "source?" : sourceVoice.GetType().Name,
                ResolveVoiceInputSampleRate(sourceVoice),
                ResolveSourceInputChannelCount(sourceVoice),
                bus == null ? "bus?" : bus.Key);
        }

        private static bool IsSourceReverbFailureSuppressed(string signature, DateTime now)
        {
            if (string.IsNullOrEmpty(signature))
                return false;

            if (!SourceReverbFailedSignatures.TryGetValue(signature, out DateTime failedUtc))
                return false;

            if (now - failedUtc > TimeSpan.FromMinutes(5))
            {
                SourceReverbFailedSignatures.Remove(signature);
                return false;
            }

            return true;
        }

        private static void RegisterSourceReverbFailure(string signature, DateTime now, string status)
        {
            if (string.IsNullOrEmpty(signature))
                return;

            SourceReverbFailedSignatures[signature] = now;
            _lastSourceReverbSummary = "sourceReverb=xapo-failed " + Trim(signature, 40);
            LogSourceReverbStatus("source=xapo-cache " + Trim(signature, 64) + " " + status, true);
        }

        private static bool IsSourceReverbRouteFailureSuppressed(string signature, DateTime now)
        {
            if (string.IsNullOrEmpty(signature))
                return false;

            if (!SourceReverbRouteFailedSignatures.TryGetValue(signature, out DateTime failedUtc))
                return false;

            if (now - failedUtc > TimeSpan.FromMinutes(5))
            {
                SourceReverbRouteFailedSignatures.Remove(signature);
                return false;
            }

            return true;
        }

        private static void RegisterSourceReverbRouteFailure(string signature, DateTime now, string status)
        {
            if (string.IsNullOrEmpty(signature))
                return;

            SourceReverbRouteFailedSignatures[signature] = now;
            _lastSourceReverbSummary = "sourceReverb=route-failed " + Trim(signature, 40);
            LogSourceReverbStatus("source=route-cache " + Trim(signature, 64) + " " + status, true);
        }

        private static object ResolveAudioEngine(object audio)
        {
            if (audio == null)
                return null;

            try
            {
                return _audioEngineField?.GetValue(audio);
            }
            catch
            {
                return null;
            }
        }

        private static object ResolveSourceVoiceDevice(object sourceVoice)
        {
            if (sourceVoice == null)
                return null;

            Type type = sourceVoice.GetType();
            if (!SourceVoiceDeviceFields.TryGetValue(type, out FieldInfo field))
            {
                field = type.GetField("device", InstanceMembers);
                SourceVoiceDeviceFields[type] = field;
            }

            try
            {
                return field?.GetValue(sourceVoice);
            }
            catch
            {
                return null;
            }
        }

        private static int ResolveSourceInputChannelCount(object sourceVoice)
        {
            object details = ResolveVoiceDetails(sourceVoice);
            if (details == null)
                return 2;

            try
            {
                FieldInfo field = details.GetType().GetField("InputChannelCount", InstanceMembers);
                if (field == null)
                    return 2;

                return Math.Max(1, Convert.ToInt32(field.GetValue(details), CultureInfo.InvariantCulture));
            }
            catch
            {
                return 2;
            }
        }

        private static object ResolveVoiceDetails(object voice)
        {
            if (voice == null)
                return null;

            Type type = voice.GetType();
            if (!SourceVoiceDetailsProperties.TryGetValue(type, out PropertyInfo property))
            {
                property = type.GetProperty("VoiceDetails", InstanceMembers);
                SourceVoiceDetailsProperties[type] = property;
            }

            try
            {
                return property?.GetValue(voice, null);
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo ResolveSourceSetEffectChainMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null || _effectDescriptorType == null)
                return null;

            if (SourceSetEffectChainMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            foreach (MethodInfo candidate in sourceVoiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(candidate.Name, "SetEffectChain", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsArray && parameters[0].ParameterType.GetElementType() == _effectDescriptorType)
                {
                    SourceSetEffectChainMethods[sourceVoiceType] = candidate;
                    return candidate;
                }
            }

            SourceSetEffectChainMethods[sourceVoiceType] = null;
            return null;
        }

        private static MethodInfo ResolveSourceSetEffectParametersMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null || _reverbParametersType == null)
                return null;

            if (SourceSetEffectParameterMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            foreach (MethodInfo candidate in sourceVoiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(candidate.Name, "SetEffectParameters", StringComparison.Ordinal) || !candidate.IsGenericMethodDefinition)
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int))
                {
                    method = candidate.MakeGenericMethod(_reverbParametersType);
                    SourceSetEffectParameterMethods[sourceVoiceType] = method;
                    return method;
                }
            }

            SourceSetEffectParameterMethods[sourceVoiceType] = null;
            return null;
        }

        private static MethodInfo ResolveSourceEnableEffectMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null)
                return null;

            if (SourceEnableEffectMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            method = ResolveSourceEffectToggleMethod(sourceVoiceType, "EnableEffect");
            SourceEnableEffectMethods[sourceVoiceType] = method;
            return method;
        }

        private static MethodInfo ResolveSourceDisableEffectMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null)
                return null;

            if (SourceDisableEffectMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            method = ResolveSourceEffectToggleMethod(sourceVoiceType, "DisableEffect");
            SourceDisableEffectMethods[sourceVoiceType] = method;
            return method;
        }

        private static MethodInfo ResolveSourceSetOutputVoicesMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null)
                return null;

            if (SourceSetOutputVoicesMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            foreach (MethodInfo candidate in sourceVoiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(candidate.Name, "SetOutputVoices", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsArray && parameters[0].ParameterType.GetElementType() == _voiceSendDescriptorType)
                {
                    SourceSetOutputVoicesMethods[sourceVoiceType] = candidate;
                    return candidate;
                }
            }

            SourceSetOutputVoicesMethods[sourceVoiceType] = null;
            return null;
        }

        private static MethodInfo ResolveSourceEffectToggleMethod(Type sourceVoiceType, string name)
        {
            foreach (MethodInfo candidate in sourceVoiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    return candidate;
            }

            return null;
        }

        private static void PurgeStaleSourceReverbTargets()
        {
            if (SourceReverbStates.Count == 0)
            {
                _lastSourceReverbSummary = "sourceReverb=0";
                return;
            }

            DateTime now = DateTime.UtcNow;
            List<IMySourceVoice> remove = null;
            foreach (KeyValuePair<IMySourceVoice, SourceReverbState> pair in SourceReverbStates)
            {
                IMySourceVoice voice = pair.Key;
                if (voice == null || !voice.IsValid || !voice.IsPlaying || now - pair.Value.LastTouchedUtc > TimeSpan.FromSeconds(2))
                {
                    if (remove == null)
                        remove = new List<IMySourceVoice>();
                    remove.Add(voice);
                }
            }

            if (remove == null)
            {
                UpdateSourceReverbSummary("active");
                return;
            }

            for (int i = 0; i < remove.Count; i++)
                ClearSourceVoiceTarget(remove[i], "stale");
        }

        private static void ClearSourceReverbTargets(string reason)
        {
            if (SourceReverbStates.Count == 0)
            {
                _lastSourceReverbSummary = "sourceReverb=0 buses=" + SourceReverbBuses.Count.ToString(CultureInfo.InvariantCulture);
                return;
            }

            List<IMySourceVoice> voices = new List<IMySourceVoice>(SourceReverbStates.Keys);
            for (int i = 0; i < voices.Count; i++)
                ClearSourceVoiceTarget(voices[i], reason);
        }

        private static void ClearSourceVoiceReverb(IMySourceVoice voice, SourceReverbState state, string reason)
        {
            object sourceVoice = state?.NativeSourceVoice;
            if (sourceVoice == null)
                return;

            if (state.ReverbBusVoice != null)
            {
                try
                {
                    MethodInfo setOutputVoices = ResolveSourceSetOutputVoicesMethod(sourceVoice.GetType());
                    if (setOutputVoices != null && state.OriginalOutputVoices != null)
                    {
                        setOutputVoices.Invoke(sourceVoice, new[] { state.OriginalOutputVoices });
                        V2DebugLog.WriteEvent("source-reverb", "bus-route-restored " + reason);
                    }
                }
                catch (Exception ex)
                {
                    V2DebugLog.WriteEvent("source-reverb", "bus-clear-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
                }

                return;
            }

            bool chainCleared = false;
            try
            {
                ResolveSourceDisableEffectMethod(sourceVoice.GetType())?.Invoke(sourceVoice, new object[] { SourceReverbEffectIndex });
            }
            catch
            {
            }

            try
            {
                MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(sourceVoice.GetType());
                setEffectChain?.Invoke(sourceVoice, new object[] { null });
                chainCleared = setEffectChain != null;
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("source-reverb", "clear-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
            }

            if (chainCleared || voice == null || !voice.IsValid)
                V2DebugLog.WriteEvent("source-reverb", "source direct chain cleared " + reason);
        }

        private static void ClearSourceReverbBuses(string reason)
        {
            if (SourceReverbBuses.Count == 0)
                return;

            List<SourceReverbBusState> buses = new List<SourceReverbBusState>(SourceReverbBuses.Values);
            SourceReverbBuses.Clear();
            for (int i = 0; i < buses.Count; i++)
            {
                SourceReverbBusState bus = buses[i];
                if (bus == null || bus.BusVoice == null)
                    continue;

                try
                {
                    ResolveSourceDisableEffectMethod(bus.BusVoice.GetType())?.Invoke(bus.BusVoice, new object[] { SourceReverbEffectIndex });
                }
                catch
                {
                }

                try
                {
                    MethodInfo setEffectChain = ResolveSourceSetEffectChainMethod(bus.BusVoice.GetType());
                    setEffectChain?.Invoke(bus.BusVoice, new object[] { null });
                }
                catch (Exception ex)
                {
                    V2DebugLog.WriteEvent("source-reverb", "bus-chain-clear-failed " + reason + " " + ex.GetType().Name + ": " + ex.Message);
                }

                DisposeComObject(bus.ReverbEffect);
                DisposeComObject(bus.BusVoice);
            }

            UpdateSourceReverbSummary("clear buses " + reason);
        }

        private static string BuildSourceReverbSignature(RealisticSoundPlusSettings settings)
        {
            if (settings == null)
                return "default";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.000}:{1:0.000}:{2:0.000}:{3:0.000}:{4:0.000}:{5:0.000}:{6:0.000}:{7:0.000}:{8:0.0}:{9:0}:{10:0.000}",
                Clamp01(settings.GlobalReverbDiffusion),
                Clamp01(settings.GlobalReverbRoomSize),
                Clamp(settings.GlobalReverbWetSend, 0f, 4f),
                Clamp(settings.GlobalReverbDecaySeconds, 0.1f, 30f),
                Clamp(settings.GlobalReverbEarlyGainDb, -60f, 20f),
                Clamp(settings.GlobalReverbTailGainDb, -60f, 20f),
                Clamp(settings.GlobalReverbPredelayMs, 0f, 300f),
                Clamp(settings.GlobalReverbLateDelayMs, 0f, 85f),
                Clamp(settings.GlobalReverbDensity, 0f, 100f),
                Clamp(settings.GlobalReverbToneHz, 20f, 20000f),
                Clamp(settings.GlobalReverbHighFrequencyDb, -60f, 0f));
        }

        private static void UpdateSourceReverbSummary(string last)
        {
            _lastSourceReverbSummary = string.Format(
                CultureInfo.InvariantCulture,
                "sourceReverb={0} buses={1} {2}",
                SourceReverbStates.Count,
                SourceReverbBuses.Count,
                Trim(last ?? string.Empty, 40));
        }

        private static void LogSourceReverbStatus(string message, bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastSourceReverbLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastSourceReverbLogUtc = now;
            V2DebugLog.WriteEvent("source-reverb", message);
        }

        private static void DisposeComObject(object value)
        {
            try
            {
                (value as IDisposable)?.Dispose();
            }
            catch
            {
            }
        }

        private static void CaptureBaseReverbParameters(object gameVoice)
        {
            if (_capturedReverbParameters || gameVoice == null)
                return;

            object parameters;
            if (TryGetEffectParameters(gameVoice, out parameters))
            {
                _baseReverbParameters = parameters;
                _capturedReverbParameters = true;
            }
        }

        private static void RestoreBaseReverbParameters(object audio)
        {
            if (!_capturedReverbParameters || _baseReverbParameters == null)
                return;

            object gameVoice = ResolveReverbEffectVoice(audio);
            if (gameVoice == null)
                return;

            MethodInfo setParameters = ResolveSetEffectParametersMethod(gameVoice.GetType());
            if (setParameters == null)
                return;

            try
            {
                setParameters.Invoke(gameVoice, new object[] { ReverbEffectIndex, _baseReverbParameters });
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[RealisticSoundPlus] Direct reverb parameter restore failed: " + ex);
            }
        }

        private static bool TryGetEffectParameters(object gameVoice, out object parameters)
        {
            parameters = null;
            if (gameVoice == null || _reverbParametersType == null)
                return false;

            MethodInfo getParameters = ResolveGetEffectParametersMethod(gameVoice.GetType());
            if (getParameters == null)
                return false;

            try
            {
                parameters = getParameters.Invoke(gameVoice, new object[] { ReverbEffectIndex });
                return parameters != null;
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo ResolveSetEffectParametersMethod(Type voiceType)
        {
            if (_setEffectParametersMethod != null)
                return _setEffectParametersMethod;

            if (voiceType == null || _reverbParametersType == null)
                return null;

            foreach (MethodInfo method in voiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(method.Name, "SetEffectParameters", StringComparison.Ordinal) || !method.IsGenericMethodDefinition)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int))
                {
                    _setEffectParametersMethod = method.MakeGenericMethod(_reverbParametersType);
                    return _setEffectParametersMethod;
                }
            }

            return null;
        }

        private static MethodInfo ResolveGetEffectParametersMethod(Type voiceType)
        {
            if (_getEffectParametersMethod != null)
                return _getEffectParametersMethod;

            if (voiceType == null || _reverbParametersType == null)
                return null;

            foreach (MethodInfo method in voiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(method.Name, "GetEffectParameters", StringComparison.Ordinal) || !method.IsGenericMethodDefinition)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                {
                    _getEffectParametersMethod = method.MakeGenericMethod(_reverbParametersType);
                    return _getEffectParametersMethod;
                }
            }

            return null;
        }

        private static object CreateReverbParameters(float diffusion, float roomSize)
        {
            return CreateReverbParameters(diffusion, roomSize, false);
        }

        private static object CreateReverbParameters(float diffusion, float roomSize, bool wetOnly)
        {
            RealisticSoundPlusSettings settings = new RealisticSoundPlusSettings
            {
                GlobalReverbDiffusion = diffusion,
                GlobalReverbRoomSize = roomSize,
                GlobalReverbDecaySeconds = 0.35f + Clamp01(roomSize) * 5.65f,
                GlobalReverbEarlyGainDb = -3f + Clamp01(roomSize) * 8f,
                GlobalReverbTailGainDb = -2f + Clamp01(roomSize) * 10f,
                GlobalReverbPredelayMs = 5f + Clamp01(roomSize) * 75f,
                GlobalReverbLateDelayMs = 5f + Clamp01(roomSize) * 70f,
                GlobalReverbDensity = Math.Max(10f, Clamp01(diffusion) * 100f),
                GlobalReverbToneHz = 5000f,
                GlobalReverbHighFrequencyDb = 0f
            };
            return CreateReverbParameters(settings, wetOnly);
        }

        private static object CreateReverbParameters(RealisticSoundPlusSettings settings, bool wetOnly)
        {
            float diffusion = Clamp01(settings?.GlobalReverbDiffusion ?? 0.5f);
            float roomSize = Clamp01(settings?.GlobalReverbRoomSize ?? 0.8f);
            object parameters = CreateDefaultReverbParameters();
            byte diffusionByte = ToByte(diffusion * 15f, 0, 15);
            float wetDryMix = wetOnly ? 100f : 30f + roomSize * 40f;

            SetMember(parameters, "WetDryMix", wetDryMix);
            SetMember(parameters, "EarlyDiffusion", diffusionByte);
            SetMember(parameters, "LateDiffusion", diffusionByte);
            SetMember(parameters, "Density", Clamp(settings?.GlobalReverbDensity ?? Math.Max(10f, diffusion * 100f), 0f, 100f));
            SetMember(parameters, "RoomSize", ToNativeRoomSize(roomSize));
            SetMember(parameters, "DecayTime", Clamp(settings?.GlobalReverbDecaySeconds ?? (0.35f + roomSize * 5.65f), 0.1f, 30f));
            SetMember(parameters, "ReflectionsDelay", (int)Math.Round(Clamp(settings?.GlobalReverbPredelayMs ?? (5f + roomSize * 75f), 0f, 300f)));
            SetMember(parameters, "ReverbDelay", ToByte(Clamp(settings?.GlobalReverbLateDelayMs ?? (5f + roomSize * 70f), 0f, 85f), 0, 85));
            float tailGain = ClampReverbGain(settings?.GlobalReverbTailGainDb ?? (-2f + roomSize * 10f));
            SetMember(parameters, "ReflectionsGain", ClampEarlyReverbGain(settings?.GlobalReverbEarlyGainDb ?? (-3f + roomSize * 8f), tailGain));
            SetMember(parameters, "ReverbGain", tailGain);
            SetMember(parameters, "RoomFilterFreq", Clamp(settings?.GlobalReverbToneHz ?? 5000f, 20f, 20000f));
            SetMember(parameters, "RoomFilterMain", 0f);
            SetMember(parameters, "RoomFilterHF", Clamp(settings?.GlobalReverbHighFrequencyDb ?? 0f, -60f, 0f));
            SetMember(parameters, "LowEQGain", (byte)8);
            SetMember(parameters, "LowEQCutoff", (byte)4);
            SetMember(parameters, "HighEQGain", (byte)0);
            SetMember(parameters, "HighEQCutoff", (byte)14);
            byte sourcePosition = ToByte(18f + roomSize * 12f, 0, 30);
            SetMember(parameters, "PositionLeft", sourcePosition);
            SetMember(parameters, "PositionRight", sourcePosition);
            SetMember(parameters, "PositionMatrixLeft", (byte)30);
            SetMember(parameters, "PositionMatrixRight", (byte)30);
            SetMember(parameters, "RearDelay", (byte)5);
            SetMember(parameters, "SideDelay", (byte)5);
            return parameters;
        }

        private static object CreateDefaultReverbParameters()
        {
            if (_reverbParametersType == null)
                return null;

            try
            {
                FieldInfo defaultField = _reverbParametersType.GetField("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (defaultField != null)
                {
                    object value = defaultField.GetValue(null);
                    if (value != null)
                        return value;
                }

                PropertyInfo defaultProperty = _reverbParametersType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (defaultProperty != null && defaultProperty.GetGetMethod(true) != null)
                {
                    object value = defaultProperty.GetValue(null, null);
                    if (value != null)
                        return value;
                }
            }
            catch
            {
            }

            return Activator.CreateInstance(_reverbParametersType);
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null || _reverbParametersType == null)
                return;

            FieldInfo field = _reverbParametersType.GetField(name, InstanceMembers);
            if (field != null)
            {
                field.SetValue(target, CoerceValue(value, field.FieldType));
                return;
            }

            PropertyInfo property = _reverbParametersType.GetProperty(name, InstanceMembers);
            if (property != null && property.GetSetMethod(true) != null)
                property.SetValue(target, CoerceValue(value, property.PropertyType), null);
        }

        private static object CoerceValue(object value, Type targetType)
        {
            if (targetType == null || value == null)
                return value;

            Type nullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nullableType.IsInstanceOfType(value))
                return value;

            if (nullableType.IsEnum)
                return Enum.ToObject(nullableType, value);

            return Convert.ChangeType(value, nullableType, CultureInfo.InvariantCulture);
        }

        private static string DescribeReverbParameters(object parameters)
        {
            if (parameters == null)
                return "readback-missing";

            return string.Format(
                CultureInfo.InvariantCulture,
                "wet={0:0} decay={1:0.00}s room={2:0} dens={3:0} diff={4}/{5} delay={6}/{7} gain={8:0.0}/{9:0.0} pos={10:0}/{11:0} hiEq={12:0}/{13:0}",
                GetFloatField(parameters, "WetDryMix"),
                GetFloatField(parameters, "DecayTime"),
                GetFloatField(parameters, "RoomSize"),
                GetFloatField(parameters, "Density"),
                GetByteField(parameters, "EarlyDiffusion"),
                GetByteField(parameters, "LateDiffusion"),
                GetIntField(parameters, "ReflectionsDelay"),
                GetByteField(parameters, "ReverbDelay"),
                GetFloatField(parameters, "ReflectionsGain"),
                GetFloatField(parameters, "ReverbGain"),
                GetByteField(parameters, "PositionLeft"),
                GetByteField(parameters, "PositionMatrixLeft"),
                GetByteField(parameters, "HighEQGain"),
                GetByteField(parameters, "HighEQCutoff"));
        }

        private static string DescribeGameVoiceEffect(object audio)
        {
            object gameVoice = ResolveReverbEffectVoice(audio);
            if (gameVoice == null)
                return "effect=gameVoice-missing";

            MethodInfo isEnabled = ResolveIsEffectEnabledMethod(gameVoice.GetType());
            if (isEnabled == null)
                return "effect=readback-missing";

            try
            {
                Type rawBoolType = ResolveType("SharpDX.Mathematics.Interop.RawBool");
                object enabled = rawBoolType != null ? Activator.CreateInstance(rawBoolType) : null;
                object[] args = new object[] { ReverbEffectIndex, enabled };
                isEnabled.Invoke(gameVoice, args);
                bool value = ConvertRawBool(args[1]);
                return value ? "effect=enabled" : "effect=disabled";
            }
            catch (Exception ex)
            {
                return "effect=failed:" + ex.GetType().Name;
            }
        }

        private static MethodInfo ResolveIsEffectEnabledMethod(Type voiceType)
        {
            if (_isEffectEnabledMethod != null)
                return _isEffectEnabledMethod;

            if (voiceType == null)
                return null;

            foreach (MethodInfo method in voiceType.GetMethods(InstanceMembers))
            {
                if (!string.Equals(method.Name, "IsEffectEnabled", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType.IsByRef)
                {
                    _isEffectEnabledMethod = method;
                    return method;
                }
            }

            return null;
        }

        private static void RefreshObservedVoices(object audio)
        {
            object gameVoice = _baseGameAudioVoice ?? _gameAudioVoiceField?.GetValue(audio);
            object hudVoice = _hudAudioVoiceField?.GetValue(audio);
            object musicVoice = _musicAudioVoiceField?.GetValue(audio);
            if (MyAudio.Static == null)
            {
                _lastObservedSummary = "voices=0";
                _lastAffectedVoices = "No live game-audio voices observed yet.";
                return;
            }

            MyPlayedSounds played = MyAudio.Static.GetCurrentlyPlayedSounds();
            List<IMySourceVoice> voices = played.Sound;
            if (voices == null || voices.Count == 0)
            {
                _lastObservedSummary = "voices=0";
                _lastAffectedVoices = "No live game-audio voices observed yet.";
                return;
            }

            int game = 0;
            int hud = 0;
            int music = 0;
            int other = 0;
            int affected = 0;
            StringBuilder builder = new StringBuilder();
            builder.Append("route cat    cue                       vol  mult");

            for (int i = 0; i < voices.Count; i++)
            {
                IMySourceVoice voice = voices[i];
                if (voice == null || !voice.IsValid || !voice.IsPlaying)
                    continue;

                string route = ResolveOutputRoute(voice, gameVoice, hudVoice, musicVoice);
                if (string.Equals(route, "game", StringComparison.OrdinalIgnoreCase))
                {
                    game++;
                    if (affected < 8)
                    {
                        string cueName = voice.CueEnum.ToString();
                        builder.AppendLine();
                        builder.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "{0,-5} {1,-6} {2,-25} {3:0.00} {4:0.00}",
                            route,
                            ClassifyCue(cueName),
                            Trim(cueName, 25),
                            voice.Volume,
                            voice.VolumeMultiplier);
                    }

                    affected++;
                }
                else if (string.Equals(route, "hud", StringComparison.OrdinalIgnoreCase))
                {
                    hud++;
                }
                else if (string.Equals(route, "music", StringComparison.OrdinalIgnoreCase))
                {
                    music++;
                }
                else
                {
                    other++;
                }
            }

            _lastObservedSummary = string.Format(CultureInfo.InvariantCulture, "voices game={0} hud={1} music={2} other={3}", game, hud, music, other);
            _lastAffectedVoices = affected == 0
                ? "No live game-audio voices observed yet. HUD/music routes are not on this reverb submix."
                : builder.ToString();
        }

        private static string ResolveOutputRoute(IMySourceVoice voice, object gameVoice, object hudVoice, object musicVoice)
        {
            object[] descriptors = ResolveOutputVoiceDescriptors(voice);
            return ResolveOutputRoute(descriptors, gameVoice, hudVoice, musicVoice);
        }

        private static string ResolveOutputRoute(object[] descriptors, object gameVoice, object hudVoice, object musicVoice)
        {
            if (descriptors == null || descriptors.Length == 0)
                return "unknown";

            bool sawOutput = false;
            for (int i = 0; i < descriptors.Length; i++)
            {
                object outputVoice = ResolveOutputVoice(descriptors[i]);
                if (outputVoice == null)
                    continue;

                sawOutput = true;
                if (ReferenceEquals(outputVoice, gameVoice))
                    return "game";
                if (ReferenceEquals(outputVoice, hudVoice))
                    return "hud";
                if (ReferenceEquals(outputVoice, musicVoice))
                    return "music";
            }

            return sawOutput ? "other" : "unknown";
        }

        private static object[] ResolveOutputVoiceDescriptors(IMySourceVoice voice)
        {
            if (voice == null)
                return null;

            if (_currentOutputVoicesProperty == null)
                _currentOutputVoicesProperty = voice.GetType().GetProperty("CurrentOutputVoices", InstanceMembers);

            if (_currentOutputVoicesProperty == null)
                return null;

            try
            {
                IEnumerable raw = _currentOutputVoicesProperty.GetValue(voice, null) as IEnumerable;
                if (raw == null)
                    return null;

                List<object> result = new List<object>();
                foreach (object descriptor in raw)
                    result.Add(descriptor);

                return result.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static object[] ResolveOutputVoiceDescriptorsFromVoiceObject(object voice)
        {
            if (voice == null)
                return null;

            try
            {
                PropertyInfo property = voice.GetType().GetProperty("CurrentOutputVoices", InstanceMembers);
                IEnumerable raw = property?.GetValue(voice, null) as IEnumerable;
                if (raw == null)
                    return null;

                List<object> result = new List<object>();
                foreach (object descriptor in raw)
                    result.Add(descriptor);

                return result.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static object ResolveOutputVoice(object descriptor)
        {
            if (descriptor == null)
                return null;

            Type type = descriptor.GetType();
            if (_voiceSendOutputVoiceField == null || _voiceSendOutputVoiceField.DeclaringType != type)
                _voiceSendOutputVoiceField = type.GetField("OutputVoice", InstanceMembers);

            try
            {
                return _voiceSendOutputVoiceField?.GetValue(descriptor);
            }
            catch
            {
                return null;
            }
        }

        private static string ClassifyCue(string cueName)
        {
            if (V2AuxCueClassifier.IsEngineCue(cueName))
                return "engine";
            if (V2AuxCueClassifier.IsEnvironmentCue(cueName))
                return "env";
            if (V2AuxCueClassifier.IsControllableActionCue(cueName))
                return "action";
            if (V2AuxCueClassifier.IsPlayerLocalCue(cueName))
                return "local";
            if (V2AuxCueClassifier.IsKnownBlockCue(cueName) || V2AuxCueClassifier.IsKnownBlockCueButNeedsPhysicalSource(cueName))
                return "block";
            if (V2AuxCueClassifier.IsNonWorldCue(cueName))
                return "nonw";
            return "world";
        }

        private static string FormatCapabilityStatus()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "audio={0} apply={1} enable={2} params={3}",
                _audioType == null ? "?" : _audioType.FullName,
                _applyReverbProperty == null ? "missing" : _applyReverbProperty.Name,
                _enableReverbProperty == null ? "missing" : _enableReverbProperty.Name,
                _setReverbParametersMethod == null ? "missing" : _setReverbParametersMethod.Name);
        }

        private static bool IsNoOp(MethodInfo method)
        {
            byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
            return il != null && il.Length == 1 && il[0] == 0x2A;
        }

        private static Type ResolveType(string fullName)
        {
            Type type = Type.GetType(fullName + ", SharpDX.XAudio2", false)
                ?? Type.GetType(fullName + ", SharpDX", false);
            if (type != null)
                return type;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool ConvertRawBool(object value)
        {
            if (value == null)
                return false;

            try
            {
                return (bool)value;
            }
            catch
            {
            }

            FieldInfo field = value.GetType().GetField("Value", InstanceMembers);
            if (field == null)
                return false;

            try
            {
                return Convert.ToInt32(field.GetValue(value), CultureInfo.InvariantCulture) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static float GetFloatField(object target, string name)
        {
            object value = GetFieldValue(target, name);
            return value == null ? 0f : Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static int GetIntField(object target, string name)
        {
            object value = GetFieldValue(target, name);
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static byte GetByteField(object target, string name)
        {
            object value = GetFieldValue(target, name);
            return value == null ? (byte)0 : Convert.ToByte(value, CultureInfo.InvariantCulture);
        }

        private static object GetFieldValue(object target, string name)
        {
            if (target == null)
                return null;

            FieldInfo field = target.GetType().GetField(name, InstanceMembers);
            if (field != null)
                return field.GetValue(target);

            PropertyInfo property = target.GetType().GetProperty(name, InstanceMembers);
            if (property == null || property.GetGetMethod(true) == null)
                return null;

            return property.GetValue(target, null);
        }

        private static byte ToByte(float value, int min, int max)
        {
            int rounded = (int)Math.Round(value);
            if (rounded < min)
                rounded = min;
            if (rounded > max)
                rounded = max;
            return (byte)rounded;
        }

        private static float ToNativeRoomSize(float roomSize)
        {
            return 1f + Clamp01(roomSize) * 99f;
        }

        private static float ClampReverbGain(float value)
        {
            float clamped = Clamp(value, -60f, 20f);
            return clamped <= -60f ? -59f : clamped;
        }

        private static float ClampEarlyReverbGain(float value, float tailGain)
        {
            float clamped = ClampReverbGain(value);
            return tailGain > -59f && clamped < MinLateFieldSeedDb ? MinLateFieldSeedDb : clamped;
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;

            return value.Substring(0, Math.Max(0, max - 1)) + "~";
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
                return 0f;
            return value > 1f ? 1f : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value <= min)
                return min;

            return value >= max ? max : value;
        }

        private sealed class SourceReverbState
        {
            public object NativeSourceVoice;
            public object OriginalOutputVoices;
            public string ReverbBusKey;
            public object ReverbBusVoice;
            public string Signature;
            public string Category;
            public string CueName;
            public bool ForceRouted;
            public DateTime LastTouchedUtc;
        }

        private sealed class SourceReverbBusState
        {
            public string Key;
            public object BusVoice;
            public object ReverbEffect;
            public string Signature;
            public int Channels;
            public int SampleRate;
            public DateTime LastTouchedUtc;
        }

        private sealed class GlobalBusSourceRouteState
        {
            public object NativeSourceVoice;
            public object OriginalOutputVoices;
            public string CueName;
            public DateTime LastTouchedUtc;
        }

    }
}
