using HarmonyLib;
using UnityEngine;

namespace Modzified_Cinematics.Patches;

/// <summary>
/// Dream hold: keep <c>zzzz</c> + black; do not show DreamTexts body while replace Prepare runs.
/// Vanilla gates only on <see cref="CinematicsManager.IsPlaying"/> which is false until URL Play.
/// </summary>
internal static class SleepTextPatches
{
  /// <summary>One <c>type: state, sleep</c> pass per bed sleep (SleepText OnEnable can re-run when pause UI flickers).</summary>
  private static bool _sleepMomentDone;

  [HarmonyPatch(typeof(Player), nameof(Player.SetSleeping))]
  private static class SetSleepingPatch
  {
    [HarmonyPostfix]
    private static void Postfix(bool sleep)
    {
      if (!sleep)
      {
        _sleepMomentDone = false;
      }
    }
  }

  [HarmonyPatch(typeof(SleepText), "HideZZZ")]
  private static class HideZZZPatch
  {
    [HarmonyPrefix]
    private static bool Prefix()
    {
      return !ShouldHoldSleepUi();
    }
  }

  [HarmonyPatch(typeof(SleepText), "ShowDreamText")]
  private static class ShowDreamTextPatch
  {
    [HarmonyPrefix]
    private static bool Prefix(SleepText __instance)
    {
      // Once per sleep session — SleepText OnEnable re-Invokes this after cinematic Unpause.
      if (!_sleepMomentDone)
      {
        _sleepMomentDone = true;
        // SoftRef dream queue wins this sleep; otherwise type: state, sleep play-now.
        if (SoftRefDreamReplacePending())
        {
          CinematicsManager.OnSleep();
        }
        else
        {
          TriggerEngine.OnSleepMoment();
        }
      }

      if (ShouldHoldSleepUi())
      {
        SuppressDreamBody(__instance);
        if (CinematicsManager.IsPlaying())
        {
          HideZzz(__instance);
        }
        else
        {
          KeepZzz(__instance);
        }

        return false;
      }

      return true;
    }
  }

  /// <summary>Called from replace RevealAndPlay once the URL is actually playing.</summary>
  internal static void HideSleepOverlayIfAny()
  {
    SleepText? sleep = Object.FindAnyObjectByType<SleepText>();
    if (sleep == null)
    {
      return;
    }

    SuppressDreamBody(sleep);
    HideZzz(sleep);
  }

  private static bool ShouldHoldSleepUi()
  {
    if (CinematicsManager.IsStartedPlaying() || CinematicsManager.IsPlaying())
    {
      return true;
    }

    return SoftRefDreamReplacePending();
  }

  private static bool SoftRefDreamReplacePending()
  {
    string name = CinematicsManager.m_dreamCinematic;
    if (string.IsNullOrEmpty(name))
    {
      return false;
    }

    if (!CinematicsStore.IsAutoPlayEnabled(name))
    {
      return false;
    }

    return CinematicsStore.TryPickReplaceClip(name, out _, out _);
  }

  private static void SuppressDreamBody(SleepText sleep)
  {
    sleep.m_dreamField.alpha = 0f;
    sleep.m_dreamField.enabled = false;
  }

  private static void HideZzz(SleepText sleep)
  {
    sleep.m_textField.alpha = 0f;
  }

  private static void KeepZzz(SleepText sleep)
  {
    sleep.m_textField.enabled = true;
    sleep.m_textField.alpha = 1f;
  }
}
