using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace Modzified_Cinematics;

/// <summary>
/// Soft, best-effort check for other loaded mods that also skip/replace the story intro, main menu or
/// loading screens — they can fight over the same vanilla hooks. Matches by substring on GUID or display
/// name, not an exact GUID. Confirmed 2026-09-22 against a real profile (BepInEx names each mod's own
/// config file after its GUID, so these five are hard evidence, not a guess): com.marlthon.skipintrovideo,
/// lesly.valheim.skipintro, rdmods.custommainmenu, redseiko.valheim.intermission, Azumatt.LogoChanger.
/// BalrondImmersiveLoading (a loading-screen replacer — the closest overlap with our own art) had no config
/// file to confirm its GUID; matched by name only. "modsmithcycle" has no known real mod behind it — kept
/// anyway, harmless if it never matches. One warning at startup; never blocks, never proves a real conflict
/// — same spirit as the other soft checks in this codebase.
/// </summary>
internal static class CoexistenceCheck
{
  private static readonly string[] Watch =
  {
    "skipintro", "custommainmenu", "modsmithcycle", "intermission", "logochanger", "balrond", "immersiveloading",
  };

  internal static void RunOnce()
  {
    foreach (KeyValuePair<string, BepInEx.PluginInfo> kv in Chainloader.PluginInfos)
    {
      string guid = kv.Key ?? "";
      string name = kv.Value?.Metadata?.Name ?? "";
      foreach (string watch in Watch)
      {
        if (Contains(guid, watch) || Contains(name, watch))
        {
          ModzifiedCinematicsPlugin.LogAt(
            LogLevel.Warning,
            $"Cinematics coexistence: '{name}' ({guid}) also touches the intro/main menu. " +
            "If cinematics behave oddly, try disabling one of the two mods. Not a crash, just a heads-up.");
          break;
        }
      }
    }
  }

  private static bool Contains(string haystack, string needle)
  {
    return haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
  }
}
