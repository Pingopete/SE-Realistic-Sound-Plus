using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.Utils;
using Sandbox.Game.Entities;
using RealisticSoundPlus.Patches;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class RspDynamicAudioFilters
    {
        public const string EngineFilterSubtype = "RSPEngineFilter";
        public const string AuxFilterSubtype = "RSPAuxFilter";
        public const string Filter1Subtype = EngineFilterSubtype;
        public const string Filter2Subtype = AuxFilterSubtype;
        public const float MinFilterFrequency = 5f;
        public const float MaxFilterFrequency = 8000f;
        public const float MinFilterQ = 0.1f;
        public const float MaxFilterQ = 10f;
        private const float DefaultXAudioFilterSampleRate = 44100f;
        private const float MaxXAudioFilterFrequency = 1f;
        private const float MaxXAudioOneOverQ = 1.5f;
        private static readonly TimeSpan LiveFilterDiagnosticInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan EmitterBindingLifetime = TimeSpan.FromSeconds(120);
        private const int MaxEmitterVoiceBindings = 1024;

        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type XAudioType = ResolveType("VRage.Audio.MyXAudio2");
        private static readonly FieldInfo XAudioInstanceField = XAudioType?.GetField("Instance", StaticMembers);
        private static readonly FieldInfo EffectIdField = typeof(MyAudioEffect).GetField("EffectId", InstanceMembers);
        private static readonly FieldInfo SoundsEffectsField = typeof(MyAudioEffect).GetField("SoundsEffects", InstanceMembers);
        private static readonly FieldInfo ResultEmitterIdxField = typeof(MyAudioEffect).GetField("ResultEmitterIdx", InstanceMembers);
        private static readonly Type SoundEffectType = typeof(MyAudioEffect).GetNestedType("SoundEffect", StaticMembers);

        private static readonly FieldInfo DurationField = SoundEffectType?.GetField("Duration", InstanceMembers);
        private static readonly FieldInfo VolumeCurveField = SoundEffectType?.GetField("VolumeCurve", InstanceMembers);
        private static readonly FieldInfo FilterField = SoundEffectType?.GetField("Filter", InstanceMembers);
        private static readonly FieldInfo FrequencyField = SoundEffectType?.GetField("Frequency", InstanceMembers);
        private static readonly FieldInfo OneOverQField = SoundEffectType?.GetField("OneOverQ", InstanceMembers);
        private static readonly FieldInfo StopAfterField = SoundEffectType?.GetField("StopAfter", InstanceMembers);
        private static readonly Type SharpFilterParametersType = ResolveType("SharpDX.XAudio2.FilterParameters");
        private static readonly Type SharpFilterType = ResolveType("SharpDX.XAudio2.FilterType");
        private static readonly FieldInfo SharpFilterTypeField = SharpFilterParametersType?.GetField("Type", InstanceMembers);
        private static readonly FieldInfo SharpFilterFrequencyField = SharpFilterParametersType?.GetField("Frequency", InstanceMembers);
        private static readonly FieldInfo SharpFilterOneOverQField = SharpFilterParametersType?.GetField("OneOverQ", InstanceMembers);

        private static readonly Dictionary<Type, PropertyInfo> SourceVoiceProperties = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, MethodInfo> SetFilterMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> GetFilterMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, PropertyInfo> VoiceDetailsProperties = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, FieldInfo> VoiceDetailsSampleRateFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> SoundDataSoundFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, MemberInfo> SourceVoiceEmitterMembers = new Dictionary<Type, MemberInfo>();
        private static readonly Dictionary<IMySourceVoice, EmitterVoiceBinding> EmitterVoiceBindings = new Dictionary<IMySourceVoice, EmitterVoiceBinding>();
        private static readonly HashSet<Type> SourceVoicePropertyMisses = new HashSet<Type>();
        private static readonly HashSet<Type> SetFilterMethodMisses = new HashSet<Type>();
        private static readonly HashSet<Type> GetFilterMethodMisses = new HashSet<Type>();
        private static readonly HashSet<Type> VoiceDetailsPropertyMisses = new HashSet<Type>();
        private static readonly HashSet<Type> VoiceDetailsSampleRateMisses = new HashSet<Type>();
        private static readonly HashSet<Type> SoundDataSoundFieldMisses = new HashSet<Type>();
        private static readonly HashSet<Type> SourceVoiceEmitterFieldMisses = new HashSet<Type>();
        private const float DefaultLiveFilterSmoothingMs = 45f;
        private const int MaxLiveFilterSmoothing = 512;
        private static readonly TimeSpan LiveFilterSmoothingResetGap = TimeSpan.FromMilliseconds(500);
        private static readonly Dictionary<IMySourceVoice, LiveFilterSmoothState> LiveFilterSmoothing = new Dictionary<IMySourceVoice, LiveFilterSmoothState>();
        private static long _liveFilterSmoothApplied;
        private static long _liveFilterSmoothSnaps;

        private static string _lastRegisteredSignature;
        private static string _lastLiveEffectSignature;
        private static string _lastLiveVoiceSignature;
        private static DateTime _lastLiveEffectLogUtc = DateTime.MinValue;
        private static DateTime _lastLiveVoiceLogUtc = DateTime.MinValue;
        private static int _suppressedLiveEffectLogs;
        private static int _suppressedLiveVoiceLogs;
        private static long _emitterResolveRegistered;
        private static long _emitterResolveDirect;
        private static long _emitterResolveNative;
        private static long _emitterResolveStale;
        private static long _emitterResolveMiss;
        private static bool _loggedNotReady;
        private static bool _loggedReflectionFailure;
        private static bool _loggedLiveFilterReflectionFailure;
        private static bool _disabled;

        public static bool UpdateFromSettings(RealisticSoundPlusSettings settings)
        {
            if (_disabled || settings == null)
                return false;

            if (!HasRequiredReflection())
            {
                LogReflectionFailure("missing required audio effect fields");
                return false;
            }

            string signature = BuildSettingsSignature(settings);
            if (string.Equals(_lastRegisteredSignature, signature, StringComparison.Ordinal))
                return true;

            if (!TryGetEffectDictionary(out IDictionary effects))
            {
                if (!_loggedNotReady)
                {
                    _loggedNotReady = true;
                    V2DebugLog.WriteEvent("filter-bank", "effect bank not ready");
                }

                return false;
            }

            try
            {
                RegisterOrReplace(effects, Filter1Subtype, settings.Filter1Type, settings.Filter1Frequency, settings.Filter1Q);
                RegisterOrReplace(effects, Filter2Subtype, settings.Filter2Type, settings.Filter2Frequency, settings.Filter2Q);
                _lastRegisteredSignature = signature;
                _loggedNotReady = false;
                V2DebugLog.WriteEvent("filter-register", DescribeSettings(settings));
                V2DebugLog.WriteEvent("filter-bank-entry", DescribeEffect(effects, Filter1Subtype) + " | " + DescribeEffect(effects, "realShipFilter"));
                return true;
            }
            catch (Exception ex)
            {
                LogReflectionFailure(ex.Message);
                return false;
            }
        }

        public static void ResetRuntimeState()
        {
            _lastRegisteredSignature = null;
            _lastLiveEffectSignature = null;
            _lastLiveVoiceSignature = null;
            _lastLiveEffectLogUtc = DateTime.MinValue;
            _lastLiveVoiceLogUtc = DateTime.MinValue;
            _suppressedLiveEffectLogs = 0;
            _suppressedLiveVoiceLogs = 0;
            _emitterResolveRegistered = 0L;
            _emitterResolveDirect = 0L;
            _emitterResolveNative = 0L;
            _emitterResolveStale = 0L;
            _emitterResolveMiss = 0L;
            _loggedNotReady = false;
            EmitterVoiceBindings.Clear();
            LiveFilterSmoothing.Clear();
            _liveFilterSmoothApplied = 0L;
            _liveFilterSmoothSnaps = 0L;
        }

        public static bool IsCustomFilterSubtype(string subtype)
        {
            return string.Equals(subtype, Filter1Subtype, StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtype, Filter2Subtype, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryApplyLiveCustomFilter(IMySourceVoice voice, string subtype, RealisticSoundPlusSettings settings)
        {
            return TryApplyLiveCustomFilter(voice, subtype, settings, null);
        }

        public static bool TryApplyLiveFilterParameters(IMySourceVoice voice, string filterType, float frequency, float q)
        {
            if (voice == null)
                return false;

            object sourceVoice = ResolveSourceVoice(voice);
            if (sourceVoice == null)
                return false;

            // Root-level de-zipper: smooth cutoff/Q toward target before the biquad write (see SmoothLiveFilter).
            SmoothLiveFilter(voice, ref frequency, ref q);

            try
            {
                // Fast path: SharpDX.XAudio2 is a compile-time reference, so the resolved native voice can be
                // cast and called directly. This avoids a MethodInfo.Invoke plus a per-call object[] allocation
                // and an int box on every voice every frame on the audio path. The reflection path below is kept
                // as a defensive fallback in case the runtime voice is ever an unexpected type.
                SharpDX.XAudio2.Voice sharpVoice = sourceVoice as SharpDX.XAudio2.Voice;
                if (sharpVoice != null)
                {
                    SharpDX.XAudio2.FilterParameters parameters = new SharpDX.XAudio2.FilterParameters
                    {
                        Type = ToSharpFilterTypeDirect(filterType),
                        Frequency = ToXAudioFrequency(frequency, ResolveSourceVoiceInputSampleRate(sourceVoice)),
                        OneOverQ = ToXAudioOneOverQ(q)
                    };
                    sharpVoice.SetFilterParameters(parameters, 0);
                    return true;
                }

                if (!HasLiveFilterReflection())
                {
                    LogLiveFilterReflectionFailure("missing SharpDX filter reflection");
                    return false;
                }

                MethodInfo setFilter = ResolveSetFilterMethod(sourceVoice.GetType());
                if (setFilter == null)
                    return false;

                object boxedParameters = CreateSharpFilterParameters(filterType, frequency, q, sourceVoice);
                setFilter.Invoke(sourceVoice, new[] { boxedParameters, 0 });
                return true;
            }
            catch (Exception ex)
            {
                LogLiveFilterReflectionFailure(ex.Message);
                return false;
            }
        }

        public static bool TryApplyLiveCustomFilter(IMySourceVoice voice, string subtype, RealisticSoundPlusSettings settings, MyEntity3DSoundEmitter emitter)
        {
            if (voice == null || settings == null || !IsCustomFilterSubtype(subtype))
                return false;

            GetFilterParametersForEmitter(subtype, settings, emitter, out string filterType, out float frequency, out float q);
            return TryApplyLiveFilterParameters(voice, filterType, frequency, q);
        }

        public static bool TryPrepareLiveCustomFilterEffect(object soundData, object soundEffect, RealisticSoundPlusSettings settings)
        {
            if (soundData == null || soundEffect == null || settings == null)
                return false;

            MyEntity3DSoundEmitter emitter = ResolveEmitterFromSoundData(soundData);
            if (emitter == null || !IsLiveCustomFilterTarget(emitter))
                return false;

            string subtype = AudioEngineV2Runtime.GetEngineFilterEffectSubtype(emitter);
            if (!IsCustomFilterSubtype(subtype))
                return false;

            GetFilterParametersForEmitter(subtype, settings, emitter, out string filterType, out float frequency, out float q);

            ConfigureFilter(soundEffect, filterType, ToXAudioFrequency(frequency), ToXAudioOneOverQ(q));
            LogLiveEffectApplied(subtype, filterType, frequency, q);
            return true;
        }

        public static bool TryApplyLiveCustomFilterFromSoundData(object soundData, RealisticSoundPlusSettings settings)
        {
            if (soundData == null || settings == null)
                return false;

            object sourceVoiceObject = ResolveSourceVoiceObjectFromSoundData(soundData);
            if (sourceVoiceObject == null)
                return false;

            MyEntity3DSoundEmitter emitter = ResolveEmitterFromSourceVoiceObject(sourceVoiceObject);
            if (emitter == null || !IsLiveCustomFilterTarget(emitter))
                return false;

            string subtype = AudioEngineV2Runtime.GetEngineFilterEffectSubtype(emitter);
            if (!IsCustomFilterSubtype(subtype))
                return false;

            IMySourceVoice voice = sourceVoiceObject as IMySourceVoice;
            if (voice == null)
                return false;

            bool applied = TryApplyLiveCustomFilter(voice, subtype, settings, emitter);
            if (applied)
            {
                GetFilterParametersForEmitter(subtype, settings, emitter, out string filterType, out float frequency, out float q);
                LogLiveVoiceApplied(voice, subtype, filterType, frequency, q);
            }

            return applied;
        }

        private static bool IsLiveCustomFilterTarget(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null
                && (AudioEngineV2Runtime.IsV2Emitter(emitter)
                    || ThrusterFilterPatch.IsEngineAudioEmitter(emitter));
        }

        public static bool TryResolveEmitter(IMySourceVoice voice, out MyEntity3DSoundEmitter emitter)
        {
            emitter = null;
            if (voice == null)
            {
                _emitterResolveMiss = SaturatingIncrement(_emitterResolveMiss);
                return false;
            }

            if (TryResolveRegisteredEmitter(voice, out emitter))
            {
                _emitterResolveRegistered = SaturatingIncrement(_emitterResolveRegistered);
                return true;
            }

            if (TryResolveEmitterFromObject(voice, out emitter))
            {
                if (EmitterOwnsVoice(emitter, voice))
                {
                    _emitterResolveDirect = SaturatingIncrement(_emitterResolveDirect);
                    return true;
                }

                _emitterResolveStale = SaturatingIncrement(_emitterResolveStale);
            }

            object sourceVoice = ResolveSourceVoice(voice);
            if (TryResolveEmitterFromObject(sourceVoice, out emitter))
            {
                if (EmitterOwnsVoice(emitter, voice))
                {
                    _emitterResolveNative = SaturatingIncrement(_emitterResolveNative);
                    return true;
                }

                _emitterResolveStale = SaturatingIncrement(_emitterResolveStale);
            }

            emitter = null;
            _emitterResolveMiss = SaturatingIncrement(_emitterResolveMiss);
            return false;
        }

        public static string FormatEmitterBindingSummary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "emitBind active={0} reg={1} direct={2} native={3} stale={4} miss={5}",
                EmitterVoiceBindings.Count,
                _emitterResolveRegistered,
                _emitterResolveDirect,
                _emitterResolveNative,
                _emitterResolveStale,
                _emitterResolveMiss);
        }

        public static void RecordEmitterVoiceBinding(MyEntity3DSoundEmitter emitter, IMySourceVoice voice)
        {
            if (emitter == null || voice == null)
                return;

            DateTime now = DateTime.UtcNow;
            if (EmitterVoiceBindings.Count > MaxEmitterVoiceBindings)
                PurgeEmitterVoiceBindings(now);

            if (EmitterVoiceBindings.Count > MaxEmitterVoiceBindings)
                EmitterVoiceBindings.Clear();

            EmitterVoiceBindings[voice] = new EmitterVoiceBinding
            {
                Emitter = emitter,
                UpdatedUtc = now
            };
        }

        private static bool TryResolveRegisteredEmitter(IMySourceVoice voice, out MyEntity3DSoundEmitter emitter)
        {
            emitter = null;
            if (voice == null)
                return false;

            DateTime now = DateTime.UtcNow;
            if (!EmitterVoiceBindings.TryGetValue(voice, out EmitterVoiceBinding binding))
                return false;

            if (binding.Emitter == null
                || now - binding.UpdatedUtc > EmitterBindingLifetime
                || !IsEmitterUsable(binding.Emitter)
                || !EmitterOwnsVoice(binding.Emitter, voice))
            {
                EmitterVoiceBindings.Remove(voice);
                _emitterResolveStale = SaturatingIncrement(_emitterResolveStale);
                return false;
            }

            emitter = binding.Emitter;
            return true;
        }

        private static void PurgeEmitterVoiceBindings(DateTime now)
        {
            if (EmitterVoiceBindings.Count == 0)
                return;

            List<IMySourceVoice> remove = null;
            foreach (KeyValuePair<IMySourceVoice, EmitterVoiceBinding> pair in EmitterVoiceBindings)
            {
                if (pair.Key != null
                    && pair.Key.IsValid
                    && pair.Value.Emitter != null
                    && IsEmitterUsable(pair.Value.Emitter)
                    && EmitterOwnsVoice(pair.Value.Emitter, pair.Key)
                    && now - pair.Value.UpdatedUtc <= EmitterBindingLifetime)
                {
                    continue;
                }

                _emitterResolveStale = SaturatingIncrement(_emitterResolveStale);
                if (remove == null)
                    remove = new List<IMySourceVoice>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                EmitterVoiceBindings.Remove(remove[i]);
        }

        private static bool EmitterOwnsVoice(MyEntity3DSoundEmitter emitter, IMySourceVoice voice)
        {
            if (emitter == null || voice == null)
                return false;

            try
            {
                return ReferenceEquals(emitter.Sound, voice)
                    || ReferenceEquals(emitter.SecondarySound, voice);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEmitterUsable(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            try
            {
                return emitter.Entity == null || !emitter.Entity.Closed;
            }
            catch
            {
                return true;
            }
        }

        public static bool TryResolveNativeSourceVoice(IMySourceVoice voice, out object sourceVoice)
        {
            sourceVoice = null;
            if (voice == null)
                return false;

            sourceVoice = ResolveSourceVoice(voice);
            return sourceVoice != null;
        }

        private static bool TryGetEffectDictionary(out IDictionary effects)
        {
            effects = null;

            object audio = XAudioInstanceField?.GetValue(null);
            if (audio == null)
                return false;

            FieldInfo bankField = audio.GetType().GetField("m_effectBank", InstanceMembers);
            object bank = bankField?.GetValue(audio);
            if (bank == null)
                return false;

            FieldInfo effectsField = bank.GetType().GetField("m_effects", InstanceMembers);
            effects = effectsField?.GetValue(bank) as IDictionary;
            return effects != null;
        }

        private static void RegisterOrReplace(IDictionary effects, string subtype, string filterType, float frequency, float q)
        {
            MyStringHash effectId = MyStringHash.GetOrCompute(subtype);
            string normalizedFilterType = NormalizeCustomFilterType(filterType);
            float sanitizedFrequency = SanitizeFrequency(frequency);
            float sanitizedQ = SanitizeQ(q);
            float xAudioFrequency = ToXAudioFrequency(sanitizedFrequency);
            float xAudioOneOverQ = ToXAudioOneOverQ(sanitizedQ);
            if (effects.Contains(effectId))
            {
                object existingEffect = effects[effectId];
                if (TryUpdateExistingEffect(existingEffect, normalizedFilterType, xAudioFrequency, xAudioOneOverQ))
                    return;
            }

            object effect = TryCreateFromTemplate(effects, effectId, normalizedFilterType, xAudioFrequency, xAudioOneOverQ)
                ?? CreateEffect(effectId, normalizedFilterType, xAudioFrequency, xAudioOneOverQ);
            effects[effectId] = effect;
        }

        private static bool TryUpdateExistingEffect(object effect, string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            if (effect == null)
                return false;

            object sounds = SoundsEffectsField.GetValue(effect);
            IEnumerable groups = sounds as IEnumerable;
            if (groups == null)
                return false;

            foreach (object group in groups)
            {
                IEnumerable effects = group as IEnumerable;
                if (effects == null)
                    continue;

                foreach (object soundEffect in effects)
                {
                    if (soundEffect == null)
                        continue;

                    ConfigureFilter(soundEffect, filterType, xAudioFrequency, xAudioOneOverQ);
                    return true;
                }
            }

            return false;
        }

        private static object TryCreateFromTemplate(IDictionary effects, MyStringHash effectId, string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            if (effects == null)
                return null;

            object template = null;
            MyStringHash realShipId = MyStringHash.GetOrCompute("realShipFilter");
            MyStringHash cockpitId = MyStringHash.GetOrCompute("LowPassCockpit");
            if (effects.Contains(realShipId))
                template = effects[realShipId];
            else if (effects.Contains(cockpitId))
                template = effects[cockpitId];

            if (template == null)
                return null;

            try
            {
                MyAudioEffect clone = new MyAudioEffect();
                EffectIdField.SetValue(clone, effectId);
                ResultEmitterIdxField.SetValue(clone, ResultEmitterIdxField.GetValue(template));
                SoundsEffectsField.SetValue(clone, CloneSoundEffectsList(SoundsEffectsField.GetValue(template), filterType, xAudioFrequency, xAudioOneOverQ));
                return clone;
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("filter-clone-failed", ex.Message);
                return null;
            }
        }

        private static object CreateEffect(MyStringHash effectId, string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            MyAudioEffect effect = new MyAudioEffect();
            EffectIdField.SetValue(effect, effectId);
            ResultEmitterIdxField.SetValue(effect, 0);
            SoundsEffectsField.SetValue(effect, CreateSoundEffectsList(filterType, xAudioFrequency, xAudioOneOverQ));
            return effect;
        }

        private static object CreateSoundEffectsList(string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            object soundEffect = Activator.CreateInstance(SoundEffectType);
            DurationField.SetValue(soundEffect, 0f);
            FilterField.SetValue(soundEffect, Enum.Parse(FilterField.FieldType, NormalizeCustomFilterType(filterType)));
            FrequencyField.SetValue(soundEffect, xAudioFrequency);
            OneOverQField.SetValue(soundEffect, xAudioOneOverQ);
            StopAfterField?.SetValue(soundEffect, false);

            Type innerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(SoundEffectType);
            IList innerList = (IList)Activator.CreateInstance(innerListType);
            innerList.Add(soundEffect);

            Type outerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(innerListType);
            IList outerList = (IList)Activator.CreateInstance(outerListType);
            outerList.Add(innerList);
            return outerList;
        }

        private static object CloneSoundEffectsList(object sourceList, string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            Type innerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(SoundEffectType);
            Type outerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(innerListType);
            IList outerList = (IList)Activator.CreateInstance(outerListType);
            bool replacedFirst = false;

            IEnumerable sourceOuter = sourceList as IEnumerable;
            if (sourceOuter != null)
            {
                foreach (object sourceInner in sourceOuter)
                {
                    IList innerList = (IList)Activator.CreateInstance(innerListType);
                    IEnumerable sourceEffects = sourceInner as IEnumerable;
                    if (sourceEffects != null)
                    {
                        foreach (object sourceEffect in sourceEffects)
                        {
                            object clonedEffect = CloneSoundEffect(sourceEffect);
                            if (!replacedFirst)
                            {
                                ConfigureFilter(clonedEffect, filterType, xAudioFrequency, xAudioOneOverQ);
                                replacedFirst = true;
                            }

                            innerList.Add(clonedEffect);
                        }
                    }

                    if (innerList.Count > 0)
                        outerList.Add(innerList);
                }
            }

            if (outerList.Count == 0)
                return CreateSoundEffectsList(filterType, xAudioFrequency, xAudioOneOverQ);

            if (!replacedFirst)
                ((IList)outerList[0]).Add(CreateFilterSoundEffect(filterType, xAudioFrequency, xAudioOneOverQ));

            return outerList;
        }

        private static object CloneSoundEffect(object sourceEffect)
        {
            object clonedEffect = Activator.CreateInstance(SoundEffectType);
            VolumeCurveField?.SetValue(clonedEffect, VolumeCurveField.GetValue(sourceEffect));
            DurationField.SetValue(clonedEffect, DurationField.GetValue(sourceEffect));
            FilterField.SetValue(clonedEffect, FilterField.GetValue(sourceEffect));
            FrequencyField.SetValue(clonedEffect, FrequencyField.GetValue(sourceEffect));
            OneOverQField.SetValue(clonedEffect, OneOverQField.GetValue(sourceEffect));
            StopAfterField?.SetValue(clonedEffect, StopAfterField.GetValue(sourceEffect));
            return clonedEffect;
        }

        private static object CreateFilterSoundEffect(string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            object soundEffect = Activator.CreateInstance(SoundEffectType);
            ConfigureFilter(soundEffect, filterType, xAudioFrequency, xAudioOneOverQ);
            return soundEffect;
        }

        private static void ConfigureFilter(object soundEffect, string filterType, float xAudioFrequency, float xAudioOneOverQ)
        {
            DurationField.SetValue(soundEffect, 0f);
            FilterField.SetValue(soundEffect, Enum.Parse(FilterField.FieldType, NormalizeCustomFilterType(filterType)));
            FrequencyField.SetValue(soundEffect, xAudioFrequency);
            OneOverQField.SetValue(soundEffect, xAudioOneOverQ);
            StopAfterField?.SetValue(soundEffect, false);
        }

        private static bool HasRequiredReflection()
        {
            return XAudioInstanceField != null
                && EffectIdField != null
                && SoundsEffectsField != null
                && ResultEmitterIdxField != null
                && SoundEffectType != null
                && DurationField != null
                && FilterField != null
                && FrequencyField != null
                && OneOverQField != null;
        }

        private static bool HasLiveFilterReflection()
        {
            return SharpFilterParametersType != null
                && SharpFilterTypeField != null
                && SharpFilterFrequencyField != null
                && SharpFilterOneOverQField != null
                && SharpFilterType != null;
        }

        private static object CreateSharpFilterParameters(string filterType, float frequency, float q, object sourceVoice)
        {
            object parameters = Activator.CreateInstance(SharpFilterParametersType);
            SharpFilterTypeField.SetValue(parameters, ToSharpFilterType(filterType));
            SharpFilterFrequencyField.SetValue(parameters, ToXAudioFrequency(frequency, ResolveSourceVoiceInputSampleRate(sourceVoice)));
            SharpFilterOneOverQField.SetValue(parameters, ToXAudioOneOverQ(q));
            return parameters;
        }

        private static object ResolveSourceVoice(IMySourceVoice voice)
        {
            Type voiceType = voice.GetType();
            if (SourceVoicePropertyMisses.Contains(voiceType))
                return null;

            if (!SourceVoiceProperties.TryGetValue(voiceType, out PropertyInfo property))
            {
                property = voiceType.GetProperty("Voice", InstanceMembers);
                if (property == null)
                {
                    SourceVoicePropertyMisses.Add(voiceType);
                    LogLiveFilterReflectionFailure("voice property missing on " + voiceType.FullName);
                    return null;
                }

                SourceVoiceProperties[voiceType] = property;
            }

            return property.GetValue(voice, null);
        }

        private static MyEntity3DSoundEmitter ResolveEmitterFromSoundData(object soundData)
        {
            object sourceVoice = ResolveSourceVoiceObjectFromSoundData(soundData);
            if (sourceVoice == null)
                return null;

            return ResolveEmitterFromSourceVoiceObject(sourceVoice);
        }

        private static object ResolveSourceVoiceObjectFromSoundData(object soundData)
        {
            FieldInfo soundField = ResolveSoundDataSoundField(soundData?.GetType());
            return soundField?.GetValue(soundData);
        }

        private static MyEntity3DSoundEmitter ResolveEmitterFromSourceVoiceObject(object sourceVoice)
        {
            return TryResolveEmitterFromObject(sourceVoice, out MyEntity3DSoundEmitter emitter)
                ? emitter
                : null;
        }

        private static FieldInfo ResolveSoundDataSoundField(Type soundDataType)
        {
            if (soundDataType == null || SoundDataSoundFieldMisses.Contains(soundDataType))
                return null;

            if (SoundDataSoundFields.TryGetValue(soundDataType, out FieldInfo field))
                return field;

            field = soundDataType.GetField("Sound", InstanceMembers);
            if (field == null)
            {
                SoundDataSoundFieldMisses.Add(soundDataType);
                LogLiveFilterReflectionFailure("SoundData.Sound missing on " + soundDataType.FullName);
                return null;
            }

            SoundDataSoundFields[soundDataType] = field;
            return field;
        }

        private static bool TryResolveEmitterFromObject(object sourceVoice, out MyEntity3DSoundEmitter emitter)
        {
            emitter = null;
            if (sourceVoice == null)
                return false;

            emitter = sourceVoice as MyEntity3DSoundEmitter;
            if (emitter != null)
                return true;

            MemberInfo member = ResolveSourceVoiceEmitterMember(sourceVoice.GetType());
            if (member == null)
                return false;

            try
            {
                object value = null;
                if (member is FieldInfo field)
                    value = field.GetValue(sourceVoice);
                else if (member is PropertyInfo property)
                    value = property.GetValue(sourceVoice, null);

                emitter = value as MyEntity3DSoundEmitter;
            }
            catch
            {
                emitter = null;
            }

            return emitter != null;
        }

        private static MemberInfo ResolveSourceVoiceEmitterMember(Type sourceVoiceType)
        {
            if (sourceVoiceType == null || SourceVoiceEmitterFieldMisses.Contains(sourceVoiceType))
                return null;

            if (SourceVoiceEmitterMembers.TryGetValue(sourceVoiceType, out MemberInfo member))
                return member;

            member = FindEmitterField(sourceVoiceType, "Emitter")
                ?? FindEmitterField(sourceVoiceType, "m_emitter")
                ?? FindEmitterField(sourceVoiceType, "SoundEmitter")
                ?? FindEmitterField(sourceVoiceType, "m_soundEmitter")
                ?? FindEmitterProperty(sourceVoiceType, "Emitter")
                ?? FindEmitterProperty(sourceVoiceType, "SoundEmitter")
                ?? FindFallbackEmitterMember(sourceVoiceType);
            if (member == null)
            {
                SourceVoiceEmitterFieldMisses.Add(sourceVoiceType);
                LogLiveFilterReflectionFailure("MySourceVoice emitter member missing on " + sourceVoiceType.FullName);
                return null;
            }

            SourceVoiceEmitterMembers[sourceVoiceType] = member;
            return member;
        }

        private static FieldInfo FindEmitterField(Type sourceVoiceType, string name)
        {
            FieldInfo field = sourceVoiceType.GetField(name, InstanceMembers);
            return field != null && CanHold3DEmitter(field.FieldType)
                ? field
                : null;
        }

        private static PropertyInfo FindEmitterProperty(Type sourceVoiceType, string name)
        {
            PropertyInfo property = sourceVoiceType.GetProperty(name, InstanceMembers);
            return property != null
                && property.GetIndexParameters().Length == 0
                && CanHold3DEmitter(property.PropertyType)
                    ? property
                    : null;
        }

        private static MemberInfo FindFallbackEmitterMember(Type sourceVoiceType)
        {
            FieldInfo[] fields = sourceVoiceType.GetFields(InstanceMembers);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!CanHold3DEmitter(field.FieldType))
                    continue;

                if (field.Name.IndexOf("emitter", StringComparison.OrdinalIgnoreCase) >= 0)
                    return field;
            }

            PropertyInfo[] properties = sourceVoiceType.GetProperties(InstanceMembers);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (property.GetIndexParameters().Length != 0)
                    continue;

                if (!CanHold3DEmitter(property.PropertyType))
                    continue;

                if (property.Name.IndexOf("emitter", StringComparison.OrdinalIgnoreCase) >= 0)
                    return property;
            }

            return null;
        }

        private static bool CanHold3DEmitter(Type type)
        {
            return type != null
                && (typeof(MyEntity3DSoundEmitter).IsAssignableFrom(type)
                    || typeof(IMy3DSoundEmitter).IsAssignableFrom(type));
        }

        private static MethodInfo ResolveSetFilterMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null || SetFilterMethodMisses.Contains(sourceVoiceType))
                return null;

            if (SetFilterMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            MethodInfo[] methods = sourceVoiceType.GetMethods(InstanceMembers);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo candidate = methods[i];
                if (!string.Equals(candidate.Name, "SetFilterParameters", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != 2)
                    continue;

                if (parameters[0].ParameterType == SharpFilterParametersType && parameters[1].ParameterType == typeof(int))
                {
                    SetFilterMethods[sourceVoiceType] = candidate;
                    return candidate;
                }
            }

            SetFilterMethodMisses.Add(sourceVoiceType);
            LogLiveFilterReflectionFailure("SetFilterParameters missing on " + sourceVoiceType.FullName);
            return null;
        }

        private static MethodInfo ResolveGetFilterMethod(Type sourceVoiceType)
        {
            if (sourceVoiceType == null || GetFilterMethodMisses.Contains(sourceVoiceType))
                return null;

            if (GetFilterMethods.TryGetValue(sourceVoiceType, out MethodInfo method))
                return method;

            MethodInfo[] methods = sourceVoiceType.GetMethods(InstanceMembers);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo candidate = methods[i];
                if (!string.Equals(candidate.Name, "GetFilterParameters", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != 1)
                    continue;

                if (parameters[0].ParameterType.IsByRef && parameters[0].ParameterType.GetElementType() == SharpFilterParametersType)
                {
                    GetFilterMethods[sourceVoiceType] = candidate;
                    return candidate;
                }
            }

            GetFilterMethodMisses.Add(sourceVoiceType);
            return null;
        }

        private static string BuildSettingsSignature(RealisticSoundPlusSettings settings)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}:{2:0.###}:{3:0.###}|{4}:{5}:{6:0.###}:{7:0.###}",
                Filter1Subtype,
                NormalizeCustomFilterType(settings.Filter1Type),
                SanitizeFrequency(settings.Filter1Frequency),
                SanitizeQ(settings.Filter1Q),
                Filter2Subtype,
                NormalizeCustomFilterType(settings.Filter2Type),
                SanitizeFrequency(settings.Filter2Frequency),
                SanitizeQ(settings.Filter2Q));
        }

        private static string DescribeSettings(RealisticSoundPlusSettings settings)
        {
            float filter1Frequency = SanitizeFrequency(settings.Filter1Frequency);
            float filter1Q = SanitizeQ(settings.Filter1Q);
            float filter2Frequency = SanitizeFrequency(settings.Filter2Frequency);
            float filter2Q = SanitizeQ(settings.Filter2Q);
            string filter1Type = NormalizeCustomFilterType(settings.Filter1Type);
            string filter2Type = NormalizeCustomFilterType(settings.Filter2Type);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} type={1} freqHz={2:0.###} xfreq={3:0.######} q={4:0.###} oneOverQ={5:0.###}; {6} type={7} freqHz={8:0.###} xfreq={9:0.######} q={10:0.###} oneOverQ={11:0.###}",
                Filter1Subtype,
                filter1Type,
                filter1Frequency,
                ToXAudioFrequency(filter1Frequency),
                filter1Q,
                ToXAudioOneOverQ(filter1Q),
                Filter2Subtype,
                filter2Type,
                filter2Frequency,
                ToXAudioFrequency(filter2Frequency),
                filter2Q,
                ToXAudioOneOverQ(filter2Q));
        }

        public static float SanitizeFrequency(float frequency)
        {
            return Math.Max(MinFilterFrequency, Math.Min(MaxFilterFrequency, frequency));
        }

        public static float SanitizeQ(float q)
        {
            return Math.Max(MinFilterQ, Math.Min(MaxFilterQ, q));
        }

        private static float ToXAudioFrequency(float cutoffHz)
        {
            return ToXAudioFrequency(cutoffHz, DefaultXAudioFilterSampleRate);
        }

        private static float ToXAudioFrequency(float cutoffHz, float sampleRate)
        {
            float sanitized = SanitizeFrequency(cutoffHz);
            float safeSampleRate = sampleRate >= 6000f ? sampleRate : DefaultXAudioFilterSampleRate;
            sanitized = Math.Min(sanitized, GetMaxCutoffForSampleRate(safeSampleRate));
            float value = (float)(2.0 * Math.Sin(Math.PI * sanitized / safeSampleRate));
            return Math.Max(0.0001f, Math.Min(MaxXAudioFilterFrequency, value));
        }

        private static float GetMaxCutoffForSampleRate(float sampleRate)
        {
            float safeSampleRate = sampleRate >= 6000f ? sampleRate : DefaultXAudioFilterSampleRate;
            return Math.Max(MinFilterFrequency, safeSampleRate / 6f);
        }

        private static float ResolveSourceVoiceInputSampleRate(object sourceVoice)
        {
            if (sourceVoice == null)
                return DefaultXAudioFilterSampleRate;

            Type voiceType = sourceVoice.GetType();
            if (VoiceDetailsPropertyMisses.Contains(voiceType))
                return DefaultXAudioFilterSampleRate;

            if (!VoiceDetailsProperties.TryGetValue(voiceType, out PropertyInfo property))
            {
                property = voiceType.GetProperty("VoiceDetails", InstanceMembers);
                if (property == null)
                {
                    VoiceDetailsPropertyMisses.Add(voiceType);
                    return DefaultXAudioFilterSampleRate;
                }

                VoiceDetailsProperties[voiceType] = property;
            }

            object details = null;
            try
            {
                details = property.GetValue(sourceVoice, null);
            }
            catch
            {
                VoiceDetailsPropertyMisses.Add(voiceType);
                return DefaultXAudioFilterSampleRate;
            }

            Type detailsType = details?.GetType();
            if (detailsType == null || VoiceDetailsSampleRateMisses.Contains(detailsType))
                return DefaultXAudioFilterSampleRate;

            if (!VoiceDetailsSampleRateFields.TryGetValue(detailsType, out FieldInfo sampleRateField))
            {
                sampleRateField = detailsType.GetField("InputSampleRate", InstanceMembers);
                if (sampleRateField == null)
                {
                    VoiceDetailsSampleRateMisses.Add(detailsType);
                    return DefaultXAudioFilterSampleRate;
                }

                VoiceDetailsSampleRateFields[detailsType] = sampleRateField;
            }

            try
            {
                object raw = sampleRateField.GetValue(details);
                float sampleRate = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
                return sampleRate >= 6000f ? sampleRate : DefaultXAudioFilterSampleRate;
            }
            catch
            {
                VoiceDetailsSampleRateMisses.Add(detailsType);
                return DefaultXAudioFilterSampleRate;
            }
        }

        private static float ToXAudioOneOverQ(float q)
        {
            float sanitized = SanitizeQ(q);
            float value = 1f / Math.Max(0.0001f, sanitized);
            return Math.Max(0.0001f, Math.Min(MaxXAudioOneOverQ, value));
        }

        private static string GetCustomFilterType(string subtype, RealisticSoundPlusSettings settings)
        {
            string value = string.Equals(subtype, Filter1Subtype, StringComparison.OrdinalIgnoreCase)
                ? settings?.Filter1Type
                : settings?.Filter2Type;
            return NormalizeCustomFilterType(value);
        }

        private static void GetFilterParametersForEmitter(string subtype, RealisticSoundPlusSettings settings, MyEntity3DSoundEmitter emitter, out string filterType, out float frequency, out float q)
        {
            if (string.Equals(subtype, EngineFilterSubtype, StringComparison.OrdinalIgnoreCase)
                && settings != null
                && settings.EngineFilterDynamic
                && emitter != null
                && AudioEngineV2Runtime.IsHullOnlyFilterRoute(emitter)
                && V2EngineFilterModel.TryCalculateHullOnly(emitter, settings, out V2EngineFilterSample hullOnlySample))
            {
                V2EngineFilterTelemetry.Record(hullOnlySample);
                filterType = "LowPass";
                frequency = hullOnlySample.FinalCutoff;
                q = hullOnlySample.FinalQ;
                return;
            }

            if (string.Equals(subtype, EngineFilterSubtype, StringComparison.OrdinalIgnoreCase)
                && settings != null
                && settings.EngineFilterDynamic
                && emitter != null
                && V2EngineFilterModel.TryCalculate(emitter, settings, out V2EngineFilterSample sample))
            {
                V2EngineFilterTelemetry.Record(sample);
                filterType = "LowPass";
                frequency = sample.FinalCutoff;
                q = sample.FinalQ;
                return;
            }

            frequency = string.Equals(subtype, EngineFilterSubtype, StringComparison.OrdinalIgnoreCase)
                ? settings.Filter1Frequency
                : settings.Filter2Frequency;
            q = string.Equals(subtype, EngineFilterSubtype, StringComparison.OrdinalIgnoreCase)
                ? settings.Filter1Q
                : settings.Filter2Q;
            filterType = GetCustomFilterType(subtype, settings);
        }

        private static string NormalizeCustomFilterType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "lowpass":
                case "low":
                case "lp": return "LowPass";
                case "highpass":
                case "high":
                case "hp": return "HighPass";
                case "bandpass":
                case "band":
                case "bp": return "BandPass";
                case "notch":
                case "reject":
                case "bandreject": return "Notch";
                default: return "LowPass";
            }
        }

        private static object ToSharpFilterType(string filterType)
        {
            string normalized = NormalizeCustomFilterType(filterType);
            return Enum.Parse(SharpFilterType, normalized + "Filter");
        }

        // Direct-typed counterpart to ToSharpFilterType for the fast (non-reflection) apply path: maps the
        // normalized filter name straight onto the SharpDX enum with no Enum.Parse/boxing per call.
        private static SharpDX.XAudio2.FilterType ToSharpFilterTypeDirect(string filterType)
        {
            switch (NormalizeCustomFilterType(filterType))
            {
                case "HighPass": return SharpDX.XAudio2.FilterType.HighPassFilter;
                case "BandPass": return SharpDX.XAudio2.FilterType.BandPassFilter;
                case "Notch": return SharpDX.XAudio2.FilterType.NotchFilter;
                default: return SharpDX.XAudio2.FilterType.LowPassFilter;
            }
        }

        // Root-level de-zipper. Smooths the per-voice cutoff (in log-frequency) and Q toward the target before
        // the biquad write, so discrete jumps (distance/pressure/classification changes, voice rebinds) glide
        // instead of stepping the filter and clicking. Applies to every live filter write (engine and aux) at
        // this single choke point. Tunable via LiveFilterSmoothingMs (0 = off). A stale entry (a pooled voice
        // reused after a gap) snaps rather than smoothing across two unrelated sounds.
        private static void SmoothLiveFilter(IMySourceVoice voice, ref float frequency, ref float q)
        {
            float timeConstantMs = SettingsManager.Current?.LiveFilterSmoothingMs ?? DefaultLiveFilterSmoothingMs;
            if (timeConstantMs <= 0.001f)
                return;

            DateTime now = DateTime.UtcNow;
            if (!LiveFilterSmoothing.TryGetValue(voice, out LiveFilterSmoothState state)
                || now - state.UpdatedUtc > LiveFilterSmoothingResetGap)
            {
                if (LiveFilterSmoothing.Count > MaxLiveFilterSmoothing)
                    PurgeLiveFilterSmoothing(now);

                LiveFilterSmoothing[voice] = new LiveFilterSmoothState { Frequency = frequency, Q = q, UpdatedUtc = now };
                if (_liveFilterSmoothSnaps < long.MaxValue) _liveFilterSmoothSnaps++;
                return;
            }

            float elapsedMs = (float)Math.Max(0.0, (now - state.UpdatedUtc).TotalMilliseconds);
            float alpha = elapsedMs / timeConstantMs;
            if (alpha < 0f) alpha = 0f;
            else if (alpha > 1f) alpha = 1f;

            float fromLog = (float)Math.Log(Math.Max(1f, state.Frequency));
            float toLog = (float)Math.Log(Math.Max(1f, frequency));
            float smoothedFrequency = (float)Math.Exp(fromLog + (toLog - fromLog) * alpha);
            float smoothedQ = state.Q + (q - state.Q) * alpha;

            LiveFilterSmoothing[voice] = new LiveFilterSmoothState { Frequency = smoothedFrequency, Q = smoothedQ, UpdatedUtc = now };
            frequency = smoothedFrequency;
            q = smoothedQ;
            if (_liveFilterSmoothApplied < long.MaxValue) _liveFilterSmoothApplied++;
        }

        // Diagnostic line for the root-level de-zipper (V2 debug log). voices=live smoothing entries,
        // applied=writes smoothed toward target, snaps=new/stale-reset entries that bypassed smoothing.
        public static string FormatSmoothingSummary()
        {
            float tc = SettingsManager.Current?.LiveFilterSmoothingMs ?? DefaultLiveFilterSmoothingMs;
            return string.Format(
                CultureInfo.InvariantCulture,
                "voices={0} applied={1} snaps={2} tc={3:0}ms",
                LiveFilterSmoothing.Count,
                _liveFilterSmoothApplied,
                _liveFilterSmoothSnaps,
                tc);
        }

        private static void PurgeLiveFilterSmoothing(DateTime now)
        {
            List<IMySourceVoice> remove = null;
            foreach (KeyValuePair<IMySourceVoice, LiveFilterSmoothState> pair in LiveFilterSmoothing)
            {
                if (now - pair.Value.UpdatedUtc <= LiveFilterSmoothingResetGap)
                    continue;

                if (remove == null)
                    remove = new List<IMySourceVoice>();
                remove.Add(pair.Key);
            }

            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                    LiveFilterSmoothing.Remove(remove[i]);
            }

            if (LiveFilterSmoothing.Count > MaxLiveFilterSmoothing)
                LiveFilterSmoothing.Clear();
        }

        private struct LiveFilterSmoothState
        {
            public float Frequency;
            public float Q;
            public DateTime UpdatedUtc;
        }

        private static string DescribeEffect(IDictionary effects, string subtype)
        {
            try
            {
                if (effects == null || string.IsNullOrWhiteSpace(subtype))
                    return subtype + "=missing";

                MyStringHash id = MyStringHash.GetOrCompute(subtype);
                if (!effects.Contains(id))
                    return subtype + "=missing";

                object effect = effects[id];
                object sounds = SoundsEffectsField.GetValue(effect);
                return subtype + " " + DescribeSoundEffects(sounds);
            }
            catch (Exception ex)
            {
                return subtype + "=describe-failed:" + ex.Message;
            }
        }

        private static string DescribeSoundEffects(object sounds)
        {
            int groupIndex = 0;
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            IEnumerable groups = sounds as IEnumerable;
            if (groups == null)
                return "sounds=null";

            foreach (object group in groups)
            {
                int effectIndex = 0;
                IEnumerable effects = group as IEnumerable;
                if (effects == null)
                    continue;

                foreach (object effect in effects)
                {
                    if (builder.Length > 0)
                        builder.Append("; ");

                    builder.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "g{0}e{1} dur={2} filter={3} xfreq={4} oneOverQ={5} stop={6} curve={7}",
                        groupIndex,
                        effectIndex,
                        DurationField.GetValue(effect),
                        FilterField.GetValue(effect),
                        FrequencyField.GetValue(effect),
                        OneOverQField.GetValue(effect),
                        StopAfterField != null ? StopAfterField.GetValue(effect) : "?",
                        VolumeCurveField != null ? (VolumeCurveField.GetValue(effect) ?? "null") : "?");
                    effectIndex++;
                }

                groupIndex++;
            }

            return builder.Length == 0 ? "sounds=empty" : builder.ToString();
        }

        private static void LogReflectionFailure(string message)
        {
            if (!_loggedReflectionFailure)
            {
                _loggedReflectionFailure = true;
                V2DebugLog.WriteEvent("filter-register-failed", message ?? "unknown");
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Dynamic audio filters unavailable: " + (message ?? "unknown"));
            }

            _disabled = true;
        }

        private static void LogLiveFilterReflectionFailure(string message)
        {
            if (_loggedLiveFilterReflectionFailure)
                return;

            _loggedLiveFilterReflectionFailure = true;
            V2DebugLog.WriteEvent("live-filter-failed", message ?? "unknown");
            MyLog.Default.WriteLine("[RealisticSoundPlus] Live custom audio filter update unavailable: " + (message ?? "unknown"));
        }

        private static void LogLiveEffectApplied(string subtype, string filterType, float frequency, float q)
        {
            if (!SettingsManager.Current.V2DebugLogEnabled)
                return;

            string normalizedFilterType = NormalizeCustomFilterType(filterType);
            string signature = string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.###}:{3:0.###}", subtype, normalizedFilterType, SanitizeFrequency(frequency), SanitizeQ(q));
            DateTime now = DateTime.UtcNow;
            if (now - _lastLiveEffectLogUtc < LiveFilterDiagnosticInterval)
            {
                IncrementSuppressed(ref _suppressedLiveEffectLogs);
                return;
            }

            if (string.Equals(_lastLiveEffectSignature, signature, StringComparison.Ordinal) && now - _lastLiveEffectLogUtc < TimeSpan.FromSeconds(2))
            {
                IncrementSuppressed(ref _suppressedLiveEffectLogs);
                return;
            }

            _lastLiveEffectSignature = signature;
            _lastLiveEffectLogUtc = now;
            int skipped = _suppressedLiveEffectLogs;
            _suppressedLiveEffectLogs = 0;
            V2DebugLog.WriteEvent("live-filter-effect", string.Format(
                CultureInfo.InvariantCulture,
                "{0} type={1} freqHz={2:0.###} xfreq={3:0.######} q={4:0.###} oneOverQ={5:0.###} skipped={6}",
                subtype,
                normalizedFilterType,
                SanitizeFrequency(frequency),
                ToXAudioFrequency(frequency),
                SanitizeQ(q),
                ToXAudioOneOverQ(q),
                skipped));
        }

        private static void LogLiveVoiceApplied(IMySourceVoice voice, string subtype, string filterType, float frequency, float q)
        {
            if (!SettingsManager.Current.V2DebugLogEnabled)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastLiveVoiceLogUtc < LiveFilterDiagnosticInterval)
            {
                IncrementSuppressed(ref _suppressedLiveVoiceLogs);
                return;
            }

            string normalizedFilterType = NormalizeCustomFilterType(filterType);
            object sourceVoice = ResolveSourceVoice(voice);
            float sampleRate = ResolveSourceVoiceInputSampleRate(sourceVoice);
            string readback = DescribeCurrentFilter(voice);
            string signature = string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.###}:{3:0.###}:{4}", subtype, normalizedFilterType, SanitizeFrequency(frequency), SanitizeQ(q), readback);
            if (string.Equals(_lastLiveVoiceSignature, signature, StringComparison.Ordinal) && now - _lastLiveVoiceLogUtc < TimeSpan.FromSeconds(2))
            {
                IncrementSuppressed(ref _suppressedLiveVoiceLogs);
                return;
            }

            _lastLiveVoiceSignature = signature;
            _lastLiveVoiceLogUtc = now;
            int skipped = _suppressedLiveVoiceLogs;
            _suppressedLiveVoiceLogs = 0;
            V2DebugLog.WriteEvent("live-filter-voice", string.Format(
                CultureInfo.InvariantCulture,
                "{0} type={1} freqHz={2:0.###} xfreq={3:0.######} q={4:0.###} oneOverQ={5:0.###} sampleRate={6:0} maxHz={7:0.###} skipped={8} {9}",
                subtype,
                normalizedFilterType,
                SanitizeFrequency(frequency),
                ToXAudioFrequency(frequency, sampleRate),
                SanitizeQ(q),
                ToXAudioOneOverQ(q),
                sampleRate,
                GetMaxCutoffForSampleRate(sampleRate),
                skipped,
                readback));
        }

        private static void IncrementSuppressed(ref int count)
        {
            if (count < int.MaxValue)
                count++;
        }

        private static long SaturatingIncrement(long value)
        {
            return value == long.MaxValue ? value : value + 1L;
        }

        private static string DescribeCurrentFilter(IMySourceVoice voice)
        {
            if (voice == null || !HasLiveFilterReflection())
                return "actual=unavailable";

            object sourceVoice = ResolveSourceVoice(voice);
            if (sourceVoice == null)
                return "actual=source-missing";

            MethodInfo getFilter = ResolveGetFilterMethod(sourceVoice.GetType());
            if (getFilter == null)
                return "actual=readback-missing";

            try
            {
                object parameters = Activator.CreateInstance(SharpFilterParametersType);
                object[] args = new[] { parameters };
                getFilter.Invoke(sourceVoice, args);
                object current = args[0];
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "actual={0} xfreq={1:0.######} oneOverQ={2:0.###} sampleRate={3:0} maxHz={4:0.###}",
                    SharpFilterTypeField.GetValue(current),
                    SharpFilterFrequencyField.GetValue(current),
                    SharpFilterOneOverQField.GetValue(current),
                    ResolveSourceVoiceInputSampleRate(sourceVoice),
                    GetMaxCutoffForSampleRate(ResolveSourceVoiceInputSampleRate(sourceVoice)));
            }
            catch (Exception ex)
            {
                return "actual=readback-failed:" + ex.Message;
            }
        }

        private static Type ResolveType(string fullName)
        {
            Type type = Type.GetType(fullName + ", VRage.Audio", false);
            if (type != null)
                return type;

            type = Type.GetType(fullName + ", SharpDX.XAudio2", false);
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

        private struct EmitterVoiceBinding
        {
            public MyEntity3DSoundEmitter Emitter;
            public DateTime UpdatedUtc;
        }
    }
}
