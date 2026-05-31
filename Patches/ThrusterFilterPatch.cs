using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "SelectEffect")]
    internal static class ThrusterFilterPatch
    {
        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyEntity3DSoundEmitter __instance, ref MyStringHash __result)
        {
            if (_disabled)
                return;

            try
            {
                if (!IsThrusterAudioEmitter(__instance))
                    return;

                string effectSubtype = SettingsManager.GetEngineFilterEffectSubtype();
                if (string.IsNullOrEmpty(effectSubtype))
                    return;

                __result = MyStringHash.GetOrCompute(effectSubtype);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Thruster low-pass filter override is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling thruster filter patch after error: " + ex);
            }
        }

        public static bool IsThrusterAudioEmitter(MyEntity3DSoundEmitter emitter)
        {
            if (emitter == null)
                return false;

            if (ShipInteriorMufflingPatch.IsKnownThrusterEmitter(emitter))
                return true;

            return emitter.Entity is MyThrust;
        }
    }

    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "SetSound")]
    internal static class ThrusterSourceVoiceFilterPatch
    {
        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyEntity3DSoundEmitter __instance, IMySourceVoice value)
        {
            if (_disabled)
                return;

            try
            {
                if (value == null || !value.IsValid || !ThrusterFilterPatch.IsThrusterAudioEmitter(__instance))
                    return;

                string effectSubtype = SettingsManager.GetEngineFilterEffectSubtype();
                if (string.IsNullOrEmpty(effectSubtype))
                    return;

                MyAudio.Static.ApplyEffect(value, MyStringHash.GetOrCompute(effectSubtype), new[] { __instance.SoundId }, null, true);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Thruster source-voice filter is active: " + effectSubtype);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling thruster source-voice filter patch after error: " + ex);
            }
        }
    }
}
