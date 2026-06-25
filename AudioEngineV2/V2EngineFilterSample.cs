using System.Globalization;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2EngineFilterSample
    {
        public string Label;
        public string Route;
        public string DominantPath;
        public float Distance;
        public float ListenerAtmosphere;
        public float SourceAtmosphere;
        public float AirPressure;
        public float AirWeight;
        public float HullWeight;
        public float AirTransmission;
        public float AirEnvironmentOcclusion;
        public bool AirEnvironmentOcclusionActive;
        public float AirCutoff;
        public float HullCutoff;
        public float FinalCutoff;
        public float FinalQ;
        public float AirDistanceGain;
        public float HullDistanceGain;
        public float DistanceGain;
        public bool Inside;
        public bool Contact;
        public bool Fallback;

        public string FormatShort()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} d={1:0}m p={2:0.00} air={3:0.00} hull={4:0.00} tr={13:0.00} envOcc={14} airCut={5:0}Hz hullCut={6:0}Hz final={7:0}Hz q={8:0.00} dg={9:0.00} airD={10:0.00} hullD={11:0.00} {12}",
                string.IsNullOrWhiteSpace(Label) ? "engine" : Label,
                Distance,
                AirPressure,
                AirWeight,
                HullWeight,
                AirCutoff,
                HullCutoff,
                FinalCutoff,
                FinalQ,
                DistanceGain,
                AirDistanceGain,
                HullDistanceGain,
                DominantPath ?? "?",
                AirTransmission,
                AirEnvironmentOcclusionActive ? AirEnvironmentOcclusion.ToString("0.00", CultureInfo.InvariantCulture) : "-");
        }
    }
}
