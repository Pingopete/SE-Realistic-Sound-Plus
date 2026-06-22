using System;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;
using VRage.Utils;

namespace RealisticSoundPlus
{
    public sealed class RealisticSoundPlusSettings
    {
        public float EngineGain { get; set; } = 4.0f;
        public float AudioCurveExponent { get; set; } = 1.9228549f;
        public float MinimumShipPresence { get; set; } = 0.35f;
        public float QuietShipForceLog10 { get; set; } = 4.0f;
        public float LoudShipForceLog10 { get; set; } = 7.0f;
        public float MufflingStrength { get; set; } = 1.0f;
        public float InteriorBaseTransmission { get; set; } = 0.2f;
        public float AtmosphericMufflingFloor { get; set; } = 0.5f;
        public string EngineFilter { get; set; } = "EngineFilter";
        public string InternalEngineFilter { get; set; } = "EngineFilter";
        public float Filter1Frequency { get; set; } = 300f;
        public float Filter1Q { get; set; } = 0.7f;
        public string Filter1Type { get; set; } = "LowPass";
        public float Filter2Frequency { get; set; } = 1200f;
        public float Filter2Q { get; set; } = 0.7f;
        public string Filter2Type { get; set; } = "LowPass";
        public bool EngineFilterDynamic { get; set; } = true;
        public bool V2AtmosphereOverrideEnabled { get; set; } = false;
        public float V2AtmosphereOverride { get; set; } = 0.971752346f;
        public float EngineFilterAirNearFrequency { get; set; } = 8000f;
        public float EngineFilterAirFarFrequency { get; set; } = 17.5603657f;
        public float EngineFilterAirRange { get; set; } = 5000f;
        public float EngineFilterAirDistanceCurve { get; set; } = 1.11341381f;
        public float EngineFilterAirQ { get; set; } = 0.751832f;
        public float EngineFilterInteriorAirWeight { get; set; } = 4.0f;
        public float EngineFilterHullNearFrequency { get; set; } = 22.340662f;
        public float EngineFilterHullFarFrequency { get; set; } = 5f;
        public float EngineFilterHullRange { get; set; } = 271.383942f;
        public float EngineFilterHullDistanceCurve { get; set; } = 1.26052666f;
        public float EngineFilterHullQ { get; set; } = 1.191213f;
        public float EngineFilterInteriorMaxFrequency { get; set; } = 8000f;
        public float EngineFilterVacuumContactFrequency { get; set; } = 20.2895f;
        public float PlayerEnvRayLength { get; set; } = 27.8737087f;
        public float PlayerEnvApertureCurve { get; set; } = 1.0102917f;
        public float PlayerEnvOcclusionCurve { get; set; } = 4.9959054f;
        public float PlayerFilterStructureThicknessScale { get; set; } = 1.15048516f;
        public float PlayerEnvStructureThicknessScale { get; set; } = 0.968652368f;
        public float PlayerFilterBlockStructureThicknessScale { get; set; } = 2.08563447f;
        public float PlayerFilterBlockOcclusionCurve { get; set; } = 0.429020733f;
        public float PlayerFilterVoxelOcclusionWeight { get; set; } = 4.13063955f;
        public float PlayerEnvSealedExtraMuffling { get; set; } = 0.5932017f;
        public float PlayerEnvSealOpenThreshold { get; set; } = 0f;
        public float PlayerFilterEnvironmentSealedFactor { get; set; } = 0.5932017f;
        public float PlayerFilterBlockSealedFactor { get; set; } = 0.5932017f;
        public bool PlayerFilterEnabled { get; set; } = true;
        public bool PlayerFilterEnvironmentEnabled { get; set; } = true;
        public bool PlayerFilterBlockEnabled { get; set; } = true;
        public bool PlayerFilterLocalEnabled { get; set; } = true;
        public bool PlayerFilterPathDebugEnabled { get; set; } = false;
        public bool PlayerFilterAtmosphereOverrideEnabled { get; set; } = false;
        public float PlayerFilterAtmosphereOverride { get; set; } = 1f;
        public float PlayerFilterOcclusionStrength { get; set; } = 1.0048033f;
        public float PlayerFilterEnvironmentVolumeMuffleWeight { get; set; } = 0.994361341f;
        public float PlayerFilterBlockVolumeMuffleWeight { get; set; } = 2.064745f;
        public float PlayerFilterLocalVolumeMuffleWeight { get; set; } = 1.0048033f;
        public float PlayerFilterEnvironmentMinGain { get; set; } = 0f;
        public float PlayerFilterEnvironmentMuffledFrequency { get; set; } = 5f;
        public float PlayerFilterBlockMuffledFrequency { get; set; } = 5f;
        public float PlayerFilterMuffledFrequency { get; set; } = 10.8494892f;
        public float PlayerFilterBlockRange { get; set; } = 1f;
        public float PlayerFilterBlockRangeScale { get; set; } = 6.80799437f;
        public float PlayerFilterBlockDistanceCurve { get; set; } = 4.11323071f;
        public float PlayerFilterSmoothingMs { get; set; } = 1000f;
        public float V2SmoothingMs { get; set; } = 237.207642f;
        public float V2DetailCommandSmoothingMs { get; set; } = 2091.426f;
        public float V2EmitterFadeInMs { get; set; } = 315.162872f;
        public float V2SoftFadeRatio { get; set; } = 0.05477318f;
        public bool V2DetailEnabled { get; set; } = true;
        public bool V2DetailIdleEnabled { get; set; } = true;
        public bool V2Detail2DPositionalTest { get; set; } = true;
        public bool V2StateEnabled { get; set; } = false;
        public bool V2State2DPositionalTest { get; set; } = false;
        public float V2DetailGain { get; set; } = 2.4459064f;
        public float V2DetailIdleGain { get; set; } = 4.0f;
        public float V2StateGain { get; set; } = 2.336257f;
        public float V2EmitterDistance { get; set; } = 325.01593f;
        public float V2DistanceCurve { get; set; } = 1.03026366f;
        public bool AudioOverlayEnabled { get; set; } = false;
        public bool FilterOverlayEnabled { get; set; } = false;
        public bool V2DebugLogEnabled { get; set; } = true;
        public bool GlobalReverbEnabled { get; set; } = true;
        public float GlobalReverbDiffusion { get; set; } = 0.172880113f;
        public float GlobalReverbRoomSize { get; set; } = 1f;
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
            return string.Format(CultureInfo.InvariantCulture,
                "route=v2, gain={0:0.00}, thrustCurve={1:0.00}, presenceMin={2:0.00}, external={3}, internal={4}, enginefilter={5}/{6:0}/{7:0.00}/dyn={8}, auxfilter={9}/{10:0}/{11:0.00}, atmOverride={12}/{13:0.00}, air={14:0}-{15:0}Hz/{16:0}m/c{17:0.00}/q{18:0.00}/inside{19:0.00}/cap{20:0}Hz/vac{21:0}Hz, hull={22:0}-{23:0}Hz/{24:0}m/c{25:0.00}/q{26:0.00}, playerEnv=ray{27:0}/ap{28:0.00}/envThick{29:0.00}m/vox{30:0.00}/sealedEnv{31:0.00}, playerFilter={32}/env{33}/block{34}/local{35}/pathdbg{36}/auxAtm{37}/{38:0.00}/occStrength{39:0.00}/volW{40:0.00}/{41:0.00}/{42:0.00}/envFloor{43:0.00}/envMuff{44:0}Hz/blockMuff{45:0}Hz/localMuff{46:0}Hz/blockThick{47:0.00}m/blockCurve{48:0.00}/sealedBlock{49:0.00}/blockFallback{50:0}m/scale{51:0.00}/c{52:0.00}/auxSmoothMs{53:0}, smoothMs={54:0}, cmdSmoothMs={55:0}, emitterFadeMs={56:0}, fade={57:0.000}, detail={58}({59:0.00}), idle={60}({61:0.00}), detail2dpos={62}, state={63}({64:0.00}), dist={65:0}, distcurve={66:0.00}, state2dpos={67}, sounds={68}, filters={69}, log={70}, reverb={71}/diff{72:0.00}/room{73:0.00}",
                Current.EngineGain,
                Current.AudioCurveExponent,
                Current.MinimumShipPresence,
                Current.EngineFilter,
                Current.InternalEngineFilter,
                Current.Filter1Type,
                Current.Filter1Frequency,
                Current.Filter1Q,
                Current.EngineFilterDynamic ? "on" : "off",
                Current.Filter2Type,
                Current.Filter2Frequency,
                Current.Filter2Q,
                Current.V2AtmosphereOverrideEnabled ? "on" : "off",
                Current.V2AtmosphereOverride,
                Current.EngineFilterAirNearFrequency,
                Current.EngineFilterAirFarFrequency,
                Current.EngineFilterAirRange,
                Current.EngineFilterAirDistanceCurve,
                Current.EngineFilterAirQ,
                Current.EngineFilterInteriorAirWeight,
                Current.EngineFilterInteriorMaxFrequency,
                Current.EngineFilterVacuumContactFrequency,
                Current.EngineFilterHullNearFrequency,
                Current.EngineFilterHullFarFrequency,
                Current.EngineFilterHullRange,
                Current.EngineFilterHullDistanceCurve,
                Current.EngineFilterHullQ,
                Current.PlayerEnvRayLength,
                Current.PlayerEnvApertureCurve,
                Current.PlayerEnvStructureThicknessScale,
                Current.PlayerFilterVoxelOcclusionWeight,
                Current.PlayerFilterEnvironmentSealedFactor,
                Current.PlayerFilterEnabled ? "on" : "off",
                Current.PlayerFilterEnvironmentEnabled ? "on" : "off",
                Current.PlayerFilterBlockEnabled ? "on" : "off",
                Current.PlayerFilterLocalEnabled ? "on" : "off",
                Current.PlayerFilterPathDebugEnabled ? "on" : "off",
                Current.PlayerFilterAtmosphereOverrideEnabled ? "on" : "off",
                Current.PlayerFilterAtmosphereOverride,
                Current.PlayerFilterOcclusionStrength,
                Current.PlayerFilterEnvironmentVolumeMuffleWeight,
                Current.PlayerFilterBlockVolumeMuffleWeight,
                Current.PlayerFilterLocalVolumeMuffleWeight,
                Current.PlayerFilterEnvironmentMinGain,
                Current.PlayerFilterEnvironmentMuffledFrequency,
                Current.PlayerFilterBlockMuffledFrequency,
                Current.PlayerFilterMuffledFrequency,
                Current.PlayerFilterBlockStructureThicknessScale,
                Current.PlayerFilterBlockOcclusionCurve,
                Current.PlayerFilterBlockSealedFactor,
                Current.PlayerFilterBlockRange,
                Current.PlayerFilterBlockRangeScale,
                Current.PlayerFilterBlockDistanceCurve,
                Current.PlayerFilterSmoothingMs,
                Current.V2SmoothingMs,
                Current.V2DetailCommandSmoothingMs,
                Current.V2EmitterFadeInMs,
                Current.V2SoftFadeRatio,
                Current.V2DetailEnabled ? "on" : "off",
                Current.V2DetailGain,
                Current.V2DetailIdleEnabled ? "on" : "off",
                Current.V2DetailIdleGain,
                Current.V2Detail2DPositionalTest ? "on" : "off",
                Current.V2StateEnabled ? "on" : "off",
                Current.V2StateGain,
                Current.V2EmitterDistance,
                Current.V2DistanceCurve,
                Current.V2State2DPositionalTest ? "on" : "off",
                Current.AudioOverlayEnabled ? "on" : "off",
                Current.FilterOverlayEnabled ? "on" : "off",
                Current.V2DebugLogEnabled ? "on" : "off",
                Current.GlobalReverbEnabled ? "on" : "off",
                Current.GlobalReverbDiffusion,
                Current.GlobalReverbRoomSize);
        }

