using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sandbox.Definitions;
using VRage.Game;
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

        public static GlobalSnapshot Global { get; private set; }

        public static void ResetRuntimeState()
        {
            CueSnapshots.Clear();
            Global = default(GlobalSnapshot);
        }

        public static void UpdateGlobal(MyCubeGrid grid, float windScale, bool hasEnginePower, bool inVacuum, bool lowSpeed)
        {
            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            float listenerPressure = ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition);
            float gridPressure = grid != null ? ExteriorSoundTransmission.GetAtmosphericPressure(grid.WorldMatrix.Translation) : 0f;
            float speed = grid?.Physics != null ? (float)grid.Physics.LinearVelocity.Length() : 0f;
            float maxSpeed = grid != null ? GetWorldMaxShipSpeed(grid) : 0f;

            Global = new GlobalSnapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                ListenerPressure = listenerPressure,
                GridPressure = gridPressure,
                ListenerAltitude = TryGetAltitude(listenerPosition),
                GridSpeed = speed,
                MaxSpeed = maxSpeed,
                SpeedScale = maxSpeed > 0f ? Clamp01(speed / maxSpeed) : 0f,
                WindScale = windScale,
                InsideShip = ExteriorSoundTransmission.IsListenerInsideShip(),
                HasEnginePower = hasEnginePower,
                InVacuum = inVacuum,
                LowSpeed = lowSpeed,
                EngineFilter = SettingsManager.Current.EngineFilter,
                SpeedFilter = SettingsManager.Current.SpeedAmbientFilter
            };
        }

        public static void RecordEmitter(MyEntity3DSoundEmitter emitter, string route, float baseVolume, float transmission, float atmosphereGain, float scale, float finalMultiplier, Vector3D sourcePosition, float windScale)
        {
            if (emitter == null)
                return;

            CueSnapshot snapshot = CreateSnapshot(route, baseVolume, transmission, atmosphereGain, scale, finalMultiplier, sourcePosition, windScale);
            RecordCue(emitter.SoundId.ToString(), snapshot);
            RecordCue(emitter.Sound?.CueEnum.ToString(), snapshot);
            RecordCue(emitter.SecondarySound?.CueEnum.ToString(), snapshot);
        }

        public static bool TryGetCueSnapshot(string cueName, out CueSnapshot snapshot)
        {
            snapshot = default(CueSnapshot);
            if (string.IsNullOrWhiteSpace(cueName) || !CueSnapshots.TryGetValue(cueName, out snapshot))
                return false;

            if (DateTime.UtcNow - snapshot.UpdatedUtc > CueLifetime)
                return false;

            return true;
        }

        public static string FormatGlobal()
        {
            GlobalSnapshot snapshot = Global;
            string altitude = float.IsNaN(snapshot.ListenerAltitude)
                ? "alt=?"
                : string.Format(CultureInfo.InvariantCulture, "alt={0:0}m", snapshot.ListenerAltitude);

            return string.Format(
                CultureInfo.InvariantCulture,
                "atmL={0:0.00} atmG={1:0.00} {2} spd={3:0}/{4:0} wind={5:0.00} inside={6} power={7} vac={8} low={9} filter={10}/{11}",
                snapshot.ListenerPressure,
                snapshot.GridPressure,
                altitude,
                snapshot.GridSpeed,
                snapshot.MaxSpeed,
                snapshot.WindScale,
                snapshot.InsideShip ? "Y" : "N",
                snapshot.HasEnginePower ? "Y" : "N",
                snapshot.InVacuum ? "Y" : "N",
                snapshot.LowSpeed ? "Y" : "N",
                snapshot.EngineFilter,
                snapshot.SpeedFilter);
        }

        public static string FormatCue(CueSnapshot snapshot)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                " {0} tr={1:0.00} atm={2:0.00} sc={3:0.00} base={4:0.00} fin={5:0.00} d={6:0} p={7:0.00}",
                snapshot.Route,
                snapshot.Transmission,
                snapshot.AtmosphereGain,
                snapshot.Scale,
                snapshot.BaseVolume,
                snapshot.FinalMultiplier,
                snapshot.Distance,
                snapshot.Pressure);
        }

        private static CueSnapshot CreateSnapshot(string route, float baseVolume, float transmission, float atmosphereGain, float scale, float finalMultiplier, Vector3D sourcePosition, float windScale)
        {
            Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
            float pressure = listenerPosition == Vector3D.Zero
                ? ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition)
                : ExteriorSoundTransmission.GetListenerSourceAtmosphericPressure(listenerPosition, sourcePosition);
            float distance = listenerPosition == Vector3D.Zero ? 0f : (float)Vector3D.Distance(listenerPosition, sourcePosition);

            return new CueSnapshot
            {
                UpdatedUtc = DateTime.UtcNow,
                Route = route,
                BaseVolume = baseVolume,
                Transmission = transmission,
                AtmosphereGain = atmosphereGain,
                Scale = scale,
                FinalMultiplier = finalMultiplier,
                Pressure = pressure,
                Distance = distance,
                WindScale = windScale
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

        private static float GetWorldMaxShipSpeed(MyCubeGrid grid)
        {
            try
            {
                var environment = MyDefinitionManager.Static?.EnvironmentDefinition;
                if (environment != null)
                    return Math.Max(grid.GridSizeEnum == MyCubeSize.Small ? environment.SmallShipMaxSpeed : environment.LargeShipMaxSpeed, 1f);
            }
            catch
            {
            }

            return grid.GridSizeEnum == MyCubeSize.Small ? 100f : 150f;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }

        public struct GlobalSnapshot
        {
            public DateTime UpdatedUtc;
            public float ListenerPressure;
            public float GridPressure;
            public float ListenerAltitude;
            public float GridSpeed;
            public float MaxSpeed;
            public float SpeedScale;
            public float WindScale;
            public bool InsideShip;
            public bool HasEnginePower;
            public bool InVacuum;
            public bool LowSpeed;
            public string EngineFilter;
            public string SpeedFilter;
        }

        public struct CueSnapshot
        {
            public DateTime UpdatedUtc;
            public string Route;
            public float BaseVolume;
            public float Transmission;
            public float AtmosphereGain;
            public float Scale;
            public float FinalMultiplier;
            public float Pressure;
            public float Distance;
            public float WindScale;
        }
    }
}