using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
  /// <summary>
  /// Called manually from Plugin.Awake (not attribute-discovered — the target is a compiler-
  /// generated nested type Harmony's normal attribute scan can't name). Removes vanilla's own
  /// ZLog.LogError("Failed to play intro cinematic") call from inside the coroutine's real body
  /// (its MoveNext), while leaving every other line in that branch — including re-enabling
  /// m_mainMenu — untouched. Patching the outer TryPlayIntroCinematic method (see
  /// TryPlayIntroCinematicPrefix below) never actually intercepted this call at runtime, for
  /// reasons still unexplained (ticket 28); patching CinematicsManager.Play to lie about success
  /// instead (tried 2026-09-22, 0.3.11) stopped the log but also stopped the menu from ever coming
  /// back — vanilla ties "reactivate the menu" and "log the error" to the same false-return branch,
  /// so a true/false return value alone can't get one without the other. Editing the coroutine's
  /// real body directly is the only way to separate them.
  /// </summary>
  internal static void PatchIntroFailureLog(Harmony harmony)
  {
    try
    {
      MethodInfo? original = AccessTools.Method(typeof(FejdStartup), "TryPlayIntroCinematic");
      MethodInfo? moveNext = original == null ? null : AccessTools.EnumeratorMoveNext(original);
      if (moveNext == null)
      {
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Warning,
          "Cinematics intro: could not find TryPlayIntroCinematic's MoveNext — vanilla's own error log stays as-is.");
        return;
      }

      harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(FejdStartupPatches), nameof(SuppressFailedIntroLogTranspiler)));
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Info,
        "Cinematics intro: patched TryPlayIntroCinematic's MoveNext to remove its own 'Failed to play intro cinematic' log line.");
    }
    catch (System.Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Cinematics intro: MoveNext transpile failed, vanilla's own error log stays as-is: {ex.Message}");
    }
  }

  /// <summary>Deletes the two IL instructions (ldstr + call) that make up ZLog.LogError("Failed to play intro cinematic").</summary>
  private static IEnumerable<CodeInstruction> SuppressFailedIntroLogTranspiler(IEnumerable<CodeInstruction> instructions)
  {
    List<CodeInstruction> codes = new(instructions);
    for (int i = 0; i < codes.Count - 1; i++)
    {
      if (codes[i].opcode != OpCodes.Ldstr
          || codes[i].operand is not string text
          || text != "Failed to play intro cinematic"
          || codes[i + 1].opcode != OpCodes.Call)
      {
        continue;
      }

      // Preserve any branch target pointing at the ldstr we're about to delete by moving it
      // onto whatever instruction follows the removed pair.
      if (i + 2 < codes.Count)
      {
        codes[i + 2].labels.AddRange(codes[i].labels);
        codes[i + 2].blocks.AddRange(codes[i].blocks);
      }

      codes.RemoveAt(i + 1);
      codes.RemoveAt(i);
      break;
    }

    return codes;
  }

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

      // A disabled-auto-play skip is handled at the CinematicsManager.Play(VideoEntry, ...) prefix
      // (CinematicsManagerPatches.PlayPrefix) — that is the point actually proven to run on every
      // boot (confirmed 2026-09-22 via a Harmony patch-info check: this coroutine's own prefix
      // never once logged across several real playtests, despite Harmony reporting it attached —
      // unexplained, flagged as a follow-up, not blocking). Play() below still honestly reports
      // false on a skip (reporting true here once caused a stuck main menu — see PlayPrefix), so
      // the else branch below correctly re-enables the main menu.
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
