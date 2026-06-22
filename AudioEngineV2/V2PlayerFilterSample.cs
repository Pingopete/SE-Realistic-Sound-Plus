using System;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2PlayerFilterSample
    {
        public DateTime UpdatedUtc;
        public string Category;
        public string CueName;
        public float Score;
        public float Distance;
        public float Muffle;
        public float Frequency;
        public float Q;
        public float LocalAtmosphere;
        public float OpenFraction;
        public float VanillaMaxDistance;
        public float EffectiveRange;
        public float RangeScale;
        public bool CustomRangeApplied;
        public float VolumeGain;
        public float VoiceVolume;
        public float VoiceMultiplier;
        public float BaseVoiceMultiplier;
        public float TargetMultiplier;
        public float EffectiveOutput;
        public float RequestedOutput;
        public bool EnvironmentCarrierForced;
        public bool EnvironmentCarrierUnavailable;
        public bool Applied;
    }
}
