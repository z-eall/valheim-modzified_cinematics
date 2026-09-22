using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace Modzified_Cinematics;

/// <summary>
/// Registers YAML-only film names into <see cref="CinematicsManager.m_videos"/> so
/// <c>Play(name)</c> / dream / console resolve. Gallery stays Hidden (skipped in Fejd).
/// </summary>
internal static class CatalogInject
{
  private static readonly HashSet<string> SoftRef =
    new(VanillaCatalog.SoftRefNames, StringComparer.Ordinal);

  private static readonly HashSet<string> Injected =
    new(StringComparer.Ordinal);

  internal static bool IsVanillaSoftRef(string? name)
  {
    return !string.IsNullOrEmpty(name) && SoftRef.Contains(name!);
  }

  /// <summary>True if this name was added by us (not SoftRef / SoftRef dump).</summary>
  internal static bool IsInjected(string? name)
  {
    return !string.IsNullOrEmpty(name) && Injected.Contains(name!);
  }

  /// <summary>
  /// Sync injected rows from compiled rules. New ids need non-empty <c>clips:</c>;
  /// empty → warn and remove/skip inject.
  /// </summary>
  internal static void SyncFromStore()
  {
    CinematicsManager? cm = CinematicsManager.s_instance;
    if (cm == null || cm.m_videos == null)
    {
      return;
    }

    HashSet<string> want = new(StringComparer.Ordinal);
    foreach (KeyValuePair<string, CinematicsStore.CompiledRule> kv in CinematicsStore.AllRules())
    {
      string name = kv.Key;
      if (IsVanillaSoftRef(name))
      {
        continue;
      }

      CinematicsStore.CompiledRule rule = kv.Value;
      if (rule.Clips == null || rule.Clips.Count == 0)
      {
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Warning,
          $"Cinematics catalog: new name '{name}' needs clips: — not injected.");
        continue;
      }

      want.Add(name);
    }

    // Remove stale injections.
    for (int i = cm.m_videos.Count - 1; i >= 0; i--)
    {
      CinematicsManager.VideoEntry entry = cm.m_videos[i];
      if (entry == null || string.IsNullOrEmpty(entry.m_name))
      {
        continue;
      }

      if (!Injected.Contains(entry.m_name))
      {
        continue;
      }

      if (!want.Contains(entry.m_name))
      {
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Info,
          $"Cinematics catalog: removed injected '{entry.m_name}'.");
        cm.m_videos.RemoveAt(i);
        Injected.Remove(entry.m_name);
      }
    }

    foreach (string name in want)
    {
      if (CinematicsManager.GetVideo(name) != null)
      {
        Injected.Add(name);
        continue;
      }

      CinematicsManager.VideoEntry entry = new()
      {
        m_name = name,
        m_settings = CinematicsManager.Settings.Hidden,
        m_videoClip = null,
        m_videoClipLow = null,
        m_subtitles = null,
        m_unlocked = false
      };
      cm.m_videos.Add(entry);
      Injected.Add(name);
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Info,
        $"Cinematics catalog: injected '{name}' (Hidden, file-backed clips).");
    }
  }
}
