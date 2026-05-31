using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class ExteriorWeaponAudioPatch
    {
        private static readonly Dictionary<MyEntity3DSoundEmitter, float> LastTransmissionByEmitter = new Dictionary<MyEntity3DSoundEmitter, float>();
        private static readonly HashSet<MyEntity3DSoundEmitter> KnownExteriorWeaponEmitters = new HashSet<MyEntity3DSoundEmitter>();
        private static bool _disabled;
        private static int _patchHits;

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySound", new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) })]
        [HarmonyPrefix]
        private static void BeforePlaySoundPair(MyEntity3DSoundEmitter __instance, MySoundPair soundId)
        {
            MarkIfExteriorWeaponAudioEmitter(__instance, soundId?.ToString());
        }

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySound", new[] { typeof(MySoundPair), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) })]
        [HarmonyPostfix]
        private static void AfterPlaySoundPair(MyEntity3DSoundEmitter __instance, MySoundPair soundId)
        {
            Apply(__instance, soundId?.ToString());
        }

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySoundWithDistance", new[] { typeof(MyCueId), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) })]
        [HarmonyPrefix]
        private static void BeforePlaySoundWithDistance(MyEntity3DSoundEmitter __instance, MyCueId soundId)
        {
            MarkIfExteriorWeaponAudioEmitter(__instance, soundId.ToString());
        }

        [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "PlaySoundWithDistance", new[] { typeof(MyCueId), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) })]
        [HarmonyPostfix]
        private static void AfterPlaySoundWithDistance(MyEntity3DSoundEmitter __instance, MyCueId soundId)
        {
            Apply(__instance, soundId.ToString());
        }

        public static bool IsExteriorWeaponAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            return KnownExteriorWeaponEmitters.Contains(emitter) || IsExteriorWeaponAudioEmitter(emitter, null);
        }

        private static void Apply(MyEntity3DSoundEmitter emitter, string cueName)
        {
            if (_disabled || emitter == null)
                return;

            try
            {
                float baseVolume = RestoreEmitter(emitter);
                if (!IsExteriorWeaponAudioEmitter(emitter, cueName))
                    return;

                KnownExteriorWeaponEmitters.Add(emitter);
                float transmission = ExteriorSoundTransmission.Calculate(emitter.SourcePosition);
                emitter.VolumeMultiplier = baseVolume * transmission;
                LastTransmissionByEmitter[emitter] = transmission;

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Exterior weapon/explosion audio muffling is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling exterior weapon audio patch after error: " + ex);
            }
        }

        private static void MarkIfExteriorWeaponAudioEmitter(MyEntity3DSoundEmitter emitter, string cueName)
        {
            if (emitter != null && IsExteriorWeaponAudioEmitter(emitter, cueName))
                KnownExteriorWeaponEmitters.Add(emitter);
        }

        private static bool IsExteriorWeaponAudioEmitter(MyEntity3DSoundEmitter emitter, string cueName)
        {
            bool weaponCue = EngineAudioClassifier.IsKnownExteriorWeaponCue(cueName)
                || EngineAudioClassifier.IsKnownExteriorWeaponCue(emitter.SoundId)
                || EngineAudioClassifier.IsKnownExteriorWeaponCue(emitter.Sound?.ToString())
                || EngineAudioClassifier.IsKnownExteriorWeaponCue(emitter.SecondarySound?.ToString());

            if (!weaponCue)
                return false;

            if (IsExplosionCue(cueName)
                || IsExplosionCue(emitter.SoundId.ToString())
                || IsExplosionCue(emitter.Sound?.ToString())
                || IsExplosionCue(emitter.SecondarySound?.ToString()))
                return true;

            return emitter.Entity is MyCubeBlock;
        }

        private static bool IsExplosionCue(string cueName)
        {
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            return cueName.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Expl", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Warhead", StringComparison.OrdinalIgnoreCase) >= 0;
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