        public static bool TryGetDefault(string name, out float value)
        {
            return TryReadFloat(new RealisticSoundPlusSettings(), name, out value);
        }

        private static bool TryReadFloat(RealisticSoundPlusSettings settings, string name, out float value)
        {
            value = 0f;
            if (settings == null || string.IsNullOrWhiteSpace(name))
                return false;

            switch (name.ToLowerInvariant())
            {
                case "gain":
                case "enginegain": value = settings.EngineGain; return true;
                case "curve":
                case "exponent":
                case "statecurve":
                case "outputcurve": value = settings.AudioCurveExponent; return true;
                case "presence":
                case "minpresence": value = settings.MinimumShipPresence; return true;
                case "quietlog":
                case "quietforce":
                case "smallforce": value = settings.QuietShipForceLog10; return true;
                case "loudlog":
                case "loudforce":
                case "largeforce": value = settings.LoudShipForceLog10; return true;
                case "filter1freq":
                case "filter1frequency":
                case "f1freq":
                case "enginefilterfreq":
                case "enginefilterfrequency":
                case "engfilterfreq":
                case "efreq": value = settings.Filter1Frequency; return true;
                case "filter1q":
                case "f1q":
                case "enginefilterq":
                case "engfilterq":
                case "eq": value = settings.Filter1Q; return true;
                case "filter2freq":
                case "filter2frequency":
                case "f2freq":
                case "auxfilterfreq":
                case "auxfilterfrequency":
                case "afreq": value = settings.Filter2Frequency; return true;
                case "filter2q":
                case "f2q":
                case "auxfilterq":
                case "aq": value = settings.Filter2Q; return true;
                case "engineairnear":
                case "enginefilterairnear":
                case "airnear": value = settings.EngineFilterAirNearFrequency; return true;
                case "engineairfar":
                case "enginefilterairfar":
                case "airfar": value = settings.EngineFilterAirFarFrequency; return true;
                case "engineairrange":
                case "enginefilterairrange":
                case "airrange": value = settings.EngineFilterAirRange; return true;
                case "engineaircurve":
                case "enginefilteraircurve":
                case "aircurve": value = settings.EngineFilterAirDistanceCurve; return true;
                case "engineairq":
                case "enginefilterairq":
                case "airq": value = settings.EngineFilterAirQ; return true;
                case "engineinteriorair":
                case "enginefilterinteriorair":
                case "interiorair":
                case "insideair":
                case "airblend": value = settings.EngineFilterInteriorAirWeight; return true;
                case "enginehullnear":
                case "enginefilterhullnear":
                case "hullnear": value = settings.EngineFilterHullNearFrequency; return true;
                case "enginehullfar":
                case "enginefilterhullfar":
                case "hullfar": value = settings.EngineFilterHullFarFrequency; return true;
                case "enginehullrange":
                case "enginefilterhullrange":
                case "hullrange": value = settings.EngineFilterHullRange; return true;
                case "enginehullcurve":
                case "enginefilterhullcurve":
                case "hullcurve": value = settings.EngineFilterHullDistanceCurve; return true;
                case "enginehullq":
                case "enginefilterhullq":
                case "hullq": value = settings.EngineFilterHullQ; return true;
                case "engineinteriorcutoff":
                case "enginefilterinteriorcutoff":
                case "interiorcutoff": value = settings.EngineFilterInteriorMaxFrequency; return true;
                case "enginevacuumcutoff":
                case "enginefiltervacuumcutoff":
                case "vacuumcutoff": value = settings.EngineFilterVacuumContactFrequency; return true;
                case "atmoverride":
                case "atmosphereoverride":
                case "externalatm":
                case "testatm": value = settings.V2AtmosphereOverride; return true;
                case "playerenvray":
                case "playerray":
                case "envray":
                case "occlusionray": value = settings.PlayerEnvRayLength; return true;
                case "playerenvcurve":
                case "playerocclusioncurve":
                case "envcurve": value = settings.PlayerEnvOcclusionCurve; return true;
                case "playerenvaperturecurve":
                case "envaperturecurve":
                case "aperturecurve":
                case "skyaperturecurve":
                case "doorcurve": value = settings.PlayerEnvApertureCurve; return true;
                case "playerblockocclusioncurve":
                case "blockocclusioncurve":
                case "blockocccurve":
                case "blockocc":
                case "occlusioncurve": value = settings.PlayerFilterBlockOcclusionCurve; return true;
                case "playerenvstructurethicknessscale":
                case "envstructurethickness":
                case "envthickness":
                case "skythickness":
                case "windthickness": value = settings.PlayerEnvStructureThicknessScale; return true;
                case "playerfilterblockstructurethicknessscale":
                case "blockstructurethickness":
                case "blockthickness":
                case "sourcepaththickness": value = settings.PlayerFilterBlockStructureThicknessScale; return true;
                case "playerfiltervoxelocclusionweight":
                case "voxelocclusionweight":
                case "voxelweight":
                case "voxelocclusion":
                case "voxelmuffle": value = settings.PlayerFilterVoxelOcclusionWeight; return true;
                case "playerfilterstructurethicknessscale":
                case "structurethickness":
                case "occlusionthickness":
                case "thicknessscale":
                case "structscale": value = settings.PlayerFilterStructureThicknessScale; return true;
                case "playersealedextra":
                case "sealedextra":
                case "sealextra": value = settings.PlayerEnvSealedExtraMuffling; return true;
                case "playerfilterenvironmentsealedfactor":
                case "environmentsealedfactor":
                case "envsealedfactor":
                case "windsealedfactor":
                case "sealedenv":
                case "sealenvironment": value = settings.PlayerFilterEnvironmentSealedFactor; return true;
                case "playerfilterblocksealedfactor":
                case "blocksealedfactor":
                case "machinesealedfactor":
                case "sealedblock":
                case "sealblock": value = settings.PlayerFilterBlockSealedFactor; return true;
                case "playerfiltermufflefreq":
                case "playerfiltermuffledfreq":
                case "auxmufflefreq":
                case "auxmuffledfreq":
                case "mufflefreq": value = settings.PlayerFilterMuffledFrequency; return true;
                case "playerfilterenvironmentmufflefreq":
                case "playerfilterenvironmentmuffledfreq":
                case "envmufflefreq":
                case "envmuffledfreq":
                case "windmufflefreq":
                case "windmuffledfreq": value = settings.PlayerFilterEnvironmentMuffledFrequency; return true;
                case "playerfilterblockmufflefreq":
                case "playerfilterblockmuffledfreq":
                case "blockmufflefreq":
                case "blockmuffledfreq":
                case "blockcutoff":
                case "blockmuffledcutoff": value = settings.PlayerFilterBlockMuffledFrequency; return true;
                case "playerfilterocclusionstrength":
                case "auxocclusionstrength":
                case "occlusionstrength":
                case "occstrength": value = settings.PlayerFilterOcclusionStrength; return true;
                case "playerfilterenvironmentvolumemuffleweight":
                case "envvolumemuffle":
                case "envvolmuffle":
                case "envvolume":
                case "envvol": value = settings.PlayerFilterEnvironmentVolumeMuffleWeight; return true;
                case "playerfilterblockvolumemuffleweight":
                case "blockvolumemuffle":
                case "blockvolmuffle":
                case "blockvolume":
                case "blockvol": value = settings.PlayerFilterBlockVolumeMuffleWeight; return true;
                case "playerfilterlocalvolumemuffleweight":
                case "localvolumemuffle":
                case "localvolmuffle":
                case "localvolume":
                case "localvol": value = settings.PlayerFilterLocalVolumeMuffleWeight; return true;
                case "playerfilterenvironmentmingain":
                case "playerfilterenvfloor":
                case "envfloor":
                case "windfloor":
                case "envmingain": value = settings.PlayerFilterEnvironmentMinGain; return true;
                case "playerfilterblockrange":
                case "blockrange":
                case "auxblockrange":
                case "blockfallbackrange": value = settings.PlayerFilterBlockRange; return true;
                case "playerfilterblockrangescale":
                case "blockrangescale":
                case "blockdistancescale":
                case "blockscale":
                case "blockdist": value = settings.PlayerFilterBlockRangeScale; return true;
                case "playerfilterblockdistancecurve":
                case "blockdistancecurve":
                case "blockcurve":
                case "blockdistcurve": value = settings.PlayerFilterBlockDistanceCurve; return true;
                case "playerfiltersmoothingms":
                case "playerfiltersmoothing":
                case "auxsmooth":
                case "auxsmoothing":
                case "filtersmooth": value = settings.PlayerFilterSmoothingMs; return true;
                case "smooth":
                case "v2smooth":
                case "smoothing": value = settings.V2SmoothingMs; return true;
                case "cmdsmooth":
                case "commandms":
                case "commandsmoothing": value = settings.V2DetailCommandSmoothingMs; return true;
                case "emitterfade":
                case "fadein":
                case "fadeinms": value = settings.V2EmitterFadeInMs; return true;
                case "fade":
                case "softfade": value = settings.V2SoftFadeRatio; return true;
                case "dist":
                case "distance":
                case "v2dist": value = settings.V2EmitterDistance; return true;
                case "distcurve":
                case "v2distcurve": value = settings.V2DistanceCurve; return true;
                case "detailgain":
                case "v2detailgain": value = settings.V2DetailGain; return true;
                case "idlegain":
                case "detailidlegain":
                case "v2idlegain": value = settings.V2DetailIdleGain; return true;
                case "stategain":
                case "v2stategain": value = settings.V2StateGain; return true;
                case "reverbdiffusion": value = settings.GlobalReverbDiffusion; return true;
                case "reverbroomsize":
                case "reverbroom": value = settings.GlobalReverbRoomSize; return true;
                default:
                    return false;
            }
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
                case "statecurve":
                case "outputcurve":
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
                case "filter1freq":
                case "filter1frequency":
                case "f1freq":
                case "enginefilterfreq":
                case "enginefilterfrequency":
                case "engfilterfreq":
                case "efreq":
                    Current.Filter1Frequency = value;
                    break;
                case "filter1q":
                case "f1q":
                case "enginefilterq":
                case "engfilterq":
                case "eq":
                    Current.Filter1Q = value;
                    break;
                case "filter2freq":
                case "filter2frequency":
                case "f2freq":
                case "auxfilterfreq":
                case "auxfilterfrequency":
                case "afreq":
                    Current.Filter2Frequency = value;
                    break;
                case "filter2q":
                case "f2q":
                case "auxfilterq":
                case "aq":
                    Current.Filter2Q = value;
                    break;
                case "engineairnear":
                case "enginefilterairnear":
                case "airnear":
                    Current.EngineFilterAirNearFrequency = value;
                    break;
                case "engineairfar":
                case "enginefilterairfar":
                case "airfar":
                    Current.EngineFilterAirFarFrequency = value;
                    break;
                case "engineairrange":
                case "enginefilterairrange":
                case "airrange":
                    Current.EngineFilterAirRange = value;
                    break;
                case "engineaircurve":
                case "enginefilteraircurve":
                case "aircurve":
                    Current.EngineFilterAirDistanceCurve = value;
                    break;
                case "engineairq":
                case "enginefilterairq":
                case "airq":
                    Current.EngineFilterAirQ = value;
                    break;
                case "engineinteriorair":
                case "enginefilterinteriorair":
                case "interiorair":
                case "insideair":
                case "airblend":
                    Current.EngineFilterInteriorAirWeight = value;
                    break;
                case "enginehullnear":
                case "enginefilterhullnear":
                case "hullnear":
                    Current.EngineFilterHullNearFrequency = value;
                    break;
                case "enginehullfar":
                case "enginefilterhullfar":
                case "hullfar":
                    Current.EngineFilterHullFarFrequency = value;
                    break;
                case "enginehullrange":
                case "enginefilterhullrange":
                case "hullrange":
                    Current.EngineFilterHullRange = value;
                    break;
                case "enginehullcurve":
                case "enginefilterhullcurve":
                case "hullcurve":
                    Current.EngineFilterHullDistanceCurve = value;
                    break;
                case "enginehullq":
                case "enginefilterhullq":
                case "hullq":
                    Current.EngineFilterHullQ = value;
                    break;
                case "engineinteriorcutoff":
                case "enginefilterinteriorcutoff":
                case "interiorcutoff":
                    Current.EngineFilterInteriorMaxFrequency = value;
                    break;
                case "enginevacuumcutoff":
                case "enginefiltervacuumcutoff":
                case "vacuumcutoff":
                    Current.EngineFilterVacuumContactFrequency = value;
                    break;
                case "atmoverride":
                case "atmosphereoverride":
                case "externalatm":
                case "testatm":
                    Current.V2AtmosphereOverride = value;
                    break;
                case "playerenvray":
                case "playerray":
                case "envray":
                case "occlusionray":
                    Current.PlayerEnvRayLength = value;
                    break;
                case "playerenvcurve":
                case "playerocclusioncurve":
                case "envcurve":
                    Current.PlayerEnvOcclusionCurve = value;
                    break;
                case "playerenvaperturecurve":
                case "envaperturecurve":
                case "aperturecurve":
                case "skyaperturecurve":
                case "doorcurve":
                    Current.PlayerEnvApertureCurve = value;
                    break;
                case "playerblockocclusioncurve":
                case "blockocclusioncurve":
                case "blockocccurve":
                case "blockocc":
                    Current.PlayerFilterBlockOcclusionCurve = value;
                    break;
                case "occlusioncurve":
                    Current.PlayerFilterBlockOcclusionCurve = value;
                    break;
                case "playerenvstructurethicknessscale":
                case "envstructurethickness":
                case "envthickness":
                case "skythickness":
                case "windthickness":
                    Current.PlayerEnvStructureThicknessScale = value;
                    break;
                case "playerfilterblockstructurethicknessscale":
                case "blockstructurethickness":
                case "blockthickness":
                case "sourcepaththickness":
                    Current.PlayerFilterBlockStructureThicknessScale = value;
                    break;
                case "playerfiltervoxelocclusionweight":
                case "voxelocclusionweight":
                case "voxelweight":
                case "voxelocclusion":
                case "voxelmuffle":
                    Current.PlayerFilterVoxelOcclusionWeight = value;
                    break;
                case "playerfilterstructurethicknessscale":
                case "structurethickness":
                case "occlusionthickness":
                case "thicknessscale":
                case "structscale":
                    Current.PlayerFilterStructureThicknessScale = value;
                    Current.PlayerEnvStructureThicknessScale = value;
                    Current.PlayerFilterBlockStructureThicknessScale = value;
                    break;
                case "playersealedextra":
                case "sealedextra":
                case "sealextra":
                    Current.PlayerEnvSealedExtraMuffling = value;
                    Current.PlayerFilterEnvironmentSealedFactor = value;
                    Current.PlayerFilterBlockSealedFactor = value;
                    break;
                case "playerfilterenvironmentsealedfactor":
                case "environmentsealedfactor":
                case "envsealedfactor":
                case "windsealedfactor":
                case "sealedenv":
                case "sealenvironment":
                    Current.PlayerFilterEnvironmentSealedFactor = value;
                    break;
                case "playerfilterblocksealedfactor":
                case "blocksealedfactor":
                case "machinesealedfactor":
                case "sealedblock":
                case "sealblock":
                    Current.PlayerFilterBlockSealedFactor = value;
                    break;
                case "playersealthreshold":
                case "sealthreshold":
                case "sealopen":
                    Current.PlayerEnvSealOpenThreshold = value;
                    break;
                case "playerfiltermufflefreq":
                case "playerfiltermuffledfreq":
                case "auxmufflefreq":
                case "auxmuffledfreq":
                case "mufflefreq":
                    Current.PlayerFilterMuffledFrequency = value;
                    break;
                case "playerfilterenvironmentmufflefreq":
                case "playerfilterenvironmentmuffledfreq":
                case "envmufflefreq":
                case "envmuffledfreq":
                case "windmufflefreq":
                case "windmuffledfreq":
                    Current.PlayerFilterEnvironmentMuffledFrequency = value;
                    break;
                case "playerfilterblockmufflefreq":
                case "playerfilterblockmuffledfreq":
                case "blockmufflefreq":
                case "blockmuffledfreq":
                case "blockcutoff":
                case "blockmuffledcutoff":
                    Current.PlayerFilterBlockMuffledFrequency = value;
                    break;
                case "playerfilterocclusionstrength":
                case "auxocclusionstrength":
                case "occlusionstrength":
                case "occstrength":
                    Current.PlayerFilterOcclusionStrength = value;
                    break;
                case "playerfilterenvironmentvolumemuffleweight":
                case "envvolumemuffle":
                case "envvolmuffle":
                case "envvolume":
                case "envvol":
                    Current.PlayerFilterEnvironmentVolumeMuffleWeight = value;
                    break;
                case "playerfilterblockvolumemuffleweight":
                case "blockvolumemuffle":
                case "blockvolmuffle":
                case "blockvolume":
                case "blockvol":
                    Current.PlayerFilterBlockVolumeMuffleWeight = value;
                    break;
                case "playerfilterlocalvolumemuffleweight":
                case "localvolumemuffle":
                case "localvolmuffle":
                case "localvolume":
                case "localvol":
                    Current.PlayerFilterLocalVolumeMuffleWeight = value;
                    break;
                case "playerfilterenvironmentmingain":
                case "playerfilterenvfloor":
                case "envfloor":
                case "windfloor":
                case "ambientfloor":
                    Current.PlayerFilterEnvironmentMinGain = value;
                    break;
                case "playerfilterblockrange":
                case "auxblockrange":
                case "blockrange":
                    Current.PlayerFilterBlockRange = value;
                    break;
                case "playerfilterblockrangescale":
                case "auxblockrangescale":
                case "blockdistancescale":
                case "blockdistance":
                case "blockdist":
                case "blocksounddistance":
                case "blocksoundscale":
                case "blockrangescale":
                case "blockscale":
                    Current.PlayerFilterBlockRangeScale = value;
                    break;
                case "playerfilterblockcurve":
                case "auxblockcurve":
                case "blockcurve":
                    Current.PlayerFilterBlockDistanceCurve = value;
                    break;
                case "playerfiltersmoothing":
                case "playerfiltersmooth":
                case "auxfiltersmoothing":
                case "auxfiltersmooth":
                case "auxsmoothing":
                case "auxsmooth":
                    Current.PlayerFilterSmoothingMs = value;
                    break;
                case "playerfilteratm":
                case "playerfilterpressure":
                case "auxatm":
                case "auxpressure":
                case "auxvacuum":
                    Current.PlayerFilterAtmosphereOverride = value;
                    break;
                case "reverbdiffusion":
                case "reverbdiff":
                case "globalreverbdiffusion":
                case "globalreverbdiff":
                    Current.GlobalReverbDiffusion = value;
                    break;
                case "reverbroomsize":
                case "reverbroom":
                case "globalreverbroomsize":
                case "globalreverbroom":
                    Current.GlobalReverbRoomSize = value;
                    break;
                case "smooth":
                case "smoothing":
                    Current.V2SmoothingMs = value;
                    break;
                case "cmdsmooth":
                case "commandsmooth":
                case "inputsmooth":
                case "thrustsmooth":
                    Current.V2DetailCommandSmoothingMs = value;
                    break;
                case "emitterfade":
                case "emitterfadein":
                case "transitionfade":
                case "transitionfadein":
                case "routefade":
                case "routefadein":
                case "contactfade":
                case "contactfadein":
                    Current.V2EmitterFadeInMs = value;
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
                case "idlegain":
                case "detailidlegain":
                case "v2idlegain":
                    Current.V2DetailIdleGain = value;
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

        public static bool TrySetInternalFilter(string value)
        {
            string normalized = NormalizeFilter(value);
            if (normalized == null)
                return false;
            Current.InternalEngineFilter = normalized;
            return true;
        }

        public static bool TrySetFilter1Type(string value)
        {
            string normalized = NormalizeCustomFilterType(value);
            if (normalized == null)
                return false;

            Current.Filter1Type = normalized;
            return true;
        }

        public static bool TrySetFilter2Type(string value)
        {
            string normalized = NormalizeCustomFilterType(value);
            if (normalized == null)
                return false;

            Current.Filter2Type = normalized;
            return true;
        }

        public static bool TrySetEngineFilterDynamic(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.EngineFilterDynamic = enabled;
            return true;
        }

        public static bool TrySetAtmosphereOverrideEnabled(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2AtmosphereOverrideEnabled = enabled;
            return true;
        }

        public static bool TrySetV2Detail(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2DetailEnabled = enabled;
            return true;
        }

        public static bool TrySetV2DetailIdle(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2DetailIdleEnabled = enabled;
            return true;
        }

        public static bool TrySetV2Detail2DPositionalTest(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2Detail2DPositionalTest = enabled;
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

        public static bool TrySetV2DebugLog(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.V2DebugLogEnabled = enabled;
            return true;
        }

        public static bool TrySetPlayerFilter(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.PlayerFilterEnabled = enabled;
            return true;
        }

        public static bool TrySetPlayerFilterEnvironment(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.PlayerFilterEnvironmentEnabled = enabled;
            return true;
        }

        public static bool TrySetPlayerFilterBlock(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.PlayerFilterBlockEnabled = enabled;
            return true;
        }

        public static bool TrySetPlayerFilterLocal(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.PlayerFilterLocalEnabled = enabled;
            return true;
        }

        public static bool TrySetPlayerFilterPathDebug(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.PlayerFilterPathDebugEnabled = enabled;
            return true;
        }

        public static bool TrySetPlayerFilterAtmosphereOverride(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.PlayerFilterAtmosphereOverrideEnabled = enabled;
            return true;
        }

        public static bool TrySetGlobalReverb(string value)
        {
            if (!TryParseBool(value, out bool enabled))
                return false;
            Current.GlobalReverbEnabled = enabled;
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

        public static string GetInternalEngineFilterEffectSubtype()
        {
            return GetFilterEffectSubtype(Current.InternalEngineFilter);
        }

        public static string GetEngineFilterEffectSignature()
        {
            return GetFilterEffectSignature(Current.EngineFilter);
        }

        public static string GetInternalEngineFilterEffectSignature()
        {
            return GetFilterEffectSignature(Current.InternalEngineFilter);
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
                case "EngineFilter": return AudioEngineV2.RspDynamicAudioFilters.EngineFilterSubtype;
                case "AuxFilter": return AudioEngineV2.RspDynamicAudioFilters.AuxFilterSubtype;
                default: return null;
            }
        }

        private static string GetFilterEffectSignature(string filter)
        {
            switch (NormalizeFilter(filter))
            {
                case "EngineFilter":
                    return string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.###}:{3:0.###}:{4}:{5:0.###}:{6:0.###}:{7:0.###}:{8:0.###}:{9:0.###}:{10:0.###}:{11:0.###}:{12:0.###}:{13:0.###}:{14:0.###}:{15:0.###}:{16:0.###}:{17:0.###}", AudioEngineV2.RspDynamicAudioFilters.EngineFilterSubtype, Current.Filter1Type, Current.Filter1Frequency, Current.Filter1Q, Current.EngineFilterDynamic ? "dyn" : "static", Current.EngineFilterAirNearFrequency, Current.EngineFilterAirFarFrequency, Current.EngineFilterAirRange, Current.EngineFilterAirDistanceCurve, Current.EngineFilterAirQ, Current.EngineFilterInteriorAirWeight, Current.EngineFilterHullNearFrequency, Current.EngineFilterHullFarFrequency, Current.EngineFilterHullRange, Current.EngineFilterHullDistanceCurve, Current.EngineFilterHullQ, Current.EngineFilterInteriorMaxFrequency, Current.EngineFilterVacuumContactFrequency);
                case "AuxFilter":
                    return string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0.###}:{3:0.###}", AudioEngineV2.RspDynamicAudioFilters.AuxFilterSubtype, Current.Filter2Type, Current.Filter2Frequency, Current.Filter2Q);
                default:
                    return GetFilterEffectSubtype(filter) ?? string.Empty;
            }
        }

        public static string FilterOptions => "off, helmet, cockpit, cockpitnooxy, realship, deep, enginefilter, auxfilter";

        public static string CustomFilterTypeOptions => "lowpass, highpass, bandpass, notch";

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
                case "filter1":
                case "enginefilter":
                case "engine":
                case "custom1":
                case "rsp1": return "EngineFilter";
                case "filter2":
                case "auxfilter":
                case "aux":
                case "custom2":
                case "rsp2": return "AuxFilter";
                default: return null;
            }
        }

