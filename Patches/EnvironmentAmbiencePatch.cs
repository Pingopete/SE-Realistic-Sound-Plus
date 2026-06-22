using System;
using System.Reflection;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using VRage.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    internal static class EnvironmentAmbiencePatch
    {
        private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2);
        private static readonly Type PlanetAmbientType = AccessTools.TypeByName("Sandbox.Game.SessionComponents.MySessionComponentPlanetAmbientSounds");
        private static readonly Type WeatherType = AccessTools.TypeByName("Sandbox.Game.SessionComponents.MySectorWeatherComponent");
        private static readonly FieldInfo PlanetVolumeModifierTargetField = PlanetAmbientType != null ? AccessTools.Field(PlanetAmbientType, "m_volumeModifierTarget") : null;
        private static readonly FieldInfo PlanetVolumeModifierField = PlanetAmbientType != null ? AccessTools.Field(PlanetAmbientType, "m_volumeModifier") : null;
        private static readonly FieldInfo WeatherAmbientSoundField = WeatherType != null ? AccessTools.Field(WeatherType, "m_ambientSound") : null;
        private static readonly FieldInfo WeatherTargetVolumeField = WeatherType != null ? AccessTools.Field(WeatherType, "m_targetVolume") : null;
        private static readonly FieldInfo WeatherCurrentWeatherField = WeatherType != null ? AccessTools.Field(WeatherType, "m_currentWeather") : null;
        private static readonly FieldInfo WeatherCurrentSoundField = WeatherType != null ? AccessTools.Field(WeatherType, "m_currentSound") : null;

        private static FieldInfo _weatherAmbientVolumeField;
        private static FieldInfo _weatherAmbientSoundNameField;
        private static bool _planetAmbientUnavailable;
        private static int _planetHardOffBypassed;
        private static int _planetCarrierRepairs;
        private static int _weatherCarrierRepairs;
        private static DateTime _lastLogUtc = DateTime.MinValue;

        public static void ResetRuntimeState()
        {
            _planetHardOffBypassed = 0;
            _planetCarrierRepairs = 0;
            _weatherCarrierRepairs = 0;
            _planetAmbientUnavailable = false;
            _lastLogUtc = DateTime.MinValue;
        }

        public static bool ShouldOwnEnvironmentAmbience()
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            return settings != null
                && settings.PlayerFilterEnabled
                && settings.PlayerFilterEnvironmentEnabled;
        }

        public static string FormatSummary()
        {
            return "planetOffBypass=" + _planetHardOffBypassed
                + " planetCarrier=" + _planetCarrierRepairs
                + " weatherCarrier=" + _weatherCarrierRepairs;
        }

        public static bool BypassPlanetAmbientOff(object instance)
        {
            if (!ShouldOwnEnvironmentAmbience())
                return false;

            if (!SetPlanetAmbientTarget(instance, 1f, true))
                return false;

            _planetHardOffBypassed++;
            LogIfDue("planet-off-bypass");
            return true;
        }

        public static void KeepPlanetAmbientCarrierAlive(object instance)
        {
            if (!ShouldOwnEnvironmentAmbience() || instance == null)
                return;

            if (SetPlanetAmbientTarget(instance, 1f, false))
            {
                _planetCarrierRepairs++;
                LogIfDue("planet-carrier");
            }
        }

        public static void KeepWeatherAmbientCarrierAlive(object instance)
        {
            if (!ShouldOwnEnvironmentAmbience() || instance == null)
                return;

            IMySourceVoice voice = WeatherAmbientSoundField?.GetValue(instance) as IMySourceVoice;
            if (voice == null || !voice.IsValid || !voice.IsPlaying)
                return;

            object weather = WeatherCurrentWeatherField?.GetValue(instance);
            if (weather == null)
                return;

            string ambientCue = GetWeatherAmbientSoundName(weather);
            if (string.IsNullOrWhiteSpace(ambientCue))
                ambientCue = WeatherCurrentSoundField?.GetValue(instance) as string;

            if (string.IsNullOrWhiteSpace(ambientCue))
                return;

            float ambientVolume = Math.Max(0.001f, GetWeatherAmbientVolume(weather));
            float targetVolume = GetFieldFloat(WeatherTargetVolumeField, instance, 0f);
            if (targetVolume < ambientVolume)
                WeatherTargetVolumeField?.SetValue(instance, ambientVolume);

            if (voice.Volume < ambientVolume)
                voice.SetVolume(ambientVolume);

            _weatherCarrierRepairs++;
            LogIfDue("weather-carrier");
        }

        private static bool SetPlanetAmbientTarget(object instance, float value, bool forceModifierFloor)
        {
            if (_planetAmbientUnavailable || PlanetVolumeModifierTargetField == null)
                return false;

            try
            {
                object targetOwner = GetFieldOwner(PlanetVolumeModifierTargetField, instance);
                if (targetOwner == MissingFieldOwner)
                    return false;

                float previousTarget = GetFieldFloat(PlanetVolumeModifierTargetField, targetOwner, value);
                PlanetVolumeModifierTargetField.SetValue(targetOwner, value);

                if (forceModifierFloor && PlanetVolumeModifierField != null)
                {
                    object modifierOwner = GetFieldOwner(PlanetVolumeModifierField, instance);
                    if (modifierOwner != MissingFieldOwner)
                    {
                        float current = GetFieldFloat(PlanetVolumeModifierField, modifierOwner, value);
                        if (current <= 0.001f)
                            PlanetVolumeModifierField.SetValue(modifierOwner, 0.001f);
                    }
                }

                return Math.Abs(previousTarget - value) > 0.0001f;
            }
            catch (Exception ex)
            {
                _planetAmbientUnavailable = true;
                V2DebugLog.WriteEvent("env-ambience-disabled", ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string GetWeatherAmbientSoundName(object weather)
        {
            if (weather == null)
                return null;

            if (_weatherAmbientSoundNameField == null)
                _weatherAmbientSoundNameField = AccessTools.Field(weather.GetType(), "AmbientSound");

            return _weatherAmbientSoundNameField?.GetValue(weather) as string;
        }

        private static float GetWeatherAmbientVolume(object weather)
        {
            if (weather == null)
                return 1f;

            if (_weatherAmbientVolumeField == null)
                _weatherAmbientVolumeField = AccessTools.Field(weather.GetType(), "AmbientVolume");

            return GetFieldFloat(_weatherAmbientVolumeField, weather, 1f);
        }

        private static float GetFieldFloat(FieldInfo field, object instance, float fallback)
        {
            if (field == null)
                return fallback;

            object value = field.GetValue(instance);
            if (value is float f)
                return f;

            if (value is double d)
                return (float)d;

            return fallback;
        }

        private static readonly object MissingFieldOwner = new object();

        private static object GetFieldOwner(FieldInfo field, object instance)
        {
            if (field == null)
                return MissingFieldOwner;

            if (field.IsStatic)
                return null;

            return instance ?? MissingFieldOwner;
        }

        private static void LogIfDue(string reason)
        {
            if (!SettingsManager.Current.V2DebugLogEnabled)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastLogUtc < LogInterval)
                return;

            _lastLogUtc = now;
            V2DebugLog.WriteEvent("env-ambience-" + reason, FormatSummary());
        }
    }

    [HarmonyPatch]
    internal static class PlanetAmbientHardOffPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Sandbox.Game.SessionComponents.MySessionComponentPlanetAmbientSounds");
            return type != null ? AccessTools.Method(type, "SetAmbientOff", Type.EmptyTypes) : null;
        }

        private static bool Prefix(object __instance)
        {
            return !EnvironmentAmbiencePatch.BypassPlanetAmbientOff(__instance);
        }
    }

    [HarmonyPatch]
    internal static class PlanetAmbientCarrierPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Sandbox.Game.SessionComponents.MySessionComponentPlanetAmbientSounds");
            return type != null ? AccessTools.Method(type, "UpdateAfterSimulation", Type.EmptyTypes) : null;
        }

        private static void Postfix(object __instance)
        {
            EnvironmentAmbiencePatch.KeepPlanetAmbientCarrierAlive(__instance);
        }
    }

    [HarmonyPatch]
    internal static class WeatherAmbientCarrierPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Sandbox.Game.SessionComponents.MySectorWeatherComponent");
            return type != null ? AccessTools.Method(type, "ApplySound", Type.EmptyTypes) : null;
        }

        private static void Postfix(object __instance)
        {
            EnvironmentAmbiencePatch.KeepWeatherAmbientCarrierAlive(__instance);
        }
    }
}
