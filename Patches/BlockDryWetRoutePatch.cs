using System;
using System.Reflection;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    // Keeps a block voice pinned to RSP's dry/wet "split" submix. SE re-asserts a 3D voice's output to the game
    // submix every frame (its Apply3D path drives MySourceVoice.SetOutputVoices), so rerouting from outside that
    // flow only toggles game<->split and glitches. By re-pointing our tracked voices in a POSTFIX on the engine's
    // own SetOutputVoices call, RSP wins in the same call stack BEFORE the buffer renders — the same trick that
    // makes the live filter postfix stick. No-op (one dictionary/null check) unless the split feature is active.
    [HarmonyPatch]
    internal static class BlockDryWetRoutePatch
    {
        private static bool _disabled;
        private static bool _loggedTarget;

        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("VRage.Audio.MySourceVoice");
            MethodInfo method = null;
            if (type != null)
            {
                foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!string.Equals(candidate.Name, "SetOutputVoices", StringComparison.Ordinal))
                        continue;

                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsArray)
                    {
                        method = candidate;
                        break;
                    }
                }
            }

            if (!_loggedTarget)
            {
                _loggedTarget = true;
                V2DebugLog.WriteEvent("block-split-patch", method != null ? "target=MySourceVoice.SetOutputVoices" : "target-missing");
            }

            return method;
        }

        private static void Postfix(object __instance)
        {
            if (_disabled)
                return;

            try
            {
                V2GlobalReverbRuntime.OnEngineSetOutputVoices(__instance);
            }
            catch (Exception ex)
            {
                _disabled = true;
                V2DebugLog.WriteEvent("block-split-patch", "postfix-disabled " + ex.Message);
                MyLog.Default.WriteLine("[RealisticSoundPlus] Block dry/wet route postfix disabled: " + ex);
            }
        }
    }
}
