using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using VRage.Data.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class RspDynamicAudioFilters
    {
        public const string Filter1Subtype = "RSPFilter1";
        public const string Filter2Subtype = "RSPFilter2";
        public const float MinFilterFrequency = 20f;
        public const float MaxFilterFrequency = 7350f;
        public const float MinFilterQ = 0.1f;
        public const float MaxFilterQ = 10f;
        private const float XAudioFilterSampleRate = 44100f;
        private const float MaxXAudioFilterFrequency = 1f;
        private const float MaxXAudioOneOverQ = 1.5f;

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

        private static string _lastRegisteredSignature;
        private static bool _loggedNotReady;
        private static bool _loggedReflectionFailure;
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
                RegisterOrReplace(effects, Filter1Subtype, settings.Filter1Frequency, settings.Filter1Q);
                RegisterOrReplace(effects, Filter2Subtype, settings.Filter2Frequency, settings.Filter2Q);
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
            _loggedNotReady = false;
        }

        public static bool IsCustomFilterSubtype(string subtype)
        {
            return string.Equals(subtype, Filter1Subtype, StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtype, Filter2Subtype, StringComparison.OrdinalIgnoreCase);
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

        private static void RegisterOrReplace(IDictionary effects, string subtype, float frequency, float q)
        {
            MyStringHash effectId = MyStringHash.GetOrCompute(subtype);
            float sanitizedFrequency = SanitizeFrequency(frequency);
            float sanitizedQ = SanitizeQ(q);
            float xAudioFrequency = ToXAudioFrequency(sanitizedFrequency);
            float xAudioOneOverQ = ToXAudioOneOverQ(sanitizedQ);
            object effect = TryCreateFromTemplate(effects, effectId, xAudioFrequency, xAudioOneOverQ)
                ?? CreateEffect(effectId, xAudioFrequency, xAudioOneOverQ);
            effects[effectId] = effect;
        }

        private static object TryCreateFromTemplate(IDictionary effects, MyStringHash effectId, float xAudioFrequency, float xAudioOneOverQ)
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
                SoundsEffectsField.SetValue(clone, CloneSoundEffectsList(SoundsEffectsField.GetValue(template), xAudioFrequency, xAudioOneOverQ));
                return clone;
            }
            catch (Exception ex)
            {
                V2DebugLog.WriteEvent("filter-clone-failed", ex.Message);
                return null;
            }
        }

        private static object CreateEffect(MyStringHash effectId, float xAudioFrequency, float xAudioOneOverQ)
        {
            MyAudioEffect effect = new MyAudioEffect();
            EffectIdField.SetValue(effect, effectId);
            ResultEmitterIdxField.SetValue(effect, 0);
            SoundsEffectsField.SetValue(effect, CreateSoundEffectsList(xAudioFrequency, xAudioOneOverQ));
            return effect;
        }

        private static object CreateSoundEffectsList(float xAudioFrequency, float xAudioOneOverQ)
        {
            object soundEffect = Activator.CreateInstance(SoundEffectType);
            DurationField.SetValue(soundEffect, 0f);
            FilterField.SetValue(soundEffect, Enum.Parse(FilterField.FieldType, "LowPass"));
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

        private static object CloneSoundEffectsList(object sourceList, float xAudioFrequency, float xAudioOneOverQ)
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
                                ConfigureLowPass(clonedEffect, xAudioFrequency, xAudioOneOverQ);
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
                return CreateSoundEffectsList(xAudioFrequency, xAudioOneOverQ);

            if (!replacedFirst)
                ((IList)outerList[0]).Add(CreateLowPassSoundEffect(xAudioFrequency, xAudioOneOverQ));

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

        private static object CreateLowPassSoundEffect(float xAudioFrequency, float xAudioOneOverQ)
        {
            object soundEffect = Activator.CreateInstance(SoundEffectType);
            ConfigureLowPass(soundEffect, xAudioFrequency, xAudioOneOverQ);
            return soundEffect;
        }

        private static void ConfigureLowPass(object soundEffect, float xAudioFrequency, float xAudioOneOverQ)
        {
            DurationField.SetValue(soundEffect, 0f);
            FilterField.SetValue(soundEffect, Enum.Parse(FilterField.FieldType, "LowPass"));
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

        private static string BuildSettingsSignature(RealisticSoundPlusSettings settings)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:0.###}:{2:0.###}|{3}:{4:0.###}:{5:0.###}",
                Filter1Subtype,
                SanitizeFrequency(settings.Filter1Frequency),
                SanitizeQ(settings.Filter1Q),
                Filter2Subtype,
                SanitizeFrequency(settings.Filter2Frequency),
                SanitizeQ(settings.Filter2Q));
        }

        private static string DescribeSettings(RealisticSoundPlusSettings settings)
        {
            float filter1Frequency = SanitizeFrequency(settings.Filter1Frequency);
            float filter1Q = SanitizeQ(settings.Filter1Q);
            float filter2Frequency = SanitizeFrequency(settings.Filter2Frequency);
            float filter2Q = SanitizeQ(settings.Filter2Q);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} cutoffHz={1:0.###} xfreq={2:0.######} q={3:0.###} oneOverQ={4:0.###}; {5} cutoffHz={6:0.###} xfreq={7:0.######} q={8:0.###} oneOverQ={9:0.###}",
                Filter1Subtype,
                filter1Frequency,
                ToXAudioFrequency(filter1Frequency),
                filter1Q,
                ToXAudioOneOverQ(filter1Q),
                Filter2Subtype,
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
            float sanitized = SanitizeFrequency(cutoffHz);
            float value = (float)(2.0 * Math.Sin(Math.PI * sanitized / XAudioFilterSampleRate));
            return Math.Max(0.0001f, Math.Min(MaxXAudioFilterFrequency, value));
        }

        private static float ToXAudioOneOverQ(float q)
        {
            float sanitized = SanitizeQ(q);
            float value = 1f / Math.Max(0.0001f, sanitized);
            return Math.Max(0.0001f, Math.Min(MaxXAudioOneOverQ, value));
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

        private static Type ResolveType(string fullName)
        {
            Type type = Type.GetType(fullName + ", VRage.Audio", false);
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
    }
}
