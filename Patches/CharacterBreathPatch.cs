using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using VRage.Audio;
using VRage.Game.ModAPI.Interfaces;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyCharacterBreath), "Update")]
    internal static class CharacterBreathPatch
    {
        private static readonly FieldInfo CharacterField = AccessTools.Field(typeof(MyCharacterBreath), "m_character");
        private static readonly FieldInfo SoundField = AccessTools.Field(typeof(MyCharacterBreath), "m_sound");

        private static bool _disabled;
        private static int _patchHits;

        private static bool Prefix(MyCharacterBreath __instance)
        {
            if (_disabled)
                return true;

            try
            {
                if (!ShouldSuppressBreath(__instance))
                    return true;

                StopBreath(__instance);
                return false;
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling character breath prefix after error: " + ex);
                return true;
            }
        }

        private static void Postfix(MyCharacterBreath __instance)
        {
            if (_disabled)
                return;

            try
            {
                if (!ShouldSuppressBreath(__instance))
                    return;

                StopBreath(__instance);
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling character breath patch after error: " + ex);
            }
        }

        private static bool ShouldSuppressBreath(MyCharacterBreath breath)
        {
            MyCharacter character = (MyCharacter)CharacterField.GetValue(breath);
            if (character == null)
                return false;

            bool helmetOpen = character.OxygenComponent != null && !character.OxygenComponent.HelmetEnabled;

            IMyControllableEntity controllable = character as IMyControllableEntity;
            if (controllable != null && !controllable.EnabledHelmet)
                helmetOpen = true;

            return helmetOpen;
        }

        private static void StopBreath(MyCharacterBreath breath)
        {
            if (breath.CurrentState != MyCharacterBreath.State.NoBreath)
                breath.CurrentState = MyCharacterBreath.State.NoBreath;

            IMySourceVoice sound = (IMySourceVoice)SoundField.GetValue(breath);
            if (sound != null && sound.IsValid)
                sound.Stop(true);

            SoundField.SetValue(breath, null);

            if (++_patchHits == 1)
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Open-helmet breath loop suppression is active.");
        }
    }
}