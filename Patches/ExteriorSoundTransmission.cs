using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    internal static class ExteriorSoundTransmission
    {
        private static readonly TimeSpan InsideShipReportLifetime = TimeSpan.FromMilliseconds(250);
        private static bool _pressureLookupDisabled;
        private static int _pressureLookupErrors;
        private static DateTime _lastInsideShipReportUtc = DateTime.MinValue;

        public static void ResetRuntimeState()
        {
            _pressureLookupDisabled = false;
            _pressureLookupErrors = 0;
            _lastInsideShipReportUtc = DateTime.MinValue;
        }

        public static void ReportListenerInsideShip(bool insideShip)
        {
            if (insideShip)
                _lastInsideShipReportUtc = DateTime.UtcNow;
        }

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
            double distance = Vector3D.Distance(listenerPosition, sourcePosition);
            float nearDistance = Math.Min(settings.NearDistance, Math.Max(0f, settings.FarDistance - 0.001f));
            float distanceRange = Math.Max(0.001f, settings.FarDistance - nearDistance);
            float distanceBlend = Clamp01((float)((distance - nearDistance) / distanceRange));
            float distanceTransmission = Lerp(1f, settings.FarDistanceTransmission, distanceBlend);

            float effectiveMuffling = CalculateEffectiveMufflingStrength(listenerPosition, sourcePosition, settings);
            if (effectiveMuffling <= 0f)
                return Clamp01(distanceTransmission);

            float mufflingTransmission = Clamp01(1f - (1f - settings.InteriorBaseTransmission) * effectiveMuffling);
            return Clamp01(distanceTransmission * mufflingTransmission);
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
            float atmosphericFloor = IsListenerInsideShip() ? settings.AtmosphericMufflingFloor : 0f;
            float pressureScale = Lerp(1f, atmosphericFloor, pressure);
            return Clamp01(settings.MufflingStrength * pressureScale);
        }

        private static bool IsListenerInsideShip()
        {
            if (DateTime.UtcNow - _lastInsideShipReportUtc <= InsideShipReportLifetime)
                return true;

            return MyAPIGateway.Session?.ControlledObject is IMyShipController;
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
