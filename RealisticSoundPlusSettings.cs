using System;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;
using VRage.Utils;

namespace RealisticSoundPlus
{
    public sealed class RealisticSoundPlusSettings
    {
        public float EngineGain { get; set; } = 2.0f;
        public float AudioCurveExponent { get; set; } = 1.0f;
        public float MinimumShipPresence { get; set; } = 0.35f;
        public float QuietShipForceLog10 { get; set; } = 4.0f;
        public float LoudShipForceLog10 { get; set; } = 7.0f;
        public float MufflingStrength { get; set; } = 1.0f;
        public float InteriorBaseTransmission { get; set; } = 0.2f;
        public float AtmosphericMufflingFloor { get; set; } = 0.5f;
        public string EngineFilter { get; set; } = "Deep";
        public float V2SmoothingMs { get; set; } = 100f;
        public float V2SoftFadeRatio { get; set; } = 0.04f;
        public bool V2DetailEnabled { get; set; } = true;
        public bool V2StateEnabled { get; set; } = true;
        public bool V2State2DPositionalTest { get; set; }
        public float V2DetailGain { get; set; } = 2.0f;
        public float V2StateGain { get; set; } = 2.0f;
        public float V2EmitterDistance { get; set; } = 200f;
        public float V2DistanceCurve { get; set; } = 1.0f;
    }

