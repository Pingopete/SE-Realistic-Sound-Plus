using System;
using RealisticSoundPlus.AudioEngineV2;
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
            float effectiveMuffling = CalculateEffectiveMufflingStrength(listenerPosition, sourcePosition, settings);
            if (effectiveMuffling <= 0f)
                return 1f;

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            float distanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.V2EmitterDistance, settings.V2DistanceCurve);
            float fullTransmission = Clamp01(Lerp(settings.InteriorBaseTransmission, 1f, distanceGain));
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
            float atmosphericFloor = IsListenerInsideShip() ? settings.AtmosphericMufflingFloor : 0f;
            float pressureScale = Lerp(1f, atmosphericFloor, pressure);
            return Clamp01(settings.MufflingStrength * pressureScale);
        }

        private static bool IsListenerInsideShip()
        {
            if (AudioEngineV2Runtime.Listener.InsideShip)
                return true;

            if (DateTime.UtcNow - _lastInsideShipReportUtc <= InsideShipReportLifetime)
                return true;

            return false;
        }

        public static float GetAtmosphericPressure(Vector3D position)
        {
            if (SettingsManager.Current.V2AtmosphereOverrideEnabled)
                return Clamp01(SettingsManager.Current.V2AtmosphereOverride);

            if (_pressureLookupDisabled || position == Vector3D.Zero)
                return 0f;

            try
            {
                MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(position);
                if (planet == null || !planet.HasAtmosphere)
                    return 0f;

                // SE's physical air density is accurate but exists only across the planet's narrow (~900m) atmosphere
                // shell - it is exactly 0 above the shell, so on its own it can't drive a GRADUAL high-altitude entry
                // or exit (the muffle stays pinned to vacuum until the shell, then snaps). Blend it with a SYNTHETIC
                // altitude ramp that opens over a wide band well above the shell: density wins lower down (accurate
                // near the surface), the altitude ramp provides the gradual lead-in up high where density is still 0.
                // max() hands off cleanly between the two. This is the single atmosphere source every audio path reads.
                float density = Clamp01(planet.GetAirDensity(position));
                float altitudeRamp = ComputeAltitudeAtmosphere(planet, position);
                return Clamp01(Math.Max(density, altitudeRamp));
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

        // The synthetic altitude ramp blended into GetAtmosphericPressure. Returns 1 at/below the surface and eases
        // smoothly to 0 at AtmosphereTransitionAltitudeMeters above it, so the muffle begins opening well above SE's
        // narrow physical air shell - giving the gradual "slowly adjusts as I enter/exit" feel the density curve
        // alone cannot (density is 0 above the shell, so nothing can ramp there). Tunable via the const; widen it for
        // an even more gradual transition. smoothstep so the ends ease rather than ramp linearly.
        private const float AtmosphereTransitionAltitudeMeters = 25000f;

        private static float ComputeAltitudeAtmosphere(MyPlanet planet, Vector3D position)
        {
            try
            {
                double altitude = (position - planet.PositionComp.GetPosition()).Length() - planet.AverageRadius;
                if (altitude <= 0.0)
                    return 1f;
                if (altitude >= AtmosphereTransitionAltitudeMeters)
                    return 0f;

                float t = (float)(altitude / AtmosphereTransitionAltitudeMeters); // 0 at surface .. 1 at band top
                float eased = t * t * (3f - 2f * t); // smoothstep
                return Clamp01(1f - eased);
            }
            catch
            {
                return 0f;
            }
        }
    }
}
