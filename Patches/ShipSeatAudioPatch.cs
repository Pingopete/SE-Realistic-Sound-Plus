using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch]
    internal static class ShipSeatAudioPatch
    {
        private static readonly FieldInfo ShouldPlay2DField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shouldPlay2D");
        private static readonly FieldInfo ShouldPlay2DChangedField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shouldPlay2DChanged");
        private static readonly FieldInfo EmittersField = AccessTools.Field(typeof(MyShipSoundComponent), "m_emitters");

        private static bool _disabled;
        private static int _patchHits;

        [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateShouldPlay2D")]
        [HarmonyPrefix]
        private static bool KeepShipSoundsSpatial(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return true;

            try
            {
                bool was2D = (bool)ShouldPlay2DField.GetValue(__instance);
                ShouldPlay2DField.SetValue(__instance, false);
                ShouldPlay2DChangedField.SetValue(__instance, was2D);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Seat/cockpit 2D ship-engine audio override is active.");

                return false;
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling seat audio patch after error: " + ex);
                return true;
            }
        }

        public static void ResetRuntimeState()
        {
            _disabled = false;
            _patchHits = 0;
        }

        [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateSoundDimension")]
        [HarmonyPostfix]
        private static void ClearForced2D(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return;

            try
            {
                var emitters = (MyEntity3DSoundEmitter[])EmittersField.GetValue(__instance);
                if (emitters == null)
                    return;

                foreach (MyEntity3DSoundEmitter emitter in emitters)
                {
                    if (emitter == null)
                        continue;

                    emitter.Force2D = false;
                    emitter.Force3D = true;
                }
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling seat audio dimension patch after error: " + ex);
            }
        }
    }
}
