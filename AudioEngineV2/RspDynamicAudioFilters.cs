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
        public const float MaxFilterFrequency = 20000f;
        public const float MinFilterQ = 0.1f;
        public const float MaxFilterQ = 1.5f;

        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type XAudioType = ResolveType("VRage.Audio.MyXAudio2");
        private static readonly FieldInfo XAudioInstanceField = XAudioType?.GetField("Instance", StaticMembers);
        private static readonly FieldInfo EffectIdField = typeof(MyAudioEffect).GetField("EffectId", InstanceMembers);
        private static readonly FieldInfo SoundsEffectsField = typeof(MyAudioEffect).GetField("SoundsEffects", InstanceMembers);
        private static readonly FieldInfo ResultEmitterIdxField = typeof(MyAudioEffect).GetField("ResultEmitterIdx", InstanceMembers);
        private static readonly Type SoundEffectType = typeof(MyAudioEffect).GetNestedType("SoundEffect", StaticMembers);

        private static readonly FieldInfo DurationField = SoundEffectType?.GetField("Duration", InstanceMembers);
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
            object effect = CreateEffect(effectId, SanitizeFrequency(frequency), SanitizeQ(q));
            effects[effectId] = effect;
        }

        private static object CreateEffect(MyStringHash effectId, float frequency, float q)
        {
            MyAudioEffect effect = new MyAudioEffect();
            EffectIdField.SetValue(effect, effectId);
            ResultEmitterIdxField.SetValue(effect, 0);
            SoundsEffectsField.SetValue(effect, CreateSoundEffectsList(frequency, q));
            return effect;
        }

        private static object CreateSoundEffectsList(float frequency, float q)
        {
            object soundEffect = Activator.CreateInstance(SoundEffectType);
            DurationField.SetValue(soundEffect, 0f);
            FilterField.SetValue(soundEffect, Enum.Parse(FilterField.FieldType, "LowPass"));
            FrequencyField.SetValue(soundEffect, frequency);
            OneOverQField.SetValue(soundEffect, q);
            StopAfterField?.SetValue(soundEffect, false);

            Type innerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(SoundEffectType);
            IList innerList = (IList)Activator.CreateInstance(innerListType);
            innerList.Add(soundEffect);

            Type outerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(innerListType);
            IList outerList = (IList)Activator.CreateInstance(outerListType);
            outerList.Add(innerList);
            return outerList;
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
                "{0} freq={1:0.###} q={2:0.###} oneOverQ={3:0.###}; {4} freq={5:0.###} q={6:0.###} oneOverQ={7:0.###}",
                Filter1Subtype,
                filter1Frequency,
                filter1Q,
                filter1Q,
                Filter2Subtype,
                filter2Frequency,
                filter2Q,
                filter2Q);
        }

        public static float SanitizeFrequency(float frequency)
        {
            return Math.Max(MinFilterFrequency, Math.Min(MaxFilterFrequency, frequency));
        }

        public static float SanitizeQ(float q)
        {
            return Math.Max(MinFilterQ, Math.Min(MaxFilterQ, q));
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
