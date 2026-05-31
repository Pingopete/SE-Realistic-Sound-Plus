using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    internal static class ExteriorSoundTransmission
    {
        private static bool _pressureLookupDisabled;
        private static int _pressureLookupErrors;

        public static float Calculate(Vector3D sourcePosition)
        {
            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (listenerPosition == Vector3D.Zero)
                return 1f;

            return Calculate(listenerPosition, sourcePosition);
        }

        public static float Calculate(Vector3D listenerPosition, Vector3D sourcePosition)
        {
            var settings = SettingsManager.Current;
            float effectiveMuffling = CalculateEffectiveMufflingStrength(listenerPosition, sourcePosition, settings);
            if (effectiveMuffling <= 0f)
                return 1f;

            double distance = Vector3D.Distance(listenerPosition, sourcePosition);
            float distanceBlend = Clamp01((float)((distance - settings.NearDistance) / (settings.FarDistance - settings.NearDistance)));
            float distanceTransmission = Lerp(1f, settings.FarDistanceTransmission, distanceBlend);
            float fullTransmission = Clamp01(settings.InteriorBaseTransmission * distanceTransmission);
            return Clamp01(1f - (1f - fullTransmission) * effectiveMuffling);
        }

        public static float CalculateEffectiveMufflingStrength(Vector3D sourcePosition)
        {
            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            if (listenerPosition == Vector3D.Zero)
                return SettingsManager.Current.MufflingStrength;

            return CalculateEffectiveMufflingStrength(listenerPosition, sourcePosition, SettingsManager.Current);
        }

        private static float CalculateEffectiveMufflingStrength(Vector3D listenerPosition, Vector3D sourcePosition, RealisticSoundPlusSettings settings)
        {
            float pressure = Math.Max(GetAtmosphericPressure(listenerPosition), GetAtmosphericPressure(sourcePosition));
            float pressureScale = Lerp(1f, settings.AtmosphericMufflingFloor, pressure);
            return Clamp01(settings.MufflingStrength * pressureScale);
        }

        public static float GetAtmosphericPressure(Vector3D position)
        {
            if (_pressureLookupDisabled || position == Vector3D.Zero)
                return 0f;

            try
            {
                MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(position);
                if (planet == null || !planet.HasAtmosphere)
                    return 0f;

                return Clamp01(planet.GetAirDensity(position));
            }
            catch (Exception ex)
            {
                if (++_pressureLookupErrors >= 3)
                {
                    _pressureLookupDisabled = true;
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling atmospheric pressure lookup after error: " + ex);
                }

                return 0f;
            }
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * amount;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
