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
        private static readonly int[] ThrusterEmitterIndexes =
        {
            2, 3, 4,
            5, 6, 7,
            12, 13,
            14, 15, 16
        };

        private static readonly FieldInfo EmittersField = AccessTools.Field(typeof(MyShipSoundComponent), "m_emitters");
        private static readonly FieldInfo InsideShipField = AccessTools.Field(typeof(MyShipSoundComponent), "m_insideShip");
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> LastTransmissionByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownThrusterEmitters = new HashSet<MyEntity3DSoundEmitter>();

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

                bool insideShip = (bool)InsideShipField.GetValue(__instance);
                ExteriorSoundTransmission.ReportListenerInsideShip(insideShip);
                AudioDiagnostics.UpdateGlobal(insideShip);

                if (!insideShip)
                {
                    RestoreEmitters(emitters);
                    return;
                }

                Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (listenerPosition == Vector3D.Zero)
                    return;

                foreach (int index in ThrusterEmitterIndexes)
                {
                    if (index < 0 || index >= emitters.Length)
                        continue;

                    MyEntity3DSoundEmitter emitter = emitters[index];
                    if (emitter == null)
                        continue;

                    KnownThrusterEmitters.Add(emitter);
                    float baseVolume = RestoreEmitter(emitter);
                    float transmission = SettingsManager.Current.SpatialAudioEnabled
                        ? 0f
                        : ExteriorSoundTransmission.Calculate(listenerPosition, emitter.SourcePosition);
                    float finalMultiplier = baseVolume * transmission;
                    emitter.VolumeMultiplier = finalMultiplier;
                    AudioDiagnostics.RecordEmitter(emitter, "interior", baseVolume, transmission, transmission, finalMultiplier, emitter.SourcePosition);
                    LastTransmissionByEmitter[emitter] = transmission;
                }

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Interior thruster transmission muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling interior muffling patch after error: " + ex);
            }
        }

        private static void RestoreEmitters(MyEntity3DSoundEmitter[] emitters)
        {
            foreach (int index in ThrusterEmitterIndexes)
            {
                if (index < 0 || index >= emitters.Length)
                    continue;

                MyEntity3DSoundEmitter emitter = emitters[index];
                if (emitter != null)
                    RestoreEmitter(emitter);
            }
        }

        public static bool IsKnownThrusterEmitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && KnownThrusterEmitters.Contains(emitter);
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


        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
