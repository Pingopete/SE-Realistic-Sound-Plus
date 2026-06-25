using System;
using System.Globalization;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal struct V2LiveReverbParameters
    {
        public string Source;
        public float EquivalentRadius;
        public float RoomSize;
        public float Diffusion;
        public float Density;
        public float DecaySeconds;
        public float EarlyGainDb;
        public float TailGainDb;
        public float PredelayMs;
        public float LateDelayMs;
        public float ToneHz;
        public float HighFrequencyDb;
        public float AirPressure;
        public float WetSend;
        public float ApertureFraction;
        public float StructuralOcclusion;
        public float FinalMuffling;
        public float ClosedFraction;

        public string ToStatus()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "room={0:0.00} radius={1:0.0}m decay={2:0.0}s pre={3:0}ms late={4:0}ms diff={5:0.00} dens={6:0}% cutoff={7:0}Hz pressure={8:0.00} wet={9:0.00} src={10}",
                RoomSize,
                EquivalentRadius,
                DecaySeconds,
                PredelayMs,
                LateDelayMs,
                Diffusion,
                Density,
                ToneHz,
                AirPressure,
                WetSend,
                Source ?? "?");
        }
    }
}