    internal static class SettingsManager
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(RealisticSoundPlusSettings));
        private static DateTime _lastWriteUtc;

        public static RealisticSoundPlusSettings Current { get; private set; } = new RealisticSoundPlusSettings();

        public static string ConfigPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "RealisticSoundPlus.xml");

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
                Current = (RealisticSoundPlusSettings)Serializer.Deserialize(stream);
            ApplyLiveV2TestDefaults();
            Clamp();
            _lastWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Loaded settings from " + ConfigPath + ": " + Summary());
        }

        public static void Save()
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            using (FileStream stream = File.Create(ConfigPath))
                Serializer.Serialize(stream, Current);
            _lastWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
            MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Saved settings to " + ConfigPath + ": " + Summary());
        }

        public static string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture, "route=v2, gain={0:0.00}, curve={1:0.00}, presenceMin={2:0.00}, quietLog={3:0.00}, loudLog={4:0.00}, muffling={5:0.00}, interiorBase={6:0.00}, filter={7}, smoothMs={8:0}, fade={9:0.000}, atmosphereFloor={10:0.00}, detail={11}({12:0.00}), state={13}({14:0.00}), dist={15:0}, distcurve={16:0.00}, state2dpos={17}", Current.EngineGain, Current.AudioCurveExponent, Current.MinimumShipPresence, Current.QuietShipForceLog10, Current.LoudShipForceLog10, Current.MufflingStrength, Current.InteriorBaseTransmission, Current.EngineFilter, Current.V2SmoothingMs, Current.V2SoftFadeRatio, Current.AtmosphericMufflingFloor, Current.V2DetailEnabled ? "on" : "off", Current.V2DetailGain, Current.V2StateEnabled ? "on" : "off", Current.V2StateGain, Current.V2EmitterDistance, Current.V2DistanceCurve, Current.V2State2DPositionalTest ? "on" : "off");
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
                case "presence":
                case "minpresence":
                    Current.MinimumShipPresence = value;
                    break;
                case "quietlog":
                case "quietforce":
                case "smallforce":
                    Current.QuietShipForceLog10 = value;
                    break;
                case "loudlog":
                case "loudforce":
                case "largeforce":
                    Current.LoudShipForceLog10 = value;
                    break;
                case "muffling":
                case "muffle":
                    Current.MufflingStrength = value;
                    break;
                case "interior":
                case "interiorbase":
                    Current.InteriorBaseTransmission = value;
                    break;
                case "atmospherefloor":
                case "atmosphericfloor":
                case "atmfloor":
                    Current.AtmosphericMufflingFloor = value;
                    break;
                case "smooth":
                case "smoothing":
                    Current.V2SmoothingMs = value;
                    break;
                case "fade":
                case "softfade":
                    Current.V2SoftFadeRatio = value;
                    break;
                case "dist":
                case "v2dist":
                case "emitterdist":
                    Current.V2EmitterDistance = value;
                    break;
                case "distcurve":
                case "v2distcurve":
                    Current.V2DistanceCurve = value;
                    break;
                case "detailgain":
                case "v2detailgain":
                    Current.V2DetailGain = value;
                    break;
                case "stategain":
                case "v2stategain":
                    Current.V2StateGain = value;
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

        public static bool TrySetV2Detail(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2DetailEnabled = enabled;
            return true;
        }

        public static bool TrySetV2State(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2StateEnabled = enabled;
            return true;
        }

        public static bool TrySetV2State2DPositionalTest(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2State2DPositionalTest = enabled;
            return true;
        }

        private static bool TryParseBool(string value, out bool enabled)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "on":
                case "1":
                case "true":
                case "yes":
                    enabled = true;
                    return true;
                case "off":
                case "0":
                case "false":
                case "no":
                    enabled = false;
                    return true;
                default:
                    enabled = false;
                    return false;
            }
        }

        public static string GetEngineFilterEffectSubtype()
        {
            return GetFilterEffectSubtype(Current.EngineFilter);
        }

        private static string GetFilterEffectSubtype(string filter)
        {
            switch (NormalizeFilter(filter))
            {
                case "Helmet": return "LowPassHelmet";
                case "Cockpit": return "LowPassCockpit";
                case "CockpitNoOxy": return "LowPassCockpitNoOxy";
                case "RealShip": return "realShipFilter";
                case "Deep": return "LowPassNoHelmetNoOxy";
                default: return null;
            }
        }

        public static string FilterOptions => "off, helmet, cockpit, cockpitnooxy, realship, deep";

        private static string NormalizeFilter(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "off":
                case "none": return "Off";
                case "helmet":
                case "light": return "Helmet";
                case "cockpit":
                case "medium": return "Cockpit";
                case "cockpitnooxy":
                case "nooxy":
                case "heavy": return "CockpitNoOxy";
                case "realship":
                case "ship": return "RealShip";
                case "deep":
                case "lowpass": return "Deep";
                default: return null;
            }
        }

        private static void Clamp()
        {
            Current.EngineGain = Clamp(Current.EngineGain, 0f, 4f);
            Current.AudioCurveExponent = Clamp(Current.AudioCurveExponent, 0.25f, 10f);
            Current.MinimumShipPresence = Clamp(Current.MinimumShipPresence, 0f, 1f);
            Current.QuietShipForceLog10 = Clamp(Current.QuietShipForceLog10, 1f, 10f);
            Current.LoudShipForceLog10 = Math.Max(Current.QuietShipForceLog10 + 0.1f, Clamp(Current.LoudShipForceLog10, 1f, 12f));
            Current.MufflingStrength = Clamp(Current.MufflingStrength, 0f, 1f);
            Current.InteriorBaseTransmission = Clamp(Current.InteriorBaseTransmission, 0.05f, 1f);
            Current.AtmosphericMufflingFloor = Clamp(Current.AtmosphericMufflingFloor, 0f, 1f);
            Current.EngineFilter = NormalizeFilter(Current.EngineFilter) ?? "Off";
            Current.V2SmoothingMs = Clamp(Current.V2SmoothingMs, 0f, 500f);
            Current.V2SoftFadeRatio = Clamp(Current.V2SoftFadeRatio, 0.001f, 0.25f);
            Current.V2DetailGain = Clamp(Current.V2DetailGain, 0f, 4f);
            Current.V2StateGain = Clamp(Current.V2StateGain, 0f, 4f);
            Current.V2EmitterDistance = Clamp(Current.V2EmitterDistance, 1f, 1000f);
            Current.V2DistanceCurve = Clamp(Current.V2DistanceCurve, 0.1f, 5f);
        }

        private static void ApplyLiveV2TestDefaults()
        {
            if (Current.EngineGain <= 1.0f)
                Current.EngineGain = 2.0f;
            if (Current.V2DetailGain <= 1.0f)
                Current.V2DetailGain = 2.0f;
            if (Current.V2StateGain <= 1.0f)
                Current.V2StateGain = 2.0f;
            if (Current.V2EmitterDistance <= 36.0f)
                Current.V2EmitterDistance = 200.0f;
            if (string.Equals(Current.EngineFilter, "RealShip", StringComparison.OrdinalIgnoreCase))
                Current.EngineFilter = "Deep";
            Current.V2DetailEnabled = true;
            Current.V2StateEnabled = true;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
