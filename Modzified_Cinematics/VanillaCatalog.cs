using System;
using System.Collections.Generic;

namespace Modzified_Cinematics;

/// <summary>
/// SoftRef offline catalog (1.0.12 / build 25253764) for dedicated / pre-Awake seed.
/// Runtime <see cref="CinematicsManager.m_videos"/> is preferred when available.
/// </summary>
internal static class VanillaCatalog
{
  internal const string IntroName = "$cinematics_intro";

  /// <summary>Marker in seeded YAML so we can refresh comment layout without wiping user enabled/clips.</summary>
  internal const string CommentLayoutMarker = "Queues again if you defeat";

  internal static readonly string[] SoftRefNames =
  {
    "$cinematics_intro",
    "$biome_blackforest",
    "$biome_swamp",
    "$biome_mountain",
    "$biome_plains",
    "$biome_mistlands",
    "$biome_ashlands",
    "$biome_deepnorth",
    "$cinematics_outro",
    "$cinematics_end_credits",
  };

  /// <summary>YAML comment lines above each entry (verified against Fejd / dream / EndCredits paths).</summary>
  internal static string[] GetClueLines(string name)
  {
    return name switch
    {
      "$cinematics_intro" => new[]
      {
        "Main-menu story intro: Plays when you launch the game",
        "Once per Valheim start, until you enter a world",
      },
      "$biome_blackforest" => new[]
      {
        "Black Forest video: Plays on the next sleep after you defeat Eikthyr",
        "Queues again if you defeat him again",
      },
      "$biome_swamp" => new[]
      {
        "Swamp video: Plays on the next sleep after you defeat The Elder",
        "Queues again if you defeat him again",
      },
      "$biome_mountain" => new[]
      {
        "Mountain video: Plays on the next sleep after you defeat Bonemass",
        "Queues again if you defeat him again",
      },
      "$biome_plains" => new[]
      {
        "Plains video: Plays on the next sleep after you defeat Moder",
        "Queues again if you defeat her again",
      },
      "$biome_mistlands" => new[]
      {
        "Mistlands video: Plays on the next sleep after you defeat Yagluth",
        "Queues again if you defeat him again",
      },
      "$biome_ashlands" => new[]
      {
        "Ashlands video: Plays on the next sleep after you defeat The Queen",
        "Queues again if you defeat her again",
      },
      "$biome_deepnorth" => new[]
      {
        "Deep North video: Plays on the next sleep after you defeat Fader",
        "Queues again if you defeat him again",
      },
      "$cinematics_outro" => new[]
      {
        "World outro: Plays when you start the ending at the stone circle",
      },
      "$cinematics_end_credits" => new[]
      {
        "End credits: Plays after the world outro (loops)",
      },
      _ => new[]
      {
        "Extra cinematic: Kept as written (not a vanilla seed row)",
      },
    };
  }

  /// <summary>
  /// SoftRef offline catalog for dedicated / pre-Awake seed (vanilla rows only).
  /// Runtime SoftRef names from <see cref="CinematicsManager.m_videos"/> when available —
  /// excludes mod-injected Hidden rows so migrate does not treat customs as vanilla seed.
  /// </summary>
  internal static IReadOnlyList<string> ResolveNames()
  {
    CinematicsManager? cm = CinematicsManager.s_instance;
    if (cm != null && cm.m_videos != null && cm.m_videos.Count > 0)
    {
      List<string> names = new(cm.m_videos.Count);
      foreach (CinematicsManager.VideoEntry entry in cm.m_videos)
      {
        if (string.IsNullOrEmpty(entry.m_name))
        {
          continue;
        }

        if (CatalogInject.IsInjected(entry.m_name))
        {
          continue;
        }

        names.Add(entry.m_name);
      }

      if (names.Count > 0)
      {
        return names;
      }
    }

    return SoftRefNames;
  }
}