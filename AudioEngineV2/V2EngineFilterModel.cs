using System;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2EngineFilterModel
    {
        public static bool TryCalculate(MyEntity3DSoundEmitter emitter, RealisticSoundPlusSettings settings, out V2EngineFilterSample sample)
        {
            sample = default(V2EngineFilterSample);
            if (emitter == null || settings == null)
                return false;

            if (!TryResolveListenerAndSource(emitter, out V2AudioListenerState listener, out Vector3D listenerPosition, out Vector3D sourcePosition))
                return false;

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            bool inside = listener.InsideShip;
            bool contact = listener.SeatedInShip || listener.ContactGridEntityId != 0L || inside;
            bool fallback = listener.VanillaFallback;
            float externalListenerAtmosphere = listener.Atmosphere;
            if (externalListenerAtmosphere <= 0f)
                externalListenerAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition);

            float listenerAtmosphere = ResolveListenerAtmosphere(externalListenerAtmosphere, inside || contact);
            float sourceAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition);
            float airPressure = Clamp01(Math.Max(listenerAtmosphere, sourceAtmosphere));

            float airCutoff = DistanceCutoff(
                settings.EngineFilterAirNearFrequency,
                settings.EngineFilterAirFarFrequency,
                settings.EngineFilterAirRange,
                settings.EngineFilterAirDistanceCurve,
                distance);
            float hullCutoff = DistanceCutoff(
                settings.EngineFilterHullNearFrequency,
                settings.EngineFilterHullFarFrequency,
                settings.EngineFilterHullRange,
                settings.EngineFilterHullDistanceCurve,
                distance);

            if (inside)
                airCutoff = Math.Min(airCutoff, settings.EngineFilterInteriorMaxFrequency);

            if (airPressure <= 0.01f)
                airCutoff = settings.EngineFilterVacuumContactFrequency;

            float airWeight = fallback ? 0f : airPressure;
            if (inside)
                airWeight *= settings.EngineFilterInteriorAirWeight;

            float hullWeight = 0f;
            if (!fallback && contact)
            {
                if (inside)
                    hullWeight = 1f;
                else if (airPressure <= 0.05f)
                    hullWeight = 1f;
                else
                    hullWeight = 0.35f;
            }

            if (airWeight <= 0.001f && hullWeight <= 0.001f)
                hullWeight = contact && !fallback ? 1f : 0f;

            float finalCutoff = BlendCutoffs(airCutoff, airWeight, hullCutoff, hullWeight, settings.EngineFilterVacuumContactFrequency);
            float hullShare = (airWeight + hullWeight) <= 0.001f ? 1f : hullWeight / (airWeight + hullWeight);
            float finalQ = Lerp(settings.EngineFilterAirQ, settings.EngineFilterHullQ, Clamp01(hullShare));
            float airDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterAirRange, settings.EngineFilterAirDistanceCurve);
            float hullDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
            float distanceGain = BlendPathDistanceGain(airDistanceGain, airWeight, hullDistanceGain, hullWeight, contact, fallback);

            sample = new V2EngineFilterSample
            {
                Label = AudioEngineV2Runtime.GetEmitterDebugLabel(emitter),
                Route = AudioEngineV2Runtime.GetEmitterFilterRouteName(emitter),
                DominantPath = hullShare >= 0.66f ? "hull" : (hullShare <= 0.33f ? "air" : "mixed"),
                Distance = distance,
                ListenerAtmosphere = listenerAtmosphere,
                SourceAtmosphere = sourceAtmosphere,
                AirPressure = airPressure,
                AirWeight = airWeight,
                HullWeight = hullWeight,
                AirCutoff = airCutoff,
                HullCutoff = hullCutoff,
                FinalCutoff = finalCutoff,
                FinalQ = finalQ,
                AirDistanceGain = airDistanceGain,
                HullDistanceGain = hullDistanceGain,
                DistanceGain = distanceGain,
                Inside = inside,
                Contact = contact,
                Fallback = fallback
            };

            return true;
        }

        public static bool TryCalculateHullOnly(MyEntity3DSoundEmitter emitter, RealisticSoundPlusSettings settings, out V2EngineFilterSample sample)
        {
            sample = default(V2EngineFilterSample);
            if (emitter == null || settings == null)
                return false;

            if (!TryResolveListenerAndSource(emitter, out V2AudioListenerState listener, out Vector3D listenerPosition, out Vector3D sourcePosition))
                return false;

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            bool inside = listener.InsideShip;
            bool contact = listener.SeatedInShip || listener.ContactGridEntityId != 0L || inside;
            bool fallback = listener.VanillaFallback;
            float externalListenerAtmosphere = listener.Atmosphere;
            if (externalListenerAtmosphere <= 0f)
                externalListenerAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition);

            float listenerAtmosphere = ResolveListenerAtmosphere(externalListenerAtmosphere, inside || contact);
            float sourceAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition);
            float hullCutoff = DistanceCutoff(
                settings.EngineFilterHullNearFrequency,
                settings.EngineFilterHullFarFrequency,
                settings.EngineFilterHullRange,
                settings.EngineFilterHullDistanceCurve,
                distance);
            float hullDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
            float hullWeight = !fallback && contact ? 1f : 0f;

            sample = new V2EngineFilterSample
            {
                Label = AudioEngineV2Runtime.GetEmitterDebugLabel(emitter),
                Route = AudioEngineV2Runtime.GetEmitterFilterRouteName(emitter),
                DominantPath = "hull-only",
                Distance = distance,
                ListenerAtmosphere = listenerAtmosphere,
                SourceAtmosphere = sourceAtmosphere,
                AirPressure = Clamp01(Math.Max(listenerAtmosphere, sourceAtmosphere)),
                AirWeight = 0f,
                HullWeight = hullWeight,
                AirCutoff = 0f,
                HullCutoff = hullCutoff,
                FinalCutoff = hullCutoff,
                FinalQ = settings.EngineFilterHullQ,
                AirDistanceGain = 0f,
                HullDistanceGain = hullDistanceGain,
                DistanceGain = hullWeight > 0f ? hullDistanceGain : 0f,
                Inside = inside,
                Contact = contact,
                Fallback = fallback
            };

            return true;
        }

        public static float CalculateDistanceGain(V2AudioListenerState listener, Vector3D sourcePosition, RealisticSoundPlusSettings settings)
        {
            if (settings == null)
                return 1f;

            Vector3D listenerPosition = listener.Position;
            if (listenerPosition == Vector3D.Zero)
                listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (listenerPosition == Vector3D.Zero || sourcePosition == Vector3D.Zero)
                return 1f;

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            bool inside = listener.InsideShip;
            bool contact = listener.SeatedInShip || listener.ContactGridEntityId != 0L || inside;
            bool fallback = listener.VanillaFallback;
            float externalListenerAtmosphere = listener.Atmosphere;
            if (externalListenerAtmosphere <= 0f)
                externalListenerAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition);

            float listenerAtmosphere = ResolveListenerAtmosphere(externalListenerAtmosphere, inside || contact);
            float sourceAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition);
            float airPressure = Clamp01(Math.Max(listenerAtmosphere, sourceAtmosphere));

            CalculatePathWeights(settings, airPressure, inside, contact, fallback, out float airWeight, out float hullWeight);
            float airDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterAirRange, settings.EngineFilterAirDistanceCurve);
            float hullDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
            return BlendPathDistanceGain(airDistanceGain, airWeight, hullDistanceGain, hullWeight, contact, fallback);
        }

        public static float CalculateHullDistanceGain(Vector3D sourcePosition, RealisticSoundPlusSettings settings)
        {
            if (settings == null || sourcePosition == Vector3D.Zero)
                return 0f;

            Vector3D listenerPosition = AudioEngineV2Runtime.Listener.Position;
            if (listenerPosition == Vector3D.Zero)
                listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (listenerPosition == Vector3D.Zero)
                return 0f;

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            return SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
        }

        private static bool TryResolveListenerAndSource(MyEntity3DSoundEmitter emitter, out V2AudioListenerState listener, out Vector3D listenerPosition, out Vector3D sourcePosition)
        {
            listener = V2AudioListenerState.Capture();
            if (listener.Position == Vector3D.Zero)
                listener = AudioEngineV2Runtime.Listener;
            listenerPosition = listener.Position;
            if (listenerPosition == Vector3D.Zero)
                listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            sourcePosition = AudioEngineV2Runtime.TryGetEmitterPosition(emitter, out Vector3D registeredPosition)
                ? registeredPosition
                : emitter.SourcePosition;
            return listenerPosition != Vector3D.Zero && sourcePosition != Vector3D.Zero;
        }

        private static float DistanceCutoff(float nearFrequency, float farFrequency, float range, float curve, float distance)
        {
            float normalized = Clamp01(distance / Math.Max(1f, range));
            float shaped = (float)Math.Pow(normalized, Math.Max(0.1f, curve));
            return Lerp(nearFrequency, farFrequency, shaped);
        }

        private static void CalculatePathWeights(RealisticSoundPlusSettings settings, float airPressure, bool inside, bool contact, bool fallback, out float airWeight, out float hullWeight)
        {
            airWeight = fallback ? 0f : airPressure;
            if (inside)
                airWeight *= settings.EngineFilterInteriorAirWeight;

            hullWeight = 0f;
            if (!fallback && contact)
            {
                if (inside)
                    hullWeight = 1f;
                else if (airPressure <= 0.05f)
                    hullWeight = 1f;
                else
                    hullWeight = 0.35f;
            }

            if (airWeight <= 0.001f && hullWeight <= 0.001f)
                hullWeight = contact && !fallback ? 1f : 0f;
        }

        private static float ResolveListenerAtmosphere(float externalAtmosphere, bool allowLocalRoomPressure)
        {
            float pressure = Clamp01(externalAtmosphere);
            if (allowLocalRoomPressure && V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample sample))
                pressure = Math.Max(pressure, Clamp01(sample.LocalAtmosphere));

            return pressure;
        }

        private static float BlendPathDistanceGain(float airDistanceGain, float airWeight, float hullDistanceGain, float hullWeight, bool contact, bool fallback)
        {
            if (fallback)
                return 0f;

            airWeight = Math.Max(0f, airWeight);
            hullWeight = Math.Max(0f, hullWeight);
            float total = airWeight + hullWeight;
            if (total <= 0.001f && contact)
                return hullDistanceGain;

            if (total <= 0.001f)
                return 0f;

            float gain = (airDistanceGain * airWeight + hullDistanceGain * hullWeight) / total;
            return Clamp01(gain);
        }

        private static float BlendCutoffs(float airCutoff, float airWeight, float hullCutoff, float hullWeight, float fallbackCutoff)
        {
            float total = airWeight + hullWeight;
            if (total <= 0.001f)
                return fallbackCutoff;

            float safeAir = Math.Max(1f, airCutoff);
            float safeHull = Math.Max(1f, hullCutoff);
            double blendedEnergy = (safeAir * safeAir * airWeight + safeHull * safeHull * hullWeight) / total;
            return (float)Math.Sqrt(Math.Max(1.0, blendedEnergy));
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * Clamp01(amount);
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