        private static string NormalizeCustomFilterType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "lowpass":
                case "low":
                case "lp": return "LowPass";
                case "highpass":
                case "high":
                case "hp": return "HighPass";
                case "bandpass":
                case "band":
                case "bp": return "BandPass";
                case "notch":
                case "reject":
                case "bandreject": return "Notch";
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
            Current.InternalEngineFilter = NormalizeFilter(Current.InternalEngineFilter) ?? "Off";
            Current.Filter1Frequency = Clamp(Current.Filter1Frequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.Filter1Q = Clamp(Current.Filter1Q, AudioEngineV2.RspDynamicAudioFilters.MinFilterQ, AudioEngineV2.RspDynamicAudioFilters.MaxFilterQ);
            Current.Filter1Type = NormalizeCustomFilterType(Current.Filter1Type) ?? "LowPass";
            Current.Filter2Frequency = Clamp(Current.Filter2Frequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.Filter2Q = Clamp(Current.Filter2Q, AudioEngineV2.RspDynamicAudioFilters.MinFilterQ, AudioEngineV2.RspDynamicAudioFilters.MaxFilterQ);
            Current.Filter2Type = NormalizeCustomFilterType(Current.Filter2Type) ?? "LowPass";
            Current.V2AtmosphereOverride = Clamp(Current.V2AtmosphereOverride, 0f, 1f);
            Current.EngineFilterAirNearFrequency = Clamp(Current.EngineFilterAirNearFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.EngineFilterAirFarFrequency = Clamp(Current.EngineFilterAirFarFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.EngineFilterAirRange = Clamp(Current.EngineFilterAirRange, 1f, 5000f);
            Current.EngineFilterAirDistanceCurve = Clamp(Current.EngineFilterAirDistanceCurve, 0.1f, 5f);
            Current.EngineFilterAirQ = Clamp(Current.EngineFilterAirQ, AudioEngineV2.RspDynamicAudioFilters.MinFilterQ, AudioEngineV2.RspDynamicAudioFilters.MaxFilterQ);
            Current.EngineFilterInteriorAirWeight = Clamp(Current.EngineFilterInteriorAirWeight, 0f, 4f);
            Current.EngineFilterHullNearFrequency = Clamp(Current.EngineFilterHullNearFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.EngineFilterHullFarFrequency = Clamp(Current.EngineFilterHullFarFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.EngineFilterHullRange = Clamp(Current.EngineFilterHullRange, 1f, 1000f);
            Current.EngineFilterHullDistanceCurve = Clamp(Current.EngineFilterHullDistanceCurve, 0.1f, 5f);
            Current.EngineFilterHullQ = Clamp(Current.EngineFilterHullQ, AudioEngineV2.RspDynamicAudioFilters.MinFilterQ, AudioEngineV2.RspDynamicAudioFilters.MaxFilterQ);
            Current.EngineFilterInteriorMaxFrequency = Clamp(Current.EngineFilterInteriorMaxFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.EngineFilterVacuumContactFrequency = Clamp(Current.EngineFilterVacuumContactFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.PlayerEnvRayLength = Clamp(Current.PlayerEnvRayLength, 5f, 1000f);
            Current.PlayerEnvApertureCurve = Clamp(Current.PlayerEnvApertureCurve, 0.1f, 10f);
            Current.PlayerEnvOcclusionCurve = Clamp(Current.PlayerEnvOcclusionCurve, 0.1f, 5f);
            Current.PlayerFilterStructureThicknessScale = Clamp(Current.PlayerFilterStructureThicknessScale, 0.1f, 20f);
            if (Current.PlayerEnvStructureThicknessScale <= 0f)
                Current.PlayerEnvStructureThicknessScale = Current.PlayerFilterStructureThicknessScale;
            if (Current.PlayerFilterBlockStructureThicknessScale <= 0f)
                Current.PlayerFilterBlockStructureThicknessScale = Current.PlayerFilterStructureThicknessScale;
            if (Current.PlayerFilterBlockOcclusionCurve <= 0f)
                Current.PlayerFilterBlockOcclusionCurve = Current.PlayerEnvOcclusionCurve;
            Current.PlayerEnvStructureThicknessScale = Clamp(Current.PlayerEnvStructureThicknessScale, 0.1f, 20f);
            Current.PlayerFilterBlockStructureThicknessScale = Clamp(Current.PlayerFilterBlockStructureThicknessScale, 0.1f, 20f);
            Current.PlayerFilterBlockOcclusionCurve = Clamp(Current.PlayerFilterBlockOcclusionCurve, 0.1f, 5f);
            Current.PlayerFilterVoxelOcclusionWeight = Clamp(Current.PlayerFilterVoxelOcclusionWeight, 0f, 10f);
            Current.PlayerEnvSealedExtraMuffling = Clamp(Current.PlayerEnvSealedExtraMuffling, 0f, 1f);
            Current.PlayerEnvSealOpenThreshold = Clamp(Current.PlayerEnvSealOpenThreshold, 0f, 1f);
            if (Current.PlayerFilterEnvironmentSealedFactor < 0f)
                Current.PlayerFilterEnvironmentSealedFactor = Current.PlayerEnvSealedExtraMuffling;
            if (Current.PlayerFilterBlockSealedFactor < 0f)
                Current.PlayerFilterBlockSealedFactor = Current.PlayerEnvSealedExtraMuffling;
            Current.PlayerFilterEnvironmentSealedFactor = Clamp(Current.PlayerFilterEnvironmentSealedFactor, 0f, 1f);
            Current.PlayerFilterBlockSealedFactor = Clamp(Current.PlayerFilterBlockSealedFactor, 0f, 1f);
            Current.PlayerFilterAtmosphereOverride = Clamp(Current.PlayerFilterAtmosphereOverride, 0f, 1f);
            Current.PlayerFilterOcclusionStrength = Clamp(Current.PlayerFilterOcclusionStrength, 0f, 4f);
            Current.PlayerFilterEnvironmentVolumeMuffleWeight = Clamp(Current.PlayerFilterEnvironmentVolumeMuffleWeight, 0f, 4f);
            Current.PlayerFilterBlockVolumeMuffleWeight = Clamp(Current.PlayerFilterBlockVolumeMuffleWeight, 0f, 4f);
            Current.PlayerFilterLocalVolumeMuffleWeight = Clamp(Current.PlayerFilterLocalVolumeMuffleWeight, 0f, 4f);
            Current.PlayerFilterEnvironmentMinGain = Clamp(Current.PlayerFilterEnvironmentMinGain, 0f, 0.5f);
            Current.PlayerFilterEnvironmentMuffledFrequency = Clamp(Current.PlayerFilterEnvironmentMuffledFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.PlayerFilterBlockMuffledFrequency = Clamp(Current.PlayerFilterBlockMuffledFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.PlayerFilterMuffledFrequency = Clamp(Current.PlayerFilterMuffledFrequency, AudioEngineV2.RspDynamicAudioFilters.MinFilterFrequency, AudioEngineV2.RspDynamicAudioFilters.MaxFilterFrequency);
            Current.PlayerFilterBlockRange = Clamp(Current.PlayerFilterBlockRange, 1f, 1000f);
            Current.PlayerFilterBlockRangeScale = Clamp(Current.PlayerFilterBlockRangeScale, 0.1f, 100f);
            Current.PlayerFilterBlockDistanceCurve = Clamp(Current.PlayerFilterBlockDistanceCurve, 0.1f, 5f);
            Current.PlayerFilterSmoothingMs = Clamp(Current.PlayerFilterSmoothingMs, 0f, 5000f);
            Current.V2SmoothingMs = Clamp(Current.V2SmoothingMs, 0f, 500f);
            Current.V2DetailCommandSmoothingMs = Clamp(Current.V2DetailCommandSmoothingMs, 0f, 5000f);
            Current.V2EmitterFadeInMs = Clamp(Current.V2EmitterFadeInMs, 0f, 1000f);
            Current.V2SoftFadeRatio = Clamp(Current.V2SoftFadeRatio, 0.001f, 0.25f);
            Current.V2DetailGain = Clamp(Current.V2DetailGain, 0f, 4f);
            Current.V2DetailIdleGain = Clamp(Current.V2DetailIdleGain, 0f, 4f);
            Current.V2StateGain = Clamp(Current.V2StateGain, 0f, 4f);
            Current.V2EmitterDistance = Clamp(Current.V2EmitterDistance, 1f, 1000f);
            Current.V2DistanceCurve = Clamp(Current.V2DistanceCurve, 0.1f, 5f);
            Current.GlobalReverbDiffusion = Clamp(Current.GlobalReverbDiffusion, 0f, 1f);
            Current.GlobalReverbRoomSize = Clamp(Current.GlobalReverbRoomSize, 0f, 1f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
