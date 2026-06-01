using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    internal static class AudioDiagnostics
    {
        private static readonly Dictionary<string, CueSnapshot> CueSnapshots = new Dictionary<string, CueSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CueLifetime = TimeSpan.FromSeconds(2.0);

        private static GlobalSnapshot _global;

        public static void UpdateGlobal(bool insideShip)
        {
            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            _global = new GlobalSnapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                ListenerPressure = ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition),
                ListenerAltitude = TryGetAltitude(listenerPosition),
                ControlledSpeed = TryGetControlledSpeed(),
                InsideShip = insideShip,
                EngineFilter = SettingsManager.Current.EngineFilter,
                AmbientEnabled = SettingsManager.Current.AmbientMufflingEnabled,
                SpatialEnabled = SettingsManager.Current.SpatialAudioEnabled
            };
        }

        public static void RecordEmitter(MyEntity3DSoundEmitter emitter, string route, float baseVolume, float transmission, float scale, float finalMultiplier, Vector3D sourcePosition)
        {
            if (emitter == null)
                return;

            CueSnapshot snapshot = CreateSnapshot(route, baseVolume, transmission, scale, finalMultiplier, sourcePosition);
            RecordCue(emitter.SoundId.ToString(), snapshot);
            RecordCue(emitter.Sound?.CueEnum.ToString(), snapshot);
            RecordCue(emitter.SecondarySound?.CueEnum.ToString(), snapshot);
        }

        public static bool TryGetCueSnapshot(string cueName, out CueSnapshot snapshot)
        {
            snapshot = default(CueSnapshot);
            if (string.IsNullOrWhiteSpace(cueName) || !CueSnapshots.TryGetValue(cueName, out snapshot))
                return false;

            return DateTime.UtcNow - snapshot.UpdatedUtc <= CueLifetime;
        }

        public static string FormatGlobal()
        {
            GlobalSnapshot snapshot = _global;
            string altitude = float.IsNaN(snapshot.ListenerAltitude)
                ? "alt=?"
                : string.Format(CultureInfo.InvariantCulture, "alt={0:0}m", snapshot.ListenerAltitude);

            return string.Format(
                CultureInfo.InvariantCulture,
                "atm={0:0.00} {1} speed={2:0.0} inside={3} filter={4} ambient={5} spatial={6}",
                snapshot.ListenerPressure,
                altitude,
                snapshot.ControlledSpeed,
                snapshot.InsideShip ? "Y" : "N",
                snapshot.EngineFilter,
                snapshot.AmbientEnabled ? "on" : "off",
                snapshot.SpatialEnabled ? "on" : "off");
        }

        public static string FormatCue(CueSnapshot snapshot)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                " {0} tr={1:0.00} sc={2:0.00} base={3:0.00} fin={4:0.00} d={5:0} p={6:0.00}",
                snapshot.Route,
                snapshot.Transmission,
                snapshot.Scale,
                snapshot.BaseVolume,
                snapshot.FinalMultiplier,
                snapshot.Distance,
                snapshot.Pressure);
        }

        private static CueSnapshot CreateSnapshot(string route, float baseVolume, float transmission, float scale, float finalMultiplier, Vector3D sourcePosition)
        {
            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            float pressure = listenerPosition == Vector3D.Zero
                ? ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition)
                : Math.Max(ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition), ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition));
            float distance = listenerPosition == Vector3D.Zero ? 0f : (float)Vector3D.Distance(listenerPosition, sourcePosition);

            return new CueSnapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                Route = route,
                BaseVolume = baseVolume,
                Transmission = transmission,
                Scale = scale,
                FinalMultiplier = finalMultiplier,
                Pressure = pressure,
                Distance = distance
            };
        }

        private static void RecordCue(string cueName, CueSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(cueName) || cueName == "NullOrEmpty")
                return;

            CueSnapshots[cueName] = snapshot;
        }

        private static float TryGetAltitude(Vector3D position)
        {
            if (position == Vector3D.Zero)
                return float.NaN;

            try
            {
                MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(position);
                if (planet == null)
                    return float.NaN;

                MethodInfo method = planet.GetType().GetMethod("GetAltitude", new[] { typeof(Vector3D) });
                if (method == null)
                    return float.NaN;

                object result = method.Invoke(planet, new object[] { position });
                return Convert.ToSingle(result, CultureInfo.InvariantCulture);
            }
            catch
            {
                return float.NaN;
            }
        }

        private static float TryGetControlledSpeed()
        {
            try
            {
                object controlled = MyAPIGateway.Session?.ControlledObject;
                if (controlled == null)
                    return 0f;

                object entity = controlled.GetType().GetProperty("Entity")?.GetValue(controlled, null) ?? controlled;
                object physics = entity.GetType().GetProperty("Physics")?.GetValue(entity, null);
                object velocity = physics?.GetType().GetProperty("LinearVelocity")?.GetValue(physics, null);
                if (velocity is Vector3 vector)
                    return vector.Length();
                if (velocity is Vector3D vectorD)
                    return (float)vectorD.Length();
            }
            catch
            {
            }

            return 0f;
        }

        private struct GlobalSnapshot
        {
            public DateTime UpdatedUtc;
            public float ListenerPressure;
            public float ListenerAltitude;
            public float ControlledSpeed;
            public bool InsideShip;
            public string EngineFilter;
            public bool AmbientEnabled;
            public bool SpatialEnabled;
        }

        public struct CueSnapshot
        {
            public DateTime UpdatedUtc;
            public string Route;
            public float BaseVolume;
            public float Transmission;
            public float Scale;
            public float FinalMultiplier;
            public float Pressure;
            public float Distance;
        }
    }
}