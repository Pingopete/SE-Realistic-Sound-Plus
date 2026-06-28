using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2EngineFilterTelemetry
    {
        private const int MaxSamples = 16;
        private static readonly Dictionary<string, V2EngineFilterSample> Samples = new Dictionary<string, V2EngineFilterSample>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Order = new List<string>();
        private static V2EngineFilterSample _representative;
        private static DateTime _representativeUtc;

        public static void Reset()
        {
            Samples.Clear();
            Order.Clear();
            _representative = default(V2EngineFilterSample);
            _representativeUtc = DateTime.MinValue;
        }

        public static void Record(V2EngineFilterSample sample)
        {
            string key = string.IsNullOrWhiteSpace(sample.Label) ? "engine" : sample.Label;
            if (!Samples.ContainsKey(key))
            {
                Order.Add(key);
                if (Order.Count > MaxSamples)
                {
                    string oldest = Order[0];
                    Order.RemoveAt(0);
                    Samples.Remove(oldest);
                }
            }

            Samples[key] = sample;
            if (_representativeUtc == DateTime.MinValue || sample.Distance < _representative.Distance || DateTime.UtcNow - _representativeUtc > TimeSpan.FromSeconds(1))
            {
                _representative = sample;
                _representativeUtc = DateTime.UtcNow;
            }
        }

        public static bool TryGetRepresentative(out V2EngineFilterSample sample)
        {
            sample = _representative;
            return _representativeUtc != DateTime.MinValue && DateTime.UtcNow - _representativeUtc <= TimeSpan.FromSeconds(3);
        }

        public static float RepresentativeFrequency()
        {
            return TryGetRepresentative(out V2EngineFilterSample sample)
                ? sample.FinalCutoff
                : SettingsManager.Current.Filter1Frequency;
        }

        public static float RepresentativeQ()
        {
            return TryGetRepresentative(out V2EngineFilterSample sample)
                ? sample.FinalQ
                : SettingsManager.Current.Filter1Q;
        }

        public static string RepresentativeType()
        {
            return "LowPass";
        }

        public static string FormatEnvironment()
        {
            if (!TryGetRepresentative(out V2EngineFilterSample sample))
                return "No live engine-filter samples yet. Select enginefilter on an active engine route.";

            return string.Format(
                CultureInfo.InvariantCulture,
                "route={0} inside={1} contact={2} fallback={3} listenerAtm={4:0.00} sourceAtm={5:0.00} pathPressure={6:0.00} path={7} envOcc={9:0.00} override={8}",
                sample.Route ?? "?",
                sample.Inside ? "Y" : "N",
                sample.Contact ? "Y" : "N",
                sample.Fallback ? "Y" : "N",
                sample.ListenerAtmosphere,
                sample.SourceAtmosphere,
                sample.AirPressure,
                sample.DominantPath ?? "?",
                SettingsManager.Current.V2AtmosphereOverrideEnabled
                    ? SettingsManager.Current.V2AtmosphereOverride.ToString("0.00", CultureInfo.InvariantCulture)
                    : "off",
                sample.AirEnvironmentOcclusion);
        }

        public static string FormatSummary()
        {
            if (!TryGetRepresentative(out V2EngineFilterSample sample))
                return "none";

            return sample.FormatShort();
        }

        public static string FormatEmitters(int maxLines)
        {
            if (Order.Count == 0)
                return "dir/layer  dist  pressure  airW hullW tr env airCut hullCut final Q gain airD hullD";

            StringBuilder builder = new StringBuilder();
            builder.Append("dir/layer  dist  pressure  airW hullW tr env airCut hullCut final Q gain airD hullD");
            int count = 0;
            for (int i = Order.Count - 1; i >= 0 && count < maxLines; i--)
            {
                if (!Samples.TryGetValue(Order[i], out V2EngineFilterSample sample))
                    continue;

                builder.AppendLine();
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0,-10} {1,4:0}m {2,5:0.00} {3,4:0.00} {4,4:0.00} {12,4:0.00} {13,3:0.00} {5,5:0} {6,5:0} {7,5:0} {8,4:0.00} {9,4:0.00} {10,4:0.00} {11,4:0.00}",
                    Trim(sample.Label, 10),
                    sample.Distance,
                    sample.AirPressure,
                    sample.AirWeight,
                    sample.HullWeight,
                    sample.AirCutoff,
                    sample.HullCutoff,
                    sample.FinalCutoff,
                    sample.FinalQ,
                    sample.DistanceGain,
                    sample.AirDistanceGain,
                    sample.HullDistanceGain,
                    sample.AirTransmission,
                    sample.AirEnvironmentOcclusionActive ? sample.AirEnvironmentOcclusion : 0f);
                count++;
            }

            return builder.ToString();
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "?";

            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
