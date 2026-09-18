using HarmonyLib;
using UnityEngine;

namespace Modzified_Cinematics.Patches;

/// <summary>
/// Dream hold: keep <c>zzzz</c> + black; do not show DreamTexts body while replace Prepare runs.
/// Vanilla gates only on <see cref="CinematicsManager.IsPlaying"/> which is false until URL Play.
/// </summary>
internal static class SleepTextPatches
{
  [HarmonyPatch(typeof(SleepText), "HideZZZ")]
  private static class HideZZZPatch
  {
    [HarmonyPrefix]
    private static bool Prefix()
    {
      return !ReplaceDreamQueued();
    }
  }

  [HarmonyPatch(typeof(SleepText), "ShowDreamText")]
  private static class ShowDreamTextPatch
  {
    [HarmonyPrefix]
    private static bool Prefix(SleepText __instance)
    {
      if (!ReplaceDreamQueued())
      {
        return true;
      }

      CinematicsManager.OnSleep();

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

  private static bool ReplaceDreamQueued()
  {
    if (CinematicsManager.IsStartedPlaying() || CinematicsManager.IsPlaying())
    {
      return true;
    }

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
