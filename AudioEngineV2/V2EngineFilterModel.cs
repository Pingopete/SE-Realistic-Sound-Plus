using System;
using System.Reflection;
using RealisticSoundPlus.Patches;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2EngineFilterModel
    {
        private static readonly BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static DateTime _lastFallbackAirRecoveryLogUtc = DateTime.MinValue;

        public static bool TryCalculate(MyEntity3DSoundEmitter emitter, RealisticSoundPlusSettings settings, out V2EngineFilterSample sample)
        {
            sample = default(V2EngineFilterSample);
            if (emitter == null || settings == null)
                return false;

            if (!TryResolveListenerAndSource(emitter, out V2AudioListenerState listener, out Vector3D listenerPosition, out Vector3D sourcePosition))
                return false;

            ResolveEmitterSourceIds(emitter, out long sourceGridId, out _);

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            bool inside = listener.InsideShip;
            bool contact = IsHullPathViable(listener, sourceGridId);
            bool fallback = listener.VanillaFallback;
            float listenerAtmosphere = ResolveCameraAtmosphere(listenerPosition);
            float sourceAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition);
            // Both reads come from GetAtmosphericPressure, which already blends physical density with the synthetic
            // altitude ramp, so the gradual entry/exit easing is baked in here - no separate shaping needed.
            float airPressure = Clamp01(Math.Min(listenerAtmosphere, sourceAtmosphere));
            float airTransmission = ResolveEnvironmentAirTransmission(settings, sourceAtmosphere, airPressure, out float airEnvironmentOcclusion, out bool airEnvironmentOcclusionActive);
            // Additional engine-air muffle driven directly by the player-position env probe (FinalMuffling) x menu scalar.
            float engineEnvMuffle = ResolveEngineEnvMuffle(settings, airPressure, out bool engineEnvMuffleActive);
            airEnvironmentOcclusion = engineEnvMuffle;
            airEnvironmentOcclusionActive = engineEnvMuffleActive;

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

            // Darken the air cutoff CONTINUOUSLY toward the vacuum frequency as pressure falls (was a binary <=0.01
            // step). Outside the ship the cutoff blend reduces to airCutoff, so this makes the external / 3rd-person
            // engine tone lose its highs gradually with pressure - not just fade in volume. Log-space so the sweep is
            // perceptually even; at full pressure airCutoff is unchanged, at vacuum it lands on the vacuum frequency.
            airCutoff = LogLerpFrequency(settings.EngineFilterVacuumContactFrequency, airCutoff, airPressure);

            CalculatePathWeights(airPressure, airTransmission, contact, out float airWeight, out float hullWeight);
            bool fallbackAirRecovered = fallback && airWeight > 0.001f;

            float finalCutoff = BlendCutoffs(airCutoff, airWeight, hullCutoff, hullWeight, settings.EngineFilterVacuumContactFrequency);
            finalCutoff = ApplyEngineEnvMuffle(finalCutoff, settings, engineEnvMuffle);
            float hullShare = (airWeight + hullWeight) <= 0.001f ? 1f : hullWeight / (airWeight + hullWeight);
            float finalQ = Lerp(settings.EngineFilterAirQ, settings.EngineFilterHullQ, Clamp01(hullShare));
            float airDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterAirRange, settings.EngineFilterAirDistanceCurve);
            float hullDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
            float distanceGain = BlendPathDistanceGain(airDistanceGain, airWeight, hullDistanceGain, hullWeight, contact, fallback && !fallbackAirRecovered);

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
                AirTransmission = airTransmission,
                AirEnvironmentOcclusion = airEnvironmentOcclusion,
                AirEnvironmentOcclusionActive = airEnvironmentOcclusionActive,
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

            if (fallbackAirRecovered)
                LogFallbackAirRecovery(sample);

            return true;
        }

        public static bool TryCalculateHullOnly(MyEntity3DSoundEmitter emitter, RealisticSoundPlusSettings settings, out V2EngineFilterSample sample)
        {
            sample = default(V2EngineFilterSample);
            if (emitter == null || settings == null)
                return false;

            if (!TryResolveListenerAndSource(emitter, out V2AudioListenerState listener, out Vector3D listenerPosition, out Vector3D sourcePosition))
                return false;

            ResolveEmitterSourceIds(emitter, out long sourceGridId, out _);

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            bool inside = listener.InsideShip;
            bool contact = IsHullPathViable(listener, sourceGridId);
            bool fallback = listener.VanillaFallback;
            float listenerAtmosphere = ResolveCameraAtmosphere(listenerPosition);
            float sourceAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition);
            float hullCutoff = DistanceCutoff(
                settings.EngineFilterHullNearFrequency,
                settings.EngineFilterHullFarFrequency,
                settings.EngineFilterHullRange,
                settings.EngineFilterHullDistanceCurve,
                distance);
            float hullDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
            float hullWeight = contact ? 1f : 0f;

            sample = new V2EngineFilterSample
            {
                Label = AudioEngineV2Runtime.GetEmitterDebugLabel(emitter),
                Route = AudioEngineV2Runtime.GetEmitterFilterRouteName(emitter),
                DominantPath = "hull-only",
                Distance = distance,
                ListenerAtmosphere = listenerAtmosphere,
                SourceAtmosphere = sourceAtmosphere,
                AirPressure = Clamp01(listenerAtmosphere),
                AirWeight = 0f,
                HullWeight = hullWeight,
                AirTransmission = 0f,
                AirEnvironmentOcclusion = 0f,
                AirEnvironmentOcclusionActive = false,
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

        public static float CalculateDistanceGain(V2AudioListenerState listener, Vector3D sourcePosition, RealisticSoundPlusSettings settings, long sourceGridId = 0L, long sourceEntityId = 0L)
        {
            if (settings == null)
                return 1f;

            Vector3D listenerPosition = listener.Position;
            if (listenerPosition == Vector3D.Zero)
                listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;

            if (listenerPosition == Vector3D.Zero || sourcePosition == Vector3D.Zero)
                return 1f;

            float distance = (float)Vector3D.Distance(listenerPosition, sourcePosition);
            bool contact = IsHullPathViable(listener, sourceGridId);
            bool fallback = listener.VanillaFallback;
            float listenerAtmosphere = ResolveCameraAtmosphere(listenerPosition);
            float sourceAtmosphere = ExteriorSoundTransmission.GetAtmosphericPressure(sourcePosition);
            // Ease raw density through the shared perceptual curve so the vacuum->air cutoff blend (and the <=0.01
            // vacuum gate) opens gradually across a descent instead of snapping bright in the lowest atmosphere band.
            float airPressure = Clamp01(Math.Min(listenerAtmosphere, sourceAtmosphere));
            float airTransmission = ResolveEnvironmentAirTransmission(settings, sourceAtmosphere, airPressure, out _, out _);

            CalculatePathWeights(airPressure, airTransmission, contact, out float airWeight, out float hullWeight);
            float airDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterAirRange, settings.EngineFilterAirDistanceCurve);
            float hullDistanceGain = SixDirectionSourceModel.EvaluateDistanceGain(distance, settings.EngineFilterHullRange, settings.EngineFilterHullDistanceCurve);
            bool fallbackAirRecovered = fallback && airWeight > 0.001f;
            return BlendPathDistanceGain(airDistanceGain, airWeight, hullDistanceGain, hullWeight, contact, fallback && !fallbackAirRecovered);
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

        private static void ResolveEmitterSourceIds(MyEntity3DSoundEmitter emitter, out long sourceGridId, out long sourceEntityId)
        {
            sourceGridId = 0L;
            sourceEntityId = 0L;
            if (emitter == null)
                return;

            if (AudioEngineV2Runtime.TryGetEmitterSourceIds(emitter, out sourceGridId, out sourceEntityId) && (sourceGridId != 0L || sourceEntityId != 0L))
                return;

            object entity = TryReadMember(emitter, "Entity")
                ?? TryReadMember(emitter, "m_entity")
                ?? TryReadMember(emitter, "m_sourceEntity")
                ?? TryReadMember(emitter, "SourceEntity");
            TryResolveEntityIds(entity, 0, ref sourceGridId, ref sourceEntityId);
        }

        private static bool TryResolveEntityIds(object candidate, int depth, ref long sourceGridId, ref long sourceEntityId)
        {
            if (candidate == null || depth > 4)
                return false;

            if (candidate is MyCubeGrid grid)
            {
                sourceGridId = grid.EntityId;
                if (sourceEntityId == 0L)
                    sourceEntityId = grid.EntityId;
                return sourceGridId != 0L;
            }

            if (candidate is MyCubeBlock block)
            {
                sourceEntityId = block.EntityId;
                sourceGridId = block.CubeGrid != null ? block.CubeGrid.EntityId : 0L;
                return sourceEntityId != 0L || sourceGridId != 0L;
            }

            if (candidate is MyEntity entity)
            {
                sourceEntityId = entity.EntityId;
                object cubeGrid = TryReadMember(entity, "CubeGrid");
                if (TryResolveEntityIds(cubeGrid, depth + 1, ref sourceGridId, ref sourceEntityId))
                    return true;
            }

            object cubeGridMember = TryReadMember(candidate, "CubeGrid");
            if (TryResolveEntityIds(cubeGridMember, depth + 1, ref sourceGridId, ref sourceEntityId))
                return true;

            object nestedEntity = TryReadMember(candidate, "Entity");
            if (nestedEntity != null && !ReferenceEquals(nestedEntity, candidate) && TryResolveEntityIds(nestedEntity, depth + 1, ref sourceGridId, ref sourceEntityId))
                return true;

            object parent = TryReadMember(candidate, "Parent");
            return parent != null && !ReferenceEquals(parent, candidate) && TryResolveEntityIds(parent, depth + 1, ref sourceGridId, ref sourceEntityId);
        }

        private static bool IsHullPathViable(V2AudioListenerState listener, long sourceGridId)
        {
            if (sourceGridId == 0L)
                return false;

            if (IsOutsideSeatCamera(listener))
                return false;

            if (listener.ContactGridEntityId != 0L && AreGridsAudioCoupled(sourceGridId, listener.ContactGridEntityId))
                return true;

            return listener.SeatedInShip
                && listener.InsideShip
                && listener.GridEntityId != 0L
                && AreGridsAudioCoupled(sourceGridId, listener.GridEntityId);
        }

        private static bool IsOutsideSeatCamera(V2AudioListenerState listener)
        {
            return listener.SeatedInShip
                && !listener.InsideShip
                && (listener.ModeName ?? string.Empty).IndexOf("outside-seat-camera", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool AreGridsAudioCoupled(long sourceGridId, long listenerGridId)
        {
            if (sourceGridId == 0L || listenerGridId == 0L)
                return false;

            if (sourceGridId == listenerGridId)
                return true;

            if (!AudioEngineV2Runtime.TryGetGridById(sourceGridId, out MyCubeGrid sourceGrid)
                || !AudioEngineV2Runtime.TryGetGridById(listenerGridId, out MyCubeGrid listenerGrid))
                return false;

            return TryInvokeGridCoupling(sourceGrid, listenerGrid)
                || TryInvokeGridCoupling(listenerGrid, sourceGrid);
        }

        private static bool TryInvokeGridCoupling(MyCubeGrid left, MyCubeGrid right)
        {
            if (left == null || right == null)
                return false;

            string[] names = { "IsSameConstructAs", "IsInSameLogicalGroupAs", "IsInSamePhysicalGroupAs" };
            Type type = left.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    MethodInfo method = FindCompatibleGridMethod(type, names[i], right);
                    if (method == null)
                        continue;

                    object result = method.Invoke(left, new object[] { right });
                    if (result is bool coupled && coupled)
                        return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static MethodInfo FindCompatibleGridMethod(Type type, string name, MyCubeGrid argument)
        {
            if (type == null || argument == null)
                return null;

            MethodInfo[] methods = type.GetMethods(InstanceMembers);
            Type argumentType = argument.GetType();
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                if (parameters[0].ParameterType.IsAssignableFrom(argumentType))
                    return method;
            }

            return null;
        }

        private static object TryReadMember(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return null;

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(name, InstanceMembers);
                if (property != null)
                    return property.GetValue(instance, null);

                FieldInfo field = instance.GetType().GetField(name, InstanceMembers);
                return field?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static float DistanceCutoff(float nearFrequency, float farFrequency, float range, float curve, float distance)
        {
            float normalized = Clamp01(distance / Math.Max(1f, range));
            float shaped = (float)Math.Pow(normalized, Math.Max(0.1f, curve));
            return Lerp(nearFrequency, farFrequency, shaped);
        }

        private static void CalculatePathWeights(float airPressure, float airTransmission, bool hullContact, out float airWeight, out float hullWeight)
        {
            airWeight = Clamp01(airPressure) * Clamp01(airTransmission);
            hullWeight = hullContact ? 1f : 0f;
        }

        // The env muffle no longer rides the air/hull WEIGHT blend - that was a no-op without hull contact and only
        // ever shifted tone. It is now a DIRECT cutoff darkening (ResolveEngineEnvMuffle / ApplyEngineEnvMuffle). This
        // stub returns full transmission so the existing path-weight math is unchanged.
        private static float ResolveEnvironmentAirTransmission(RealisticSoundPlusSettings settings, float sourceAtmosphere, float airPressure, out float airEnvironmentOcclusion, out bool active)
        {
            airEnvironmentOcclusion = 0f;
            active = false;
            return 1f;
        }

        // NEW engine env muffle: additional muffling of the engine AIR sound, driven DIRECTLY by the environment
        // probe's FinalMuffling at the player's CURRENT position, scaled by the menu slider. 0 = no added muffle;
        // higher MULTIPLIES the env muffle into the engine cutoff. Atmosphere-gated (the air leg needs air to carry).
        private static float ResolveEngineEnvMuffle(RealisticSoundPlusSettings settings, float airPressure, out bool active)
        {
            active = false;
            float scalar = settings?.EngineFilterAirEnvironmentOcclusionContribution ?? 0f;
            if (scalar <= 0f || airPressure <= 0.01f)
                return 0f;

            if (!V2PlayerEnvironmentTelemetry.TryGetLatest(out V2PlayerEnvironmentSample env))
                return 0f;

            float muffle = Clamp01(env.FinalMuffling * scalar);
            active = muffle > 0.001f;
            return muffle;
        }

        // Darken the engine cutoff toward its most-muffled air tone (the air-far cutoff) by the env-muffle amount, in
        // log space so the sweep sounds even. amount 1 collapses the engine to the air-far cutoff.
        private static float ApplyEngineEnvMuffle(float cutoff, RealisticSoundPlusSettings settings, float engineEnvMuffle)
        {
            if (engineEnvMuffle <= 0.001f)
                return cutoff;

            float floor = Math.Max(RspDynamicAudioFilters.MinFilterFrequency, settings.EngineFilterAirFarFrequency);
            double logCutoff = Math.Log(Math.Max(1f, cutoff));
            double logFloor = Math.Log(Math.Max(1f, floor));
            return (float)Math.Exp(logCutoff + (logFloor - logCutoff) * Clamp01(engineEnvMuffle));
        }

        private static void LogFallbackAirRecovery(V2EngineFilterSample sample)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastFallbackAirRecoveryLogUtc < TimeSpan.FromSeconds(1))
                return;

            _lastFallbackAirRecoveryLogUtc = now;
            V2DebugLog.WriteEvent("engine-filter-fallback-air", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "route={0} label={1} d={2:0}m pressure={3:0.00} airW={4:0.00} hullW={5:0.00} final={6:0}Hz gain={7:0.00}",
                sample.Route ?? "?",
                sample.Label ?? "?",
                sample.Distance,
                sample.AirPressure,
                sample.AirWeight,
                sample.HullWeight,
                sample.FinalCutoff,
                sample.DistanceGain));
        }

        private static float ResolveCameraAtmosphere(Vector3D listenerPosition)
        {
            RealisticSoundPlusSettings settings = SettingsManager.Current;
            if (settings != null && settings.V2AtmosphereOverrideEnabled)
                return Clamp01(settings.V2AtmosphereOverride);

            return Clamp01(ExteriorSoundTransmission.GetAtmosphericPressure(listenerPosition));
        }

        private static float BlendPathDistanceGain(float airDistanceGain, float airWeight, float hullDistanceGain, float hullWeight, bool contact, bool fallback)
        {
            if (fallback)
                return 0f;

            airWeight = Math.Max(0f, airWeight);
            hullWeight = Math.Max(0f, hullWeight);
            float airContribution = Clamp01(airDistanceGain * airWeight);
            float hullContribution = Clamp01(hullDistanceGain * hullWeight);
            if (airContribution <= 0.001f && hullContribution <= 0.001f && contact)
                return hullDistanceGain;

            return Clamp01(1f - (1f - airContribution) * (1f - hullContribution));
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

        // Interpolate a cutoff frequency in LOG space by t (0 -> darkFreq, 1 -> brightFreq). Used to slide the air
        // cutoff from the vacuum frequency up to its bright distance value as pressure rises, so the sweep is even.
        private static float LogLerpFrequency(float darkFreq, float brightFreq, float t)
        {
            if (t <= 0f)
                return darkFreq;
            if (t >= 1f)
                return brightFreq;

            float fromLog = (float)Math.Log(Math.Max(1f, darkFreq));
            float toLog = (float)Math.Log(Math.Max(1f, brightFreq));
            return (float)Math.Exp(fromLog + (toLog - fromLog) * t);
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
