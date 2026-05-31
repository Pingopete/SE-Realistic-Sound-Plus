using System;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;
using VRage.Utils;

namespace RealisticSoundPlus
{
    public sealed class RealisticSoundPlusSettings
    {
        public float EngineGain { get; set; } = 1.35f;
        public float AudioCurveExponent { get; set; } = 0.72f;
        public float ControlInfluence { get; set; } = 0.3f;
        public float MinimumShipPresence { get; set; } = 0.35f;
        public float QuietShipForceLog10 { get; set; } = 4.0f;
        public float LoudShipForceLog10 { get; set; } = 7.0f;
        public float MufflingStrength { get; set; } = 1.0f;
        public float InteriorBaseTransmission { get; set; } = 0.82f;
        public float NearDistance { get; set; } = 4f;
        public float FarDistance { get; set; } = 36f;
        public float FarDistanceTransmission { get; set; } = 0.52f;
        public string EngineFilter { get; set; } = "Off";
    }

    internal static class SettingsManager
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RealisticSoundPlusSettings));
        private static DateTime _lastWriteUtc;

        public static RealisticSoundPlusSettings Current { get; private set; } = new RealisticSoundPlusSettings();

        public static string ConfigPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpaceEngineers",
            "RealisticSoundPlus.xml");

        public static void LoadOrCreate()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));

            if (!File.Exists(ConfigPath))
            {
                Save();
                return;
            }

            Load();
        }

        public static bool ReloadIfChanged()
        {
            if (!File.Exists(ConfigPath))
                return false;

            DateTime writeUtc = File.GetLastWriteTimeUtc(ConfigPath);
            if (writeUtc <= _lastWriteUtc)
                return false;

            Load();
            return true;
        }

        public static void Load()
        {
            using (FileStream stream = File.OpenRead(ConfigPath))
            {
                Current = (RealisticSoundPlusSettings)Serializer.Deserialize(stream);
            }

            Clamp();
            _lastWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Loaded settings from " + ConfigPath + ": " + Summary());
        }

        public static void Save()
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));

            using (FileStream stream = File.Create(ConfigPath))
            {
                Serializer.Serialize(stream, Current);
            }

            _lastWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Saved settings to " + ConfigPath + ": " + Summary());
        }

        public static string Summary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "gain={0:0.00}, curve={1:0.00}, control={2:0.00}, presenceMin={3:0.00}, muffling={4:0.00}, interiorBase={5:0.00}, farTransmission={6:0.00}, filter={7}",
                Current.EngineGain,
                Current.AudioCurveExponent,
                Current.ControlInfluence,
                Current.MinimumShipPresence,
                Current.MufflingStrength,
                Current.InteriorBaseTransmission,
                Current.FarDistanceTransmission,
                Current.EngineFilter);
        }

        public static bool TrySet(string name, float value)
        {
            switch (name.ToLowerInvariant())
            {
                case "gain":
                case "enginegain":
                    Current.EngineGain = value;
                    break;
                case "curve":
                case "exponent":
                    Current.AudioCurveExponent = value;
                    break;
                case "control":
                case "controlinfluence":
                    Current.ControlInfluence = value;
                    break;
                case "presence":
                case "minpresence":
                    Current.MinimumShipPresence = value;
                    break;
                case "muffling":
                case "muffle":
                    Current.MufflingStrength = value;
                    break;
                case "interior":
                case "interiorbase":
                    Current.InteriorBaseTransmission = value;
                    break;
                case "far":
                case "fartransmission":
                    Current.FarDistanceTransmission = value;
                    break;
                default:
                    return false;
            }

            Clamp();
            return true;
        }


        public static bool TrySetFilter(string value)
        {
            string normalized = NormalizeFilter(value);
            if (normalized == null)
                return false;

            Current.EngineFilter = normalized;
            return true;
        }

        public static string GetEngineFilterEffectSubtype()
        {
            switch (NormalizeFilter(Current.EngineFilter))
            {
                case "Helmet":
                    return "LowPassHelmet";
                case "Cockpit":
                    return "LowPassCockpit";
                case "CockpitNoOxy":
                    return "LowPassCockpitNoOxy";
                case "RealShip":
                    return "realShipFilter";
                case "Deep":
                    return "LowPass";
                default:
                    return null;
            }
        }

        public static string FilterOptions => "off, helmet, cockpit, cockpitnooxy, realship, deep";

        private static string NormalizeFilter(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                    return "Off";
                case "helmet":
                case "light":
                    return "Helmet";
                case "cockpit":
                case "medium":
                    return "Cockpit";
                case "cockpitnooxy":
                case "nooxy":
                case "heavy":
                    return "CockpitNoOxy";
                case "realship":
                case "ship":
                    return "RealShip";
                case "deep":
                case "lowpass":
                    return "Deep";
                default:
                    return null;
            }
        }
        private static void Clamp()
        {
            Current.EngineGain = Clamp(Current.EngineGain, 0f, 4f);
            Current.AudioCurveExponent = Clamp(Current.AudioCurveExponent, 0.25f, 2f);
            Current.ControlInfluence = Clamp(Current.ControlInfluence, 0f, 1f);
            Current.MinimumShipPresence = Clamp(Current.MinimumShipPresence, 0f, 1f);
            Current.QuietShipForceLog10 = Clamp(Current.QuietShipForceLog10, 1f, 10f);
            Current.LoudShipForceLog10 = Math.Max(Current.QuietShipForceLog10 + 0.1f, Clamp(Current.LoudShipForceLog10, 1f, 12f));
            Current.MufflingStrength = Clamp(Current.MufflingStrength, 0f, 1f);
            Current.InteriorBaseTransmission = Clamp(Current.InteriorBaseTransmission, 0.05f, 1f);
            Current.NearDistance = Clamp(Current.NearDistance, 0f, 100f);
            Current.FarDistance = Math.Max(Current.NearDistance + 1f, Clamp(Current.FarDistance, 1f, 500f));
            Current.FarDistanceTransmission = Clamp(Current.FarDistanceTransmission, 0.05f, 1f);
            Current.EngineFilter = NormalizeFilter(Current.EngineFilter) ?? "Off";
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;

            return value > max ? max : value;
        }
    }
}