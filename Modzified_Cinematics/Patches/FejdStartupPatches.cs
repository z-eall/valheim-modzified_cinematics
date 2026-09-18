using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Modzified_Cinematics.Patches;

/// <summary>
/// Fejd gallery replay + startup intro cover.
/// Vanilla: hide menu then Play() with an in-memory VideoClip (no Prepare gap).
/// URL replace must Prepare — cover firepit under logo UI, then cut when ready.
/// </summary>
[HarmonyPatch(typeof(FejdStartup))]
internal static class FejdStartupPatches
{
  [HarmonyPatch(nameof(FejdStartup.OnCinematicsPlay))]
  [HarmonyPrefix]
  private static void OnCinematicsPlayPrefix()
  {
    CinematicsStore.AllowReplayOnce = true;
  }

  /// <summary>
  /// Flat prefix on the iterator method (class-level HarmonyPatch so Plugin CreateClassProcessor finds us).
  /// </summary>
  [HarmonyPatch("TryPlayIntroCinematic")]
  [HarmonyPrefix]
  private static bool TryPlayIntroCinematicPrefix(FejdStartup __instance, ref IEnumerator __result)
  {
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, "Cinematics intro: Fejd TryPlayIntroCinematic prefix.");

    if (!CinematicsStore.IsAutoPlayEnabled(VanillaCatalog.IntroName))
    {
      __result = SkippedStartupIntro(__instance);
      return false;
    }

    if (CinematicsStore.TryPickReplaceClip(VanillaCatalog.IntroName, out _, out _))
    {
      __result = PlayIntroWithBlackUnderLogo(__instance);
      return false;
    }

    return true;
  }

  /// <summary>
  /// Black cinematic cam under logo UI (main/firepit cam off). Logo can finish on top of black;
  /// then Play when warm. Vanilla has no equivalent — bundled clips need no Prepare.
  /// </summary>
  private static IEnumerator PlayIntroWithBlackUnderLogo(FejdStartup fejd)
  {
    if (PlatformPrefs.GetBool("SkipIntroCinematic"))
    {
      fejd.m_menuAnimator.SetTrigger("FadeIn");
      yield break;
    }

    if (fejd.m_queuedJoinServer != ServerJoinData.None || MatchmakingManager.HasPendingInvite())
    {
      yield break;
    }

    if (!Game.m_hasStartedOnce
        && CinematicsManager.s_instance != null
        && CinematicsManager.s_instance.m_introOnStartup)
    {
      if (!CinematicsStore.TryPickReplaceClip(VanillaCatalog.IntroName, out string abs, out _))
      {
        fejd.m_mainMenu.SetActive(true);
        fejd.m_menuAnimator.SetTrigger("FadeIn");
        yield break;
      }

      CinematicsManagerPatches.SetLogoHoldIntroAbs(abs);
      // Keep m_mainMenu active so logo UI stays; kill 3D firepit under it.
      CinematicsManagerPatches.ApplyBlackUnderLogoCover(CinematicsManager.s_instance);
      CinematicsManagerPatches.WarmPrepareExact(abs);

      float waited = 0f;
      const float maxWait = 90f;
      while (waited < maxWait && !CinematicsManagerPatches.IsWarmReady(abs))
      {
        waited += Time.unscaledDeltaTime;
        yield return null;
      }

      bool ready = CinematicsManagerPatches.IsWarmReady(abs);
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Info,
        ready
          ? $"Cinematics intro: black-under-logo warm ready after {waited:0.0}s."
          : $"Cinematics intro: warm not ready after {waited:0.0}s — Prepare under black cover.");

      if (CinematicsManager.Play(CinematicsManager.Settings.Intro))
      {
        MusicMan.instance.Reset();
      }
      else
      {
        CinematicsManagerPatches.ClearBlackUnderLogoCover(CinematicsManager.s_instance);
        fejd.m_mainMenu.SetActive(true);
        CinematicsManagerPatches.SetLogoHoldIntroAbs(null);
      }
    }
    else
    {
      fejd.m_mainMenu.SetActive(true);
    }

    while (CinematicsManager.IsStartedPlaying())
    {
      yield return null;
    }

    fejd.m_menuAnimator.SetTrigger("FadeIn");
  }

  private static IEnumerator SkippedStartupIntro(FejdStartup fejd)
  {
    if (PlatformPrefs.GetBool("SkipIntroCinematic"))
    {
      fejd.m_menuAnimator.SetTrigger("FadeIn");
      yield break;
    }

    fejd.m_mainMenu.SetActive(false);
    if (fejd.m_queuedJoinServer != ServerJoinData.None || MatchmakingManager.HasPendingInvite())
    {
      yield break;
    }

    fejd.m_mainMenu.SetActive(true);
    while (CinematicsManager.IsStartedPlaying())
    {
      yield return null;
    }

    fejd.m_menuAnimator.SetTrigger("FadeIn");
  }
}
