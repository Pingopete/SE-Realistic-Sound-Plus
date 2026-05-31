using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyFunctionalBlock), "UpdateSoundEmitters")]
    internal static class HydrogenEngineAudioPatch
    {
        private static readonly FieldInfo SoundEmitterField = AccessTools.Field(typeof(MyFunctionalBlock), "m_soundEmitter");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> LastTransmissionByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownHydrogenEngineEmitters = new HashSet<MyEntity3DSoundEmitter>();

        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyFunctionalBlock __instance)
        {
            if (_disabled)
                return;

            try
            {
                if (!(__instance is MyHydrogenEngine))
                    return;

                MyEntity3DSoundEmitter emitter = (MyEntity3DSoundEmitter)SoundEmitterField.GetValue(__instance);
                if (emitter == null)
                    return;

                KnownHydrogenEngineEmitters.Add(emitter);
                float baseVolume = RestoreEmitter(emitter);

                Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (listenerPosition == Vector3D.Zero)
                    return;

                float transmission = CalculateInteriorTransmission(listenerPosition, emitter.SourcePosition);
                emitter.VolumeMultiplier = baseVolume * transmission;
                LastTransmissionByEmitter[emitter] = transmission;

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Hydrogen engine block audio muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling hydrogen engine audio patch after error: " + ex);
            }
        }

        public static bool IsKnownHydrogenEngineEmitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && KnownHydrogenEngineEmitters.Contains(emitter);
        }

        private static float RestoreEmitter(MyEntity3DSoundEmitter emitter)
        {
            float volume = emitter.VolumeMultiplier;
            if (!LastTransmissionByEmitter.TryGetValue(emitter, out float previousTransmission))
                return volume;

            LastTransmissionByEmitter.Remove(emitter);
            if (previousTransmission <= 0f)
                return volume;

            float restored = volume / previousTransmission;
            emitter.VolumeMultiplier = restored;
            return restored;
        }

        private static float CalculateInteriorTransmission(Vector3D listenerPosition, Vector3D sourcePosition)
        {
            var settings = SettingsManager.Current;
            double distance = Vector3D.Distance(listenerPosition, sourcePosition);
            float distanceBlend = Clamp01((float)((distance - settings.NearDistance) / (settings.FarDistance - settings.NearDistance)));
            float distanceTransmission = Lerp(1f, settings.FarDistanceTransmission, distanceBlend);
            float fullTransmission = Clamp01(settings.InteriorBaseTransmission * distanceTransmission);
            return Clamp01(1f - (1f - fullTransmission) * settings.MufflingStrength);
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * amount;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}