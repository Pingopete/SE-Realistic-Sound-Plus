using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
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

        // Diagnostics for the transition "bang": confirm whether forcing the ship-sound dimension to 3D signals a
        // 2D->3D change that restarts MyShipSoundComponent's emitters (a hard stop/start = the pop). Throttled.
        private static DateTime _lastDimApplyLogUtc = DateTime.MinValue;
        private static readonly TimeSpan DimApplyLogInterval = TimeSpan.FromMilliseconds(150);

        [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateShouldPlay2D")]
        [HarmonyPrefix]
        private static bool KeepShipSoundsInSharedV2Route(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return true;

            try
            {
                bool was2D = (bool)ShouldPlay2DField.GetValue(__instance);
                ShouldPlay2DField.SetValue(__instance, false);
                ShouldPlay2DChangedField.SetValue(__instance, was2D);

                // was2D == true is the moment RSP signals the dimension changed (2D -> forced 3D), which makes the
                // ship-sound component rebuild/restart its emitters in the new dimension. This is the prime "bang"
                // suspect: log it (rare, so no throttle) so we can line it up against a reported pop.
                if (was2D && SettingsManager.Current.V2DebugLogEnabled)
                    V2DebugLog.WriteEvent("ship-dim", "was2D=Y -> forced 3D (change signaled -> emitters restart) " + DescribeEmitters(__instance));

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Seat/bed/desk vanilla ship-audio dimension override is active.");

                return false;
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling seat audio patch after error: " + ex);
                return true;
            }
        }

        [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateSoundDimension")]
        [HarmonyPostfix]
        private static void ClearVanillaForced2D(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return;

            try
            {
                var emitters = (MyEntity3DSoundEmitter[])EmittersField.GetValue(__instance);
                if (emitters == null)
                    return;

                // UpdateSoundDimension only runs when the dimension actually changed - i.e. exactly when the
                // emitters are being (re)started. Log the emitters' play state here so we can SEE the restart that
                // produces the bang. Throttled because the game may call it in bursts.
                if (SettingsManager.Current.V2DebugLogEnabled)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - _lastDimApplyLogUtc >= DimApplyLogInterval)
                    {
                        _lastDimApplyLogUtc = now;
                        V2DebugLog.WriteEvent("ship-dim-apply", "dimension applied (emitters restarted) " + DescribeEmitters(__instance));
                    }
                }

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

        private static string DescribeEmitters(MyShipSoundComponent component)
        {
            try
            {
                var emitters = (MyEntity3DSoundEmitter[])EmittersField.GetValue(component);
                if (emitters == null)
                    return "emitters=null";

                StringBuilder sb = new StringBuilder();
                sb.Append("emitters=").Append(emitters.Length).Append(" [");
                int shown = 0;
                for (int i = 0; i < emitters.Length && shown < 8; i++)
                {
                    MyEntity3DSoundEmitter e = emitters[i];
                    if (e == null)
                        continue;
                    string cue = "?";
                    try
                    {
                        string c = e.Sound?.CueEnum.ToString();
                        if (!string.IsNullOrWhiteSpace(c) && c != "NullOrEmpty")
                            cue = c;
                    }
                    catch
                    {
                    }
                    if (shown > 0)
                        sb.Append(' ');
                    sb.Append(cue).Append(':').Append(e.IsPlaying ? "P" : "-").Append('/').Append(e.Force2D ? "2D" : "3D");
                    shown++;
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch
            {
                return "emitters=err";
            }
        }

        public static void ResetRuntimeState()
        {
            _disabled = false;
            _patchHits = 0;
            _lastDimApplyLogUtc = DateTime.MinValue;
        }
    }
}
