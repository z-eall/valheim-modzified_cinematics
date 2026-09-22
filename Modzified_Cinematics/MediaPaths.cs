using System;
using System.IO;
using BepInEx.Logging;

namespace Modzified_Cinematics;

/// <summary>
/// One rule for clips and art: a relative name resolves under its own folder
/// (<c>clips\</c> / <c>arts\</c>); a leading folder name is accepted, never doubled; absolute paths pass through.
/// </summary>
internal static class MediaPaths
{
  internal const string ClipsFolderName = "clips";
  internal const string ArtsFolderName = "arts";

  internal static string ClipsFolder => Path.Combine(CinematicsStore.ConfigFolder, ClipsFolderName);
  internal static string ArtsFolder => Path.Combine(CinematicsStore.ConfigFolder, ArtsFolderName);

  /// <summary>Create <c>clips\</c> and <c>arts\</c> (no sample files). Safe to call on every reload.</summary>
  internal static void EnsureFolders()
  {
    EnsureFolder(ClipsFolder);
    EnsureFolder(ArtsFolder);
  }

  internal static string Resolve(string entry, string folder, string folderName)
  {
    string name = entry.Trim().Replace('\\', '/');
    if (Path.IsPathRooted(entry.Trim()))
    {
      return Path.GetFullPath(entry.Trim());
    }

    string prefix = folderName + "/";
    while (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
      name = name.Substring(prefix.Length);
    }

    return Path.GetFullPath(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)));
  }

  internal static string ResolveClip(string entry) => Resolve(entry, ClipsFolder, ClipsFolderName);

  internal static string ResolveArt(string entry) => Resolve(entry, ArtsFolder, ArtsFolderName);

  /// <summary>STE warning: what is missing, where the mod looked, the smallest fix.</summary>
  internal static void WarnMissing(string kind, string entry, string folder, string yamlFile)
  {
    ModzifiedCinematicsPlugin.LogWarnOnce(
      $"{kind} file not found: {entry}. Looked in {folder}. Put the file there or fix the name in {yamlFile}.");
  }

  private static void EnsureFolder(string path)
  {
    try
    {
      if (!Directory.Exists(path))
      {
        Directory.CreateDirectory(path);
        ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Created folder {path}.");
      }
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Warning, $"Could not create folder {path}: {ex.Message}");
    }
  }
}
