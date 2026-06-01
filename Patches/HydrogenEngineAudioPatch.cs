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
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownAmbientEmitters = new HashSet<MyEntity3DSoundEmitter>();

        private static bool _disabled;
        private static int _enginePatchHits;
        private static int _ambientPatchHits;

        private static void Postfix(MyFunctionalBlock __instance)
        {
            if (_disabled)
                return;

            try
            {
                MyEntity3DSoundEmitter emitter = (MyEntity3DSoundEmitter)SoundEmitterField.GetValue(__instance);
                if (emitter == null)
                    return;

                bool hydrogenEngine = __instance is MyHydrogenEngine;
                bool ambient = SettingsManager.Current.AmbientMufflingEnabled && IsAmbientEmitter(emitter);
                if (!hydrogenEngine && !ambient)
                {
                    RestoreEmitter(emitter);
                    return;
                }

                if (hydrogenEngine)
                    KnownHydrogenEngineEmitters.Add(emitter);

                if (ambient)
                    KnownAmbientEmitters.Add(emitter);

                float baseVolume = RestoreEmitter(emitter);

                Vector3D listenerPosition = MyAPIGateway.Session?.Camera?.Position ?? Vector3D.Zero;
                if (listenerPosition == Vector3D.Zero)
                    return;

                float transmission = ExteriorSoundTransmission.Calculate(listenerPosition, emitter.SourcePosition);
                emitter.VolumeMultiplier = baseVolume * transmission;
                LastTransmissionByEmitter[emitter] = transmission;

                if (hydrogenEngine && ++_enginePatchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Hydrogen engine block audio muffling is active.");

                if (ambient && ++_ambientPatchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Ambient block audio muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling functional block audio patch after error: " + ex);
            }
        }

        public static void ResetRuntimeState()
        {
            LastTransmissionByEmitter.Clear();
            KnownHydrogenEngineEmitters.Clear();
            KnownAmbientEmitters.Clear();
            _disabled = false;
            _enginePatchHits = 0;
            _ambientPatchHits = 0;
        }

        public static bool IsKnownHydrogenEngineEmitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && KnownHydrogenEngineEmitters.Contains(emitter);
        }

        public static bool IsKnownAmbientEmitter(MyEntity3DSoundEmitter emitter)
        {
            return emitter != null && KnownAmbientEmitters.Contains(emitter);
        }

        private static bool IsAmbientEmitter(MyEntity3DSoundEmitter emitter)
        {
            return EngineAudioClassifier.IsKnownAmbientCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownAmbientCue(emitter.Sound?.CueEnum)
                || EngineAudioClassifier.IsKnownAmbientCue(emitter.SecondarySound?.CueEnum);
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
    }
}
