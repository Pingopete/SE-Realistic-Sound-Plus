using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateVolumes")]
    internal static class ShipInteriorMufflingPatch
    {
        private static readonly int[] ShipEmitterIndexes =
        {
            2, 3, 4,
            5, 6, 7,
            11,
            14, 15
        };

        private static readonly FieldInfo EmittersField = AccessTools.Field(typeof(MyShipSoundComponent), "m_emitters");
        private static readonly FieldInfo InsideShipField = AccessTools.Field(typeof(MyShipSoundComponent), "m_insideShip");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> LastTransmissionByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();

        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return;

            try
            {
                var emitters = (MyEntity3DSoundEmitter[])EmittersField.GetValue(__instance);
                if (emitters == null)
                    return;

                if (!(bool)InsideShipField.GetValue(__instance))
                {
                    RestoreEmitters(emitters);
                    return;
                }

                Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (listenerPosition == Vector3D.Zero)
                    return;

                foreach (int index in ShipEmitterIndexes)
                {
                    if (index < 0 || index >= emitters.Length)
                        continue;

                    MyEntity3DSoundEmitter emitter = emitters[index];
                    if (emitter == null)
                        continue;

                    float baseVolume = RestoreEmitter(emitter);
                    float transmission = CalculateInteriorTransmission(listenerPosition, emitter.SourcePosition);
                    emitter.VolumeMultiplier = baseVolume * transmission;
                    LastTransmissionByEmitter[emitter] = transmission;
                }

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Interior ship-engine transmission muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling interior muffling patch after error: " + ex);
            }
        }

        private static void RestoreEmitters(MyEntity3DSoundEmitter[] emitters)
        {
            foreach (int index in ShipEmitterIndexes)
            {
                if (index < 0 || index >= emitters.Length)
                    continue;

                MyEntity3DSoundEmitter emitter = emitters[index];
                if (emitter != null)
                    RestoreEmitter(emitter);
            }
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