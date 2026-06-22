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

        private static Type _audioType;
        private static Type _reverbParametersType;
        private static PropertyInfo _applyReverbProperty;
        private static PropertyInfo _enableReverbProperty;
        private static PropertyInfo _currentOutputVoicesProperty;
        private static MethodInfo _setReverbParametersMethod;
        private static MethodInfo _setEffectParametersMethod;
        private static MethodInfo _getEffectParametersMethod;
        private static MethodInfo _isEffectEnabledMethod;
        private static FieldInfo _gameAudioVoiceField;
        private static FieldInfo _hudAudioVoiceField;
        private static FieldInfo _musicAudioVoiceField;
        private static FieldInfo _voiceSendOutputVoiceField;
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
        private static float _lastDiffusion = -1f;
        private static float _lastRoomSize = -1f;
        private static object _baseReverbParameters;
        private static string _lastStatus = "not initialized";
        private static string _lastParameterStatus = "direct=disabled-stability";
        private static string _lastEffectStatus = "effect=?";
        private static string _lastObservedSummary = "voices=?";
        private static string _lastAffectedVoices = "No live game-audio voices observed yet.";

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
            bool parameterChanged = Math.Abs(diffusion - _lastDiffusion) > 0.0005f || Math.Abs(roomSize - _lastRoomSize) > 0.0005f;
            bool enabling = !_lastEnabled || !_lastAppliedEnabled;

            try
            {
                if (enabling)
                {
                    SetBool(audio, _enableReverbProperty, true);
                    SetBool(audio, _applyReverbProperty, true);
                }

                if (enabling || parameterChanged)
                {
                    _setReverbParametersMethod.Invoke(audio, new object[] { diffusion, roomSize });
                    TryApplyDirectReverbParameters(audio, diffusion, roomSize, out _lastParameterStatus);
                    _lastDiffusion = diffusion;
                    _lastRoomSize = roomSize;
                }

                _lastEnabled = true;
                _lastAppliedEnabled = true;
                _lastEffectStatus = DescribeGameVoiceEffect(audio);
                RefreshObservedVoices(audio);
                _lastStatus = string.Format(CultureInfo.InvariantCulture, "on diff={0:0.00} room={1:0.00}", diffusion, roomSize);

                if (enabling)
                    V2DebugLog.WriteEvent("global-reverb", _lastStatus + " " + _lastParameterStatus + " " + _lastObservedSummary);
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
                RestoreBaseReverbParameters(audio);

                if (_capturedApplyReverb && _applyReverbProperty != null)
                    SetBool(audio, _applyReverbProperty, _baseApplyReverb);
                if (_capturedEnableReverb && _enableReverbProperty != null)
                    SetBool(audio, _enableReverbProperty, _baseEnableReverb);

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

        public static void ResetRuntimeState()
        {
            RestoreVanillaState("runtime reset");
            _audioType = null;
            _reverbParametersType = null;
            _applyReverbProperty = null;
            _enableReverbProperty = null;
            _currentOutputVoicesProperty = null;
            _setReverbParametersMethod = null;
            _setEffectParametersMethod = null;
            _getEffectParametersMethod = null;
            _isEffectEnabledMethod = null;
            _gameAudioVoiceField = null;
            _hudAudioVoiceField = null;
            _musicAudioVoiceField = null;
            _voiceSendOutputVoiceField = null;
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
            _lastDiffusion = -1f;
            _lastRoomSize = -1f;
            _baseReverbParameters = null;
            _lastStatus = "reset";
            _lastParameterStatus = "direct=disabled-stability";
            _lastEffectStatus = "effect=?";
            _lastObservedSummary = "voices=?";
            _lastAffectedVoices = "No live game-audio voices observed yet.";
        }

        public static string FormatStatus()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} | api={1} apply={2} enable={3} wrapperParams={4} {5} {6} {7}",
                _lastStatus,
                _audioType == null ? "?" : _audioType.Name,
                _applyReverbProperty == null ? "missing" : "ok",
                _enableReverbProperty == null ? "missing" : "ok",
                _setReverbParametersMethod == null ? "missing" : (_wrapperParametersNoOp ? "noop" : "ok"),
                _lastParameterStatus,
                _lastEffectStatus,
                _lastObservedSummary);
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
            _hudAudioVoiceField = type.GetField("m_hudAudioVoice", InstanceMembers);
            _musicAudioVoiceField = type.GetField("m_musicAudioVoice", InstanceMembers);
            _reverbParametersType = ResolveType("SharpDX.XAudio2.Fx.ReverbParameters");
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

        private static bool TryApplyDirectReverbParameters(object audio, float diffusion, float roomSize, out string status)
        {
            status = "direct=unavailable";
            if (audio == null)
                return false;

            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
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
                object parameters = CreateReverbParameters(diffusion, roomSize);
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
                status = "direct=failed:" + inner.GetType().Name;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Direct reverb parameter apply failed: " + inner);
                V2DebugLog.WriteEvent("global-reverb-direct", status + " " + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                status = "direct=failed:" + ex.GetType().Name;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Direct reverb parameter apply failed: " + ex);
                V2DebugLog.WriteEvent("global-reverb-direct", status + " " + ex.Message);
                return false;
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

            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
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
            diffusion = Clamp01(diffusion);
            roomSize = Clamp01(roomSize);
            object parameters = Activator.CreateInstance(_reverbParametersType);
            byte diffusionByte = ToByte(diffusion * 15f, 0, 15);

            SetField(parameters, "WetDryMix", 100f);
            SetField(parameters, "EarlyDiffusion", diffusionByte);
            SetField(parameters, "LateDiffusion", diffusionByte);
            SetField(parameters, "Density", Math.Max(10f, diffusion * 100f));
            SetField(parameters, "RoomSize", roomSize * 100f);
            SetField(parameters, "DecayTime", 0.35f + roomSize * 5.65f);
            SetField(parameters, "ReflectionsDelay", (int)Math.Round(5f + roomSize * 75f));
            SetField(parameters, "ReverbDelay", ToByte(5f + roomSize * 70f, 0, 85));
            SetField(parameters, "ReflectionsGain", -3f + roomSize * 8f);
            SetField(parameters, "ReverbGain", -2f + roomSize * 10f);
            SetField(parameters, "RoomFilterFreq", 5000f);
            SetField(parameters, "RoomFilterMain", 0f);
            SetField(parameters, "RoomFilterHF", 0f);
            SetField(parameters, "LowEQGain", (byte)8);
            SetField(parameters, "LowEQCutoff", (byte)4);
            SetField(parameters, "HighEQGain", (byte)8);
            SetField(parameters, "HighEQCutoff", (byte)4);
            SetField(parameters, "PositionLeft", (byte)6);
            SetField(parameters, "PositionRight", (byte)6);
            SetField(parameters, "PositionMatrixLeft", (byte)27);
            SetField(parameters, "PositionMatrixRight", (byte)27);
            SetField(parameters, "RearDelay", (byte)5);
            SetField(parameters, "SideDelay", (byte)5);
            return parameters;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = _reverbParametersType?.GetField(name, InstanceMembers);
            if (field == null)
                return;

            field.SetValue(target, value);
        }

        private static string DescribeReverbParameters(object parameters)
        {
            if (parameters == null)
                return "readback-missing";

            return string.Format(
                CultureInfo.InvariantCulture,
                "wet={0:0} decay={1:0.00}s room={2:0} dens={3:0} diff={4}/{5} delay={6}/{7} gain={8:0.0}/{9:0.0}",
                GetFloatField(parameters, "WetDryMix"),
                GetFloatField(parameters, "DecayTime"),
                GetFloatField(parameters, "RoomSize"),
                GetFloatField(parameters, "Density"),
                GetByteField(parameters, "EarlyDiffusion"),
                GetByteField(parameters, "LateDiffusion"),
                GetIntField(parameters, "ReflectionsDelay"),
                GetByteField(parameters, "ReverbDelay"),
                GetFloatField(parameters, "ReflectionsGain"),
                GetFloatField(parameters, "ReverbGain"));
        }

        private static string DescribeGameVoiceEffect(object audio)
        {
            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
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
            object gameVoice = _gameAudioVoiceField?.GetValue(audio);
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
            if (field == null)
                return null;

            return field.GetValue(target);
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
    }
}
