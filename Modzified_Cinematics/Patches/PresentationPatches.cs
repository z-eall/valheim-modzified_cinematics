using System.Collections.Generic;
using HarmonyLib;

namespace Modzified_Cinematics.Patches;

/// <summary>
/// Loading-screens YAML → weighted Hud tip pool (k=2) + one still-art list for every loading / portal / door / menu-load screen.
/// Fade / spinner / portal-no-tips stay vanilla. Intro TextViewer never overridden.
/// </summary>
internal static class PresentationPatches
{
  private static List<string>? _vanillaLoadingTips;

  [HarmonyPatch(typeof(Hud), "ShuffleTips")]
  private static class ShuffleTipsPatch
  {
    [HarmonyPrefix]
    private static void Prefix(Hud __instance)
    {
      if (__instance.m_loadingTips == null)
      {
        return;
      }

      _vanillaLoadingTips ??= new List<string>(__instance.m_loadingTips);

      WriteTipRef(_vanillaLoadingTips);

      if (Settings.SkipCustom || LoadingScreensStore.LoadingTips.Count == 0)
      {
        if (_vanillaLoadingTips.Count > 0 &&
            !SameTipList(__instance.m_loadingTips, _vanillaLoadingTips))
        {
          __instance.m_loadingTips = new List<string>(_vanillaLoadingTips);
        }

        return;
      }

      // Include vanilla tips (cfg): on = weighted mix (k=2); off = only the author's tips.
      __instance.m_loadingTips = Settings.VanillaTipsOn
        ? LoadingScreensStore.BuildWeightedTipPool(_vanillaLoadingTips, LoadingScreensStore.LoadingTips)
        : new List<string>(LoadingScreensStore.LoadingTips);
      ModzifiedCinematicsPlugin.LogAt(
        BepInEx.Logging.LogLevel.Debug,
        $"Cinematics presentation: tip pool {(Settings.VanillaTipsOn ? "weighted k=2" : "custom only")} " +
        $"(vanilla={_vanillaLoadingTips.Count}, custom={LoadingScreensStore.LoadingTips.Count}, " +
        $"pool={__instance.m_loadingTips.Count}).");
    }
  }

  [HarmonyPatch(typeof(Hud), "UpdateBlackScreen")]
  private static class UpdateBlackScreenArtPatch
  {
    [HarmonyPostfix]
    private static void Postfix(Hud __instance, Player player)
    {
      LoadingArt.Tick(__instance, player);
    }
  }

  /// <summary>Main menu → world load screen (<c>FejdStartup.m_loading</c>); covers Skip Intro and first spawn.</summary>
  [HarmonyPatch(typeof(FejdStartup), "LoadMainScene")]
  private static class MenuLoadingArtPatch
  {
    [HarmonyPostfix]
    private static void Postfix(FejdStartup __instance)
    {
      LoadingArt.BeginMenu(__instance.m_loading);
    }
  }

  /// <summary>Vanilla tip keys + their text for the ref file; runs once per game language.</summary>
  private static void WriteTipRef(List<string> vanilla)
  {
    Localization? loc = Localization.instance;
    if (loc == null || vanilla.Count == 0)
    {
      return;
    }

    LoadingScreensStore.WriteRefTips(vanilla, loc.Localize, loc.GetSelectedLanguage());
  }

  private static bool SameTipList(List<string> a, List<string> b)
  {
    if (a.Count != b.Count)
    {
      return false;
    }

    for (int i = 0; i < a.Count; i++)
    {
      if (!string.Equals(a[i], b[i], System.StringComparison.Ordinal))
      {
        return false;
      }
    }

    return true;
  }
}
