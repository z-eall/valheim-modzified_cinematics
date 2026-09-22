using System;
using System.Collections.Generic;
using System.IO;

namespace Modzified_Cinematics;

/// <summary>
/// EWP-style glob: alphabetical then <see cref="List{T}.Reverse"/> (study habit, not a paste).
/// </summary>
internal static class YamlGlob
{
  internal const string FilmsPattern = "modzified_cinematics*.yaml";
  internal const string LoadingScreensPattern = "modzified_loading_screens*.yaml";

  /// <summary>Full paths, reverse-alphabetical (OrdinalIgnoreCase).</summary>
  internal static List<string> GetFilesReversed(string folder, string searchPattern)
  {
    List<string> list = new();
    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
    {
      return list;
    }

    string[] files;
    try
    {
      files = Directory.GetFiles(folder, searchPattern, SearchOption.AllDirectories);
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        BepInEx.Logging.LogLevel.Error,
        $"YAML glob: GetFiles({searchPattern}) failed: {ex.Message}");
      return list;
    }

    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
    Array.Reverse(files);
    foreach (string file in files)
    {
      string name = Path.GetFileName(file);
      if (IsReferenceFile(name))
      {
        continue;
      }

      list.Add(file);
    }

    return list;
  }

  /// <summary>Auto-generated dumps (<c>ref_*</c>) — never load as writing overrides.</summary>
  internal static bool IsReferenceFile(string fileName)
  {
    return !string.IsNullOrEmpty(fileName) &&
           fileName.StartsWith("ref_", StringComparison.OrdinalIgnoreCase);
  }

  internal static bool FileNameMatches(string fileName, string searchPattern)
  {
    // Directory.GetFiles already filtered; keep a cheap check for watcher events.
    if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(searchPattern))
    {
      return false;
    }

    if (IsReferenceFile(fileName))
    {
      return false;
    }

    int star = searchPattern.IndexOf('*');
    if (star < 0)
    {
      return fileName.Equals(searchPattern, StringComparison.OrdinalIgnoreCase);
    }

    string prefix = searchPattern.Substring(0, star);
    string suffix = searchPattern.Substring(star + 1);
    return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
           fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
  }
}
