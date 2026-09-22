using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Modzified_Cinematics;

/// <summary>
/// <c>modzified_loading_screens*.yaml</c> — reverse-alpha merge, field last-wins.
/// Writing stubs omit <c>ref_*</c>; tips weighted with vanilla at Shuffle time (k=2).
/// </summary>
internal static class LoadingScreensStore
{
  internal const string RelativePath = "modzified_cinematics/modzified_loading_screens.yaml";
  internal const string RefTipsFileName = "ref_modzified_loading_tips.yaml";
  internal const float TipWeightK = 2f;

  private static IReadOnlyList<string> _loadingTips = Array.Empty<string>();
  private static IReadOnlyList<string> _loadingArts = Array.Empty<string>();
  private static string _statusLine = "not loaded";

  internal static string StatusLine => _statusLine;
  internal static IReadOnlyList<string> LoadingTips => _loadingTips;
  /// <summary>Art files that exist (resolved once per YAML load), used on every loading / portal / door screen. Empty = vanilla.</summary>
  internal static IReadOnlyList<string> LoadingArts => _loadingArts;

  internal static string AbsolutePath =>
    Path.Combine(Paths.ConfigPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));

  internal static string ConfigFolder =>
    Path.GetDirectoryName(AbsolutePath) ?? Paths.ConfigPath;

  internal static string RefTipsPath => Path.Combine(ConfigFolder, RefTipsFileName);

  internal static void EnsureStub()
  {
    string path = AbsolutePath;
    string? dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
      Directory.CreateDirectory(dir);
    }

    MediaPaths.EnsureFolders();

    if (File.Exists(path))
    {
      string existing = ReadAllSafe(path);
      // Refresh only our own comment-only stub (0.3.0 shape). A file with real entries is never touched.
      if (IsEffectivelyEmpty(existing) && existing.IndexOf(StubSignature, StringComparison.Ordinal) >= 0)
      {
        File.WriteAllText(path, FormatStub(), Encoding.UTF8);
        ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Loading screens YAML: refreshed stub at {path}");
      }

      return;
    }

    File.WriteAllText(path, FormatStub(), Encoding.UTF8);
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Loading screens YAML: wrote stub at {path}");
  }

  private const string StubSignature = "This is the loading tips / art file";

  /// <summary>
  /// Write <c>ref_modzified_loading_tips.yaml</c> once (delete it to regenerate) from the vanilla Hud tip list, each key followed by
  /// its text (game language) as a <c>#</c> comment so authors can tell what a tip says.
  /// </summary>
  internal static void WriteRefTips(IReadOnlyList<string> vanillaTips, Func<string, string> localize, string language)
  {
    if (File.Exists(RefTipsPath))
    {
      return;
    }

    try
    {
      if (!Directory.Exists(ConfigFolder))
      {
        Directory.CreateDirectory(ConfigFolder);
      }

      StringBuilder sb = new();
      sb.AppendLine("# Auto-generated from the vanilla loading tips. Delete the entire file to regenerate (new version or language).");
      sb.AppendLine("# Copy lines into modzified_loading_screens.yaml under loadingTips:.");
      sb.AppendLine("# Your tips show ~2x more than vanilla's. Turn vanilla off in the .cfg.");
      sb.AppendLine($"# Text after # is the tip text ({language}) — not part of the key.");
      sb.AppendLine();
      sb.AppendLine("loadingTips:");
      foreach (string tip in vanillaTips)
      {
        if (string.IsNullOrWhiteSpace(tip))
        {
          continue;
        }

        string text = SingleLine(localize(tip.Trim()));
        sb.Append("  - ").Append(FormatScalar(tip.Trim()));
        if (text.Length > 0 && !string.Equals(text, tip.Trim(), StringComparison.Ordinal))
        {
          sb.Append("   # ").Append(text);
        }

        sb.AppendLine();
      }

      File.WriteAllText(RefTipsPath, sb.ToString(), Encoding.UTF8);
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Loading screens YAML: wrote {RefTipsFileName} ({vanillaTips.Count} tips, {language}).");
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Loading screens YAML: failed to write tip ref dump ({ex.Message}).");
    }
  }

  private static string SingleLine(string text)
  {
    return string.Join(" ", (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
  }

  /// <summary>
  /// Build shuffled pool: each vanilla weight 1; each custom weight <c>k * (V/C)</c> (k=2).
  /// Implemented as list duplicates so vanilla <see cref="Hud.ShuffleTips"/> stays untouched.
  /// </summary>
  internal static List<string> BuildWeightedTipPool(IReadOnlyList<string> vanilla, IReadOnlyList<string> custom)
  {
    List<string> pool = new();
    if (custom == null || custom.Count == 0)
    {
      pool.AddRange(vanilla);
      return pool;
    }

    if (vanilla == null || vanilla.Count == 0)
    {
      pool.AddRange(custom);
      return pool;
    }

    int v = vanilla.Count;
    int c = custom.Count;
    int copies = Math.Max(1, (int)Math.Round(TipWeightK * v / (double)c));
    foreach (string tip in vanilla)
    {
      pool.Add(tip);
    }

    foreach (string tip in custom)
    {
      for (int i = 0; i < copies; i++)
      {
        pool.Add(tip);
      }
    }

    return pool;
  }

  /// <summary>Merge all <c>modzified_loading_screens*.yaml</c> under the config folder (skip <c>ref_*</c>).</summary>
  internal static LoadingScreenData MergeFromDisk()
  {
    EnsureStub();
    List<string> files = YamlGlob.GetFilesReversed(ConfigFolder, YamlGlob.LoadingScreensPattern);
    LoadingScreenData merged = new();
    foreach (string file in files)
    {
      if (!TryParseFile(file, out LoadingScreenData? partial) || partial == null)
      {
        continue;
      }

      // Last file wins per key; an empty key never clears an earlier one.
      if (partial.loadingTips.Count > 0)
      {
        merged.loadingTips = partial.loadingTips;
      }

      if (partial.loadingArt.Count > 0)
      {
        merged.loadingArt = partial.loadingArt;
      }
    }

    return merged;
  }

  internal static void ApplyMerged(LoadingScreenData data)
  {
    _loadingTips = new List<string>(data.loadingTips);
    _loadingArts = ResolveArt(data.loadingArt);
    _statusLine =
      $"tips={_loadingTips.Count}, " +
      $"loadingArt={data.loadingArt.Count} ({_loadingArts.Count} found)";
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Loading screens YAML: {_statusLine}.");
  }

  /// <summary>Resolve entries under <c>arts\</c> once per YAML load; keep the ones that exist. Warn once per entry.</summary>
  private static IReadOnlyList<string> ResolveArt(IReadOnlyList<string> entries)
  {
    List<string> found = new();
    foreach (string entry in entries)
    {
      string abs = MediaPaths.ResolveArt(entry);
      string ext = Path.GetExtension(abs);
      if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
          !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
          !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
      {
        ModzifiedCinematicsPlugin.LogWarnOnce($"Art file must be PNG or JPG: {entry}. Skipped. Rename or convert the file.");
        continue;
      }

      if (!File.Exists(abs))
      {
        MediaPaths.WarnMissing("Art", entry, MediaPaths.ArtsFolder, "modzified_loading_screens.yaml");
        continue;
      }

      found.Add(abs);
    }

    return found;
  }

  internal static void LoadMergedFromDisk()
  {
    ApplyMerged(MergeFromDisk());
  }

  internal static void CompileFromText(string text)
  {
    if (IsEffectivelyEmpty(text))
    {
      ApplyMerged(new LoadingScreenData());
      _statusLine = "empty (vanilla tips/art)";
      return;
    }

    if (!TryParseText(text, out LoadingScreenData? raw) || raw == null)
    {
      _statusLine = "parse error";
      return;
    }

    ApplyMerged(raw);
  }

  internal static string FormatYaml(LoadingScreenData data)
  {
    StringBuilder sb = new();
    sb.AppendLine("# Modzified_Cinematics — loading tips + still art. (Server-synced)");
    sb.AppendLine("# " + StubSignature + " (not cinematic rows).");
    sb.AppendLine();
    AppendList(sb, "loadingTips", data.loadingTips);
    AppendList(sb, "loadingArt", data.loadingArt);
    return sb.ToString();
  }

  private static void AppendList(StringBuilder sb, string key, List<string> items)
  {
    if (items.Count == 0)
    {
      return;
    }

    sb.Append(key).AppendLine(":");
    foreach (string item in items)
    {
      sb.Append("- ").AppendLine(FormatScalar(item));
    }
  }

  internal static string FormatStub()
  {
    StringBuilder sb = new();
    sb.AppendLine("# Modzified_Cinematics — loading tips + still art. (Server-synced)");
    sb.AppendLine("# " + StubSignature + ".");
    sb.AppendLine("# loadingTips: loading, portal, door, logout screens. Mixes with vanilla (off in .cfg). Copy keys from ref_modzified_loading_tips.yaml.");
    sb.AppendLine("# loadingArt: same screens plus main-menu load. Files go in arts\\ — PNG or JPG, file name only.");
    sb.AppendLine("# One entry picked at random per list. Leave \"-\" empty for vanilla. Dim it with Art dim in .cfg.");
    sb.AppendLine("# First-spawn scroll is always vanilla.");
    sb.AppendLine();
    sb.AppendLine("loadingTips:");
    sb.AppendLine("- ");
    sb.AppendLine();
    sb.AppendLine("loadingArt:");
    sb.AppendLine("- ");
    return sb.ToString();
  }

  private static bool TryParseFile(string path, out LoadingScreenData? data)
  {
    data = null;
    try
    {
      string text = File.ReadAllText(path);
      return TryParseText(text, out data, $"Loading screens YAML {Path.GetFileName(path)}");
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Error,
        $"Loading screens YAML: failed to read {path}: {ex.Message}");
      return false;
    }
  }

  private static bool TryParseText(string text, out LoadingScreenData? data, string source = "Loading screens YAML (synced)")
  {
    data = null;
    if (IsEffectivelyEmpty(text))
    {
      data = new LoadingScreenData();
      return true;
    }

    try
    {
      IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
      LoadingScreenRaw raw = deserializer.Deserialize<LoadingScreenRaw>(text) ?? new LoadingScreenRaw();
      YamlText.WarnUnknownKeys(text, source, LoadingScreenRaw.ValidKeys, expectList: false, rowLabelKey: "");
      data = new LoadingScreenData
      {
        loadingTips = LoadingScreenData.Normalize(raw.loadingTips),
        loadingArt = LoadingScreenData.Normalize(raw.loadingArt),
      };
      return true;
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Error,
        $"Loading screens YAML: parse failed: {ex.Message}");
      return false;
    }
  }

  private static string FormatScalar(string value)
  {
    if (value.IndexOfAny(new[] { ':', '#', '"', '\'', '\n', '\r' }) >= 0 ||
        value.StartsWith(" ", StringComparison.Ordinal) ||
        value.EndsWith(" ", StringComparison.Ordinal))
    {
      string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
      return "\"" + escaped + "\"";
    }

    return value;
  }

  private static string ReadAllSafe(string path)
  {
    try
    {
      return File.Exists(path) ? File.ReadAllText(path) : "";
    }
    catch
    {
      return "";
    }
  }

  private static bool IsEffectivelyEmpty(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return true;
    }

    using StringReader reader = new(text);
    string? line;
    while ((line = reader.ReadLine()) != null)
    {
      string t = line.Trim();
      if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal))
      {
        continue;
      }

      return false;
    }

    return true;
  }
}
