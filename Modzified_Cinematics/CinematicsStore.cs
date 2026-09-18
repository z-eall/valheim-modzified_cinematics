using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using ServerSync;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Modzified_Cinematics;

/// <summary>
/// Host <c>modzified_cinematics.yaml</c> → CustomSyncedValue → compiled rules.
/// Mentor shape: Skill_Bombs XpBlocklistStore (live file watch + host-wins blob).
/// </summary>
internal static class CinematicsStore
{
  internal const string RelativePath = "modzified_cinematics/modzified_cinematics.yaml";
  private const string LegacyRelativePath = "modzified_cinematics/cinematics.yaml";
  private const string SyncId = "CinematicsYaml";

  private static CustomSyncedValue<string> _synced = null!;
  private static FileSystemWatcher? _watcher;
  private static bool _reloadPending;
  private static readonly object WatchLock = new();

  private static Dictionary<string, CompiledRule> _byName =
    new(StringComparer.Ordinal);

  private static string _statusLine = "not loaded";
  private static string _lastError = "";

  /// <summary>Set by Fejd gallery play so <c>enabled: false</c> still allows deliberate replay.</summary>
  internal static bool AllowReplayOnce { get; set; }

  internal static string StatusLine => _statusLine;
  internal static string LastError => _lastError;
  internal static string AbsolutePath =>
    Path.Combine(Paths.ConfigPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));

  internal static string ConfigFolder =>
    Path.GetDirectoryName(AbsolutePath) ?? Paths.ConfigPath;

  /// <summary>Default folder for replace media; relative clip paths resolve here.</summary>
  internal static string ClipsFolder => Path.Combine(ConfigFolder, "clips");

  internal readonly struct CompiledRule
  {
    internal CompiledRule(string name, bool enabled, IReadOnlyList<string> clips)
    {
      Name = name;
      Enabled = enabled;
      Clips = clips;
    }

    internal string Name { get; }
    internal bool Enabled { get; }
    internal IReadOnlyList<string> Clips { get; }
  }

  internal static void Init(ConfigSync sync)
  {
    _synced = new CustomSyncedValue<string>(sync, SyncId, "");
    _synced.ValueChanged += OnSyncedValueChanged;

    // Authoring file on every process (client-only / solo / dedicated).
    EnsureFileMigrated();

    void BecomeHost(bool truth)
    {
      if (!truth)
      {
        TryCompileLocalIfNoSync();
        return;
      }

      EnsureFileMigrated();
      LoadHostFileIntoSync();
      StartWatcher();
    }

    if (sync.IsSourceOfTruth)
    {
      BecomeHost(true);
    }
    else
    {
      TryCompileLocalIfNoSync();
    }

    sync.SourceOfTruthChanged += BecomeHost;
  }

  /// <summary>Re-migrate when <see cref="CinematicsManager"/> catalog becomes available.</summary>
  internal static void OnCatalogReady()
  {
    EnsureFileMigrated();
    if (Settings.Sync != null && Settings.Sync.IsSourceOfTruth)
    {
      LoadHostFileIntoSync();
    }
    else
    {
      TryCompileLocalIfNoSync();
    }
  }

  internal static void Tick()
  {
    bool reload;
    lock (WatchLock)
    {
      reload = _reloadPending;
      _reloadPending = false;
    }

    if (reload && Settings.Sync != null && Settings.Sync.IsSourceOfTruth)
    {
      EnsureFileMigrated();
      LoadHostFileIntoSync();
    }
  }

  internal static bool IsAutoPlayEnabled(string? name)
  {
    if (string.IsNullOrEmpty(name))
    {
      return true;
    }

    if (_byName.TryGetValue(name!, out CompiledRule rule))
    {
      return rule.Enabled;
    }

    return true;
  }

  /// <summary>
  /// EWM-style: random among listed clips that exist on this client.
  /// </summary>
  internal static bool TryPickReplaceClip(string? name, out string absolutePath, out PickFail fail)
  {
    absolutePath = "";
    fail = PickFail.None;

    if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name!, out CompiledRule rule))
    {
      return false;
    }

    if (rule.Clips == null || rule.Clips.Count == 0)
    {
      return false;
    }

    List<string> existing = new();
    foreach (string clip in rule.Clips)
    {
      if (string.IsNullOrWhiteSpace(clip))
      {
        continue;
      }

      string abs = ResolveMediaPath(clip.Trim());
      if (File.Exists(abs))
      {
        existing.Add(abs);
      }
    }

    if (existing.Count == 0)
    {
      fail = PickFail.Missing;
      return false;
    }

    absolutePath = existing[UnityEngine.Random.Range(0, existing.Count)];
    return true;
  }

  internal enum PickFail
  {
    None,
    Missing
  }

  internal static string ResolveMediaPath(string relativeOrAbsolute)
  {
    if (Path.IsPathRooted(relativeOrAbsolute))
    {
      return Path.GetFullPath(relativeOrAbsolute);
    }

    return Path.GetFullPath(Path.Combine(ClipsFolder, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar)));
  }

  /// <summary>
  /// Unity <see cref="UnityEngine.Video.VideoPlayer.url"/> is unreliable with raw Windows paths
  /// that contain spaces or unusual characters — use a file URI.
  /// </summary>
  internal static string ToVideoPlayerUrl(string absolutePath)
  {
    return new Uri(absolutePath).AbsoluteUri;
  }

  private static void OnSyncedValueChanged()
  {
    string text = _synced.Value ?? "";
    if (IsEffectivelyEmpty(text) && Settings.Sync != null && !Settings.Sync.IsSourceOfTruth)
    {
      TryCompileLocalIfNoSync();
      return;
    }

    CompileFromText(text);
  }

  private static void TryCompileLocalIfNoSync()
  {
    if (Settings.Sync != null && Settings.Sync.IsSourceOfTruth)
    {
      return;
    }

    string synced = _synced?.Value ?? "";
    if (!IsEffectivelyEmpty(synced))
    {
      CompileFromText(synced);
      return;
    }

    EnsureFileMigrated();
    try
    {
      string text = File.Exists(AbsolutePath) ? File.ReadAllText(AbsolutePath) : "";
      CompileFromText(text);
    }
    catch (Exception ex)
    {
      _lastError = ex.Message;
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Error, $"Cinematics YAML: failed to read local {AbsolutePath}: {ex.Message}");
    }
  }

  private static void EnsureFileMigrated()
  {
    string path = AbsolutePath;
    string? dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
      Directory.CreateDirectory(dir);
    }

    if (!Directory.Exists(ClipsFolder))
    {
      Directory.CreateDirectory(ClipsFolder);
    }

    MigrateLegacyFilenameIfNeeded(path);

    string existing = "";
    if (File.Exists(path))
    {
      try
      {
        existing = File.ReadAllText(path);
      }
      catch (Exception ex)
      {
        ModzifiedCinematicsPlugin.LogAt(LogLevel.Error, $"Cinematics YAML: failed to read {path}: {ex.Message}");
        return;
      }
    }

    bool missingOrEmpty = !File.Exists(path) || IsEffectivelyEmpty(existing);
    // Drop obsolete "# Length:" comments; also refresh when header example marker is missing.
    bool needsCommentRefresh = !missingOrEmpty &&
      (existing.Contains("# Length:") || !existing.Contains(VanillaCatalog.CommentLayoutMarker));
    List<CinematicRuleData> parsed;
    if (missingOrEmpty)
    {
      parsed = new List<CinematicRuleData>();
    }
    else if (!TryParseForMigrate(existing, out parsed))
    {
      // Leave broken file alone — host must fix YAML before we rewrite.
      return;
    }

    IReadOnlyList<string> catalog = VanillaCatalog.ResolveNames();
    List<CinematicRuleData> merged = MergePreserve(parsed, catalog, out bool changed);

    if (missingOrEmpty || changed || needsCommentRefresh)
    {
      string yaml = FormatYaml(merged);
      File.WriteAllText(path, yaml, Encoding.UTF8);
      string reason = missingOrEmpty
        ? "wrote seed"
        : changed
          ? "appended missing vanilla rows"
          : "refreshed comment layout";
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Cinematics YAML: {reason} at {path}");
    }
  }

  /// <summary>Playtest v0.1.0 used <c>cinematics.yaml</c>; rename once to guid-aligned filename.</summary>
  private static void MigrateLegacyFilenameIfNeeded(string newPath)
  {
    if (File.Exists(newPath))
    {
      return;
    }

    string legacy = Path.Combine(Paths.ConfigPath, LegacyRelativePath.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(legacy))
    {
      return;
    }

    try
    {
      File.Move(legacy, newPath);
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Cinematics YAML: renamed {legacy} → {newPath}");
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Error,
        $"Cinematics YAML: failed to rename legacy file ({ex.Message}). Copy/rename manually to {newPath}.");
    }
  }

  private static bool TryParseForMigrate(string text, out List<CinematicRuleData> parsed)
  {
    parsed = new List<CinematicRuleData>();
    try
    {
      IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
      parsed = deserializer.Deserialize<List<CinematicRuleData>>(text) ?? new List<CinematicRuleData>();
      return true;
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Error,
        $"Cinematics YAML: parse failed; not rewriting file ({ex.Message}).");
      return false;
    }
  }

  private static void LoadHostFileIntoSync()
  {
    string path = AbsolutePath;
    try
    {
      string text = File.Exists(path) ? File.ReadAllText(path) : "";
      _synced.AssignLocalValue(text);
      CompileFromText(text);
    }
    catch (Exception ex)
    {
      _lastError = ex.Message;
      _statusLine = "read failed";
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Error, $"Cinematics YAML: failed to read {path}: {ex.Message}");
    }
  }

  private static void StartWatcher()
  {
    string path = AbsolutePath;
    string? dir = Path.GetDirectoryName(path);
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
    {
      return;
    }

    _watcher?.Dispose();
    _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
    {
      NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
    };
    _watcher.Changed += (_, _) => QueueReload();
    _watcher.Created += (_, _) => QueueReload();
    _watcher.Renamed += (_, _) => QueueReload();
    _watcher.EnableRaisingEvents = true;
  }

  private static void QueueReload()
  {
    lock (WatchLock)
    {
      _reloadPending = true;
    }
  }

  private static List<CinematicRuleData> MergePreserve(
    List<CinematicRuleData> existing,
    IReadOnlyList<string> catalog,
    out bool changed)
  {
    changed = false;
    Dictionary<string, CinematicRuleData> byName = new(StringComparer.Ordinal);
    List<CinematicRuleData> extras = new();

    foreach (CinematicRuleData row in existing)
    {
      if (string.IsNullOrWhiteSpace(row.name))
      {
        continue;
      }

      string key = row.name!.Trim();
      row.name = key;
      if (!byName.ContainsKey(key))
      {
        byName[key] = row;
      }
    }

    List<CinematicRuleData> merged = new(catalog.Count + extras.Count);
    HashSet<string> catalogSet = new(StringComparer.Ordinal);
    foreach (string name in catalog)
    {
      catalogSet.Add(name);
      if (byName.TryGetValue(name, out CinematicRuleData? row))
      {
        merged.Add(NormalizeDefaults(row));
      }
      else
      {
        merged.Add(DefaultRow(name));
        changed = true;
      }
    }

    foreach (KeyValuePair<string, CinematicRuleData> kv in byName)
    {
      if (!catalogSet.Contains(kv.Key))
      {
        extras.Add(NormalizeDefaults(kv.Value));
      }
    }

    if (extras.Count > 0)
    {
      merged.AddRange(extras);
    }

    if (existing.Count == 0)
    {
      changed = true;
    }

    return merged;
  }

  private static CinematicRuleData DefaultRow(string name)
  {
    return new CinematicRuleData
    {
      name = name,
      enabled = true,
      clips = new List<string>()
    };
  }

  private static CinematicRuleData NormalizeDefaults(CinematicRuleData row)
  {
    row.enabled ??= true;
    row.clips ??= new List<string>();
    return row;
  }

  private static void CompileFromText(string text)
  {
    _lastError = "";
    Dictionary<string, CompiledRule> next = new(StringComparer.Ordinal);

    if (IsEffectivelyEmpty(text))
    {
      _byName = next;
      _statusLine = "0 rules (empty)";
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, "Cinematics YAML: loaded 0 rules.");
      return;
    }

    List<CinematicRuleData>? raw;
    try
    {
      IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
      raw = deserializer.Deserialize<List<CinematicRuleData>>(text);
    }
    catch (Exception ex)
    {
      _byName = next;
      _lastError = ex.Message;
      _statusLine = "parse error";
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Error, $"Cinematics YAML: parse failed: {ex.Message}");
      return;
    }

    if (raw == null || raw.Count == 0)
    {
      _byName = next;
      _statusLine = "0 rules";
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, "Cinematics YAML: loaded 0 rules.");
      return;
    }

    int skipped = 0;
    foreach (CinematicRuleData data in raw)
    {
      if (string.IsNullOrWhiteSpace(data.name))
      {
        skipped++;
        continue;
      }

      string name = data.name!.Trim();
      bool enabled = data.enabled ?? true;
      List<string> clips = new();
      if (data.clips != null)
      {
        foreach (string? clip in data.clips)
        {
          if (!string.IsNullOrWhiteSpace(clip))
          {
            clips.Add(clip.Trim());
          }
        }
      }

      next[name] = new CompiledRule(name, enabled, clips);
    }

    _byName = next;
    _statusLine = skipped > 0
      ? $"{next.Count} rows loaded ({skipped} skipped)"
      : $"{next.Count} rows loaded";
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Cinematics YAML: {_statusLine}.");
  }

  internal static string FormatYaml(IReadOnlyList<CinematicRuleData> rows)
  {
    StringBuilder sb = new();
    sb.AppendLine("# Modzified_Cinematics — turn story videos on/off or replace them. (Server-synced)");
    sb.AppendLine("# Paths are relative to this folder (BepInEx/config/modzified_cinematics/clips).");
    sb.AppendLine("# Main menu → Cinematics can still replay a slot that is disabled.");
    sb.AppendLine("# Media: .mp4 + H.264 video + AAC audio (AV1 and odd codecs often fail or play silent).");
    sb.AppendLine("# Clip paths are written double-quoted so spaces stay obvious on rewrite.");
    sb.AppendLine("#");
    sb.AppendLine("# Example — turn off intro, or replace Black Forest with your file:");
    sb.AppendLine("# - name: $cinematics_intro");
    sb.AppendLine("#   enabled: false");
    sb.AppendLine("#   clips:");
    sb.AppendLine("#");
    sb.AppendLine("# - name: $biome_blackforest");
    sb.AppendLine("#   enabled: true");
    sb.AppendLine("#   clips:");
    sb.AppendLine("#   - filename_without_space.mp4");
    sb.AppendLine("#   - \"filename with space use quotes.mp4\"");
    sb.AppendLine();

    for (int i = 0; i < rows.Count; i++)
    {
      CinematicRuleData row = rows[i];
      string name = row.name ?? "";
      bool enabled = row.enabled ?? true;

      // Comments above the entry (cfg-style), then the keys.
      foreach (string clueLine in VanillaCatalog.GetClueLines(name))
      {
        sb.Append("# ").AppendLine(clueLine);
      }

      sb.AppendLine("# Default: enabled true, clips empty (= vanilla video)");

      sb.Append("- name: ").AppendLine(name);
      sb.Append("  enabled: ").AppendLine(enabled ? "true" : "false");
      sb.AppendLine("  clips:");
      if (row.clips != null)
      {
        foreach (string clip in row.clips)
        {
          if (!string.IsNullOrWhiteSpace(clip))
          {
            sb.Append("  - ").AppendLine(FormatClipYamlScalar(clip.Trim()));
          }
        }
      }

      if (i < rows.Count - 1)
      {
        sb.AppendLine();
      }
    }

    return sb.ToString();
  }

  /// <summary>Always double-quote clip paths so rewrites match what players typed for spaced names.</summary>
  private static string FormatClipYamlScalar(string clip)
  {
    string escaped = clip.Replace("\\", "\\\\").Replace("\"", "\\\"");
    return "\"" + escaped + "\"";
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
