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
/// Film rules: <c>modzified_cinematics*.yaml</c> (reverse-alpha merge, dup name warn+last wins).
/// Host packs films + loading-screens into one ServerSync blob (<see cref="SyncPayloadData"/>).
/// Mentor habits: EWP glob order; Skill_Bombs host-wins file watch — study, don’t paste.
/// </summary>
internal static class CinematicsStore
{
  internal const string RelativePath = "modzified_cinematics/modzified_cinematics.yaml";
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
  internal static string ClipsFolder => MediaPaths.ClipsFolder;

  internal readonly struct CompiledRule
  {
    internal CompiledRule(
      string name,
      bool enabled,
      string? type,
      TriggerTypeParse.Parsed? parsed,
      bool dream,
      IReadOnlyList<string> clips,
      TriggerTypeParse.OneTimeScope? oneTime,
      float? cooldown)
    {
      Name = name;
      Enabled = enabled;
      Type = type;
      Parsed = parsed;
      Dream = dream;
      Clips = clips;
      OneTime = oneTime;
      Cooldown = cooldown;
    }

    internal string Name { get; }
    internal bool Enabled { get; }
    /// <summary>Raw <c>type:</c> string, or null.</summary>
    internal string? Type { get; }
    /// <summary>Parsed Wave 2 type; null when <see cref="Type"/> missing or invalid.</summary>
    internal TriggerTypeParse.Parsed? Parsed { get; }
    internal bool Dream { get; }
    internal IReadOnlyList<string> Clips { get; }
    /// <summary>Null = use type default (e.g. bossstone oneTime on, world-scoped).</summary>
    internal TriggerTypeParse.OneTimeScope? OneTime { get; }
    internal float? Cooldown { get; }
  }

  internal static void Init(ConfigSync sync)
  {
    _synced = new CustomSyncedValue<string>(sync, SyncId, "");
    _synced.ValueChanged += OnSyncedValueChanged;

    // Authoring files on every process (client-only / solo / dedicated).
    EnsureFileMigrated();
    LoadingScreensStore.EnsureStub();

    void BecomeHost(bool truth)
    {
      if (!truth)
      {
        TryCompileLocalIfNoSync();
        return;
      }

      EnsureFileMigrated();
      LoadingScreensStore.EnsureStub();
      LoadHostMergedIntoSync();
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
    LoadingScreensStore.EnsureStub();
    if (Settings.Sync != null && Settings.Sync.IsSourceOfTruth)
    {
      LoadHostMergedIntoSync();
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
      ModzifiedCinematicsPlugin.ResetWarnOnce();
      EnsureFileMigrated();
      LoadingScreensStore.EnsureStub();
      LoadHostMergedIntoSync();
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

  internal static bool TryGetRule(string? name, out CompiledRule rule)
  {
    if (string.IsNullOrEmpty(name))
    {
      rule = default;
      return false;
    }

    return _byName.TryGetValue(name!, out rule);
  }

  /// <summary>Snapshot of compiled film rules (for catalog inject).</summary>
  internal static IReadOnlyDictionary<string, CompiledRule> AllRules() => _byName;

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
      else
      {
        MediaPaths.WarnMissing("Clip", clip.Trim(), ClipsFolder, "modzified_cinematics.yaml");
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
    return MediaPaths.ResolveClip(relativeOrAbsolute);
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

    ApplySyncedOrLocalText(text);
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
      ApplySyncedOrLocalText(synced);
      return;
    }

    EnsureFileMigrated();
    LoadingScreensStore.EnsureStub();
    ApplyMergedFromDisk();
  }

  private static void ApplySyncedOrLocalText(string text)
  {
    if (TryParseSyncPayload(text, out SyncPayloadData? payload) && payload != null)
    {
      CompileFromText(payload.films ?? "");
      LoadingScreensStore.CompileFromText(payload.loadingScreens ?? "");
      return;
    }

    // Not a sync envelope: treat the text as a film list.
    CompileFromText(text);
    LoadingScreensStore.LoadMergedFromDisk();
  }

  private static void ApplyMergedFromDisk()
  {
    List<CinematicRuleData> films = MergeFilmsFromDisk(out int filmFiles, out int dupWarns);
    CompileFromRows(films, filmFiles, dupWarns);
    LoadingScreensStore.LoadMergedFromDisk();
  }

  private static void LoadHostMergedIntoSync()
  {
    try
    {
      List<CinematicRuleData> films = MergeFilmsFromDisk(out int filmFiles, out int dupWarns);
      LoadingScreenData loading = LoadingScreensStore.MergeFromDisk();
      string filmsYaml = FormatYaml(films);
      string loadingYaml = LoadingScreensStore.FormatYaml(loading);
      string blob = FormatSyncPayload(filmsYaml, loadingYaml);
      _synced.AssignLocalValue(blob);
      CompileFromRows(films, filmFiles, dupWarns);
      LoadingScreensStore.ApplyMerged(loading);
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Info,
        $"Cinematics YAML: host synced {filmFiles} film file(s), loading-screens merged.");
    }
    catch (Exception ex)
    {
      _lastError = ex.Message;
      _statusLine = "read failed";
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Error, $"Cinematics YAML: host merge failed: {ex.Message}");
    }
  }

  private static List<CinematicRuleData> MergeFilmsFromDisk(out int fileCount, out int dupWarns)
  {
    fileCount = 0;
    dupWarns = 0;
    List<CinematicRuleData> stream = new();
    List<string> files = YamlGlob.GetFilesReversed(ConfigFolder, YamlGlob.FilmsPattern);
    fileCount = files.Count;
    foreach (string file in files)
    {
      if (!TryParseFilmFile(file, out List<CinematicRuleData> rows))
      {
        continue;
      }

      stream.AddRange(rows);
    }

    // Last in stream wins (after reverse-alpha AddRange).
    Dictionary<string, CinematicRuleData> byName = new(StringComparer.Ordinal);
    List<string> order = new();
    foreach (CinematicRuleData row in stream)
    {
      if (string.IsNullOrWhiteSpace(row.name))
      {
        continue;
      }

      string key = row.name!.Trim();
      row.name = key;
      if (byName.ContainsKey(key))
      {
        dupWarns++;
        order.Remove(key);
        ModzifiedCinematicsPlugin.LogWarnOnce(
          $"Cinematics YAML: duplicate name '{key}' — last wins.");
      }

      order.Add(key);
      byName[key] = NormalizeDefaults(row);
    }

    List<CinematicRuleData> merged = new(order.Count);
    foreach (string key in order)
    {
      merged.Add(byName[key]);
    }

    return merged;
  }

  private static bool TryParseFilmFile(string path, out List<CinematicRuleData> rows)
  {
    rows = new List<CinematicRuleData>();
    try
    {
      string text = File.ReadAllText(path);
      if (IsEffectivelyEmpty(text))
      {
        return true;
      }

      IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
      rows = deserializer.Deserialize<List<CinematicRuleData>>(text) ?? new List<CinematicRuleData>();
      YamlText.WarnUnknownKeys(text, $"Cinematics YAML {Path.GetFileName(path)}", CinematicRuleData.ValidKeys, expectList: true, rowLabelKey: "name");
      return true;
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Error,
        $"Cinematics YAML: skip {path} ({ex.Message}).");
      return false;
    }
  }

  private static string FormatSyncPayload(string filmsYaml, string loadingYaml)
  {
    SyncPayloadData payload = new()
    {
      films = filmsYaml,
      loadingScreens = loadingYaml
    };
    ISerializer serializer = new SerializerBuilder()
      .WithNamingConvention(CamelCaseNamingConvention.Instance)
      .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
      .Build();
    return serializer.Serialize(payload);
  }

  private static bool TryParseSyncPayload(string text, out SyncPayloadData? payload)
  {
    payload = null;
    if (IsEffectivelyEmpty(text))
    {
      return false;
    }

    // Heuristic: sync envelope always has a top-level films: key from our serializer.
    string live = YamlText.StripComments(text);
    if (!live.Contains("films:") && !live.Contains("loadingScreens:"))
    {
      return false;
    }

    try
    {
      IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
      payload = deserializer.Deserialize<SyncPayloadData>(text);
      return payload != null && (payload.films != null || payload.loadingScreens != null);
    }
    catch
    {
      return false;
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

    MediaPaths.EnsureFolders();

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
        WriteRefFilms();
        return;
      }
    }

    // Stub only when the file is missing or blank. A comment-only file is left alone: rewriting it
    // touched the file, which re-triggered the watcher, which rewrote it (200 writes in one run).
    if (!File.Exists(path) || string.IsNullOrWhiteSpace(existing))
    {
      // Wave 2: writing stub is empty + # examples; SoftRef catalog lives in ref_*.
      File.WriteAllText(path, FormatFilmsStub(), Encoding.UTF8);
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Cinematics YAML: wrote stub at {path}");
    }

    WriteRefFilms();
  }

  /// <summary>Write <c>ref_modzified_cinematics.yaml</c> (SoftRef catalog) once. Delete the file to regenerate.</summary>
  private static void WriteRefFilms()
  {
    try
    {
      string refPath = Path.Combine(ConfigFolder, "ref_modzified_cinematics.yaml");
      if (File.Exists(refPath))
      {
        return;
      }

      IReadOnlyList<string> catalog = VanillaCatalog.ResolveNames();
      List<CinematicRuleData> rows = new(catalog.Count);
      foreach (string name in catalog)
      {
        rows.Add(DefaultRow(name));
      }

      StringBuilder sb = new();
      sb.AppendLine("# Auto-generated catalog of every vanilla cinematic. Read-only.");
      sb.AppendLine("# Copy a row into modzified_cinematics.yaml to customize it.");
      sb.AppendLine();
      sb.Append(FormatYamlBody(rows));
      File.WriteAllText(refPath, sb.ToString(), Encoding.UTF8);
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics YAML: wrote ref_modzified_cinematics.yaml ({rows.Count} rows).");
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Cinematics YAML: failed to write film ref dump ({ex.Message}).");
    }
  }

  private static string FormatFilmsStub()
  {
    StringBuilder sb = new();
    sb.AppendLine("# Modzified_Cinematics — turn cinematics on/off or replace them. (Server-synced)");
    sb.AppendLine("# Paths are relative to this folder (BepInEx/config/modzified_cinematics/clips).");
    sb.AppendLine("# Empty = full vanilla behavior. Copy rows from ref_modzified_cinematics.yaml.");
    sb.AppendLine("# type grammar: kind, param1 param2 — one comma after kind; params space-separated.");
    sb.AppendLine("#");
    sb.AppendLine("# - name: $biome_blackforest");
    sb.AppendLine("#   enabled: true");
    sb.AppendLine("#   type: kill, lasthit $enemy_eikthyr");
    sb.AppendLine("#   dream: true");
    sb.AppendLine("#   clips:");
    sb.AppendLine("#   - my_blackforest.mp4");
    sb.AppendLine("#");
    sb.AppendLine("# - name: my_campfire_story");
    sb.AppendLine("#   enabled: true");
    sb.AppendLine("#   type: state, sleep");
    sb.AppendLine("#   clips:");
    sb.AppendLine("#   - campfire_tale.mp4");
    sb.AppendLine();
    return sb.ToString();
  }

  private static void StartWatcher()
  {
    string dir = ConfigFolder;
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
    {
      return;
    }

    _watcher?.Dispose();
    _watcher = new FileSystemWatcher(dir)
    {
      IncludeSubdirectories = true,
      NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName
    };
    _watcher.Changed += OnWatchedFsEvent;
    _watcher.Created += OnWatchedFsEvent;
    _watcher.Renamed += OnWatchedFsEvent;
    _watcher.Deleted += OnWatchedFsEvent;
    _watcher.EnableRaisingEvents = true;
  }

  private static void OnWatchedFsEvent(object sender, FileSystemEventArgs e)
  {
    string name = Path.GetFileName(e.FullPath);
    if (YamlGlob.FileNameMatches(name, YamlGlob.FilmsPattern) ||
        YamlGlob.FileNameMatches(name, YamlGlob.LoadingScreensPattern))
    {
      QueueReload();
    }
  }

  private static void QueueReload()
  {
    lock (WatchLock)
    {
      _reloadPending = true;
    }
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
      YamlText.WarnUnknownKeys(text, "Cinematics YAML (synced)", CinematicRuleData.ValidKeys, expectList: true, rowLabelKey: "name");
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

    CompileFromRows(raw, fileCount: 1, dupWarns: 0);
  }

  private static void CompileFromRows(List<CinematicRuleData> raw, int fileCount, int dupWarns)
  {
    _lastError = "";
    Dictionary<string, CompiledRule> next = new(StringComparer.Ordinal);
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
      string? type = string.IsNullOrWhiteSpace(data.type) ? null : data.type!.Trim();
      TriggerTypeParse.Parsed? parsed = null;
      if (type != null)
      {
        if (TriggerTypeParse.TryParse(type, out TriggerTypeParse.Parsed p, out string parseError))
        {
          parsed = p;
        }
        else
        {
          ModzifiedCinematicsPlugin.LogWarnOnce(
            $"Cinematics YAML: '{name}' type '{type}' invalid — {parseError} (canonical examples in README).");
        }
      }

      bool dream = data.dream == true;
      TriggerTypeParse.OneTimeScope? oneTime = null;
      if (!string.IsNullOrWhiteSpace(data.oneTime))
      {
        if (!TriggerTypeParse.TryParseOneTimeScope(data.oneTime, out oneTime, out string oneTimeError))
        {
          ModzifiedCinematicsPlugin.LogWarnOnce(
            $"Cinematics YAML: '{name}' oneTime '{data.oneTime}' invalid — {oneTimeError} (treated as omitted).");
        }
      }

      float? cooldown = data.cooldown is > 0f ? data.cooldown : null;
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

      if (dream && parsed is { Kind: TriggerTypeParse.Kind.State, State: TriggerTypeParse.StateMode.Sleep })
      {
        ModzifiedCinematicsPlugin.LogWarnOnce(
          $"Cinematics YAML: '{name}' dream: true with state, sleep is ignored (sleep always plays now).");
      }

      next[name] = new CompiledRule(name, enabled, type, parsed, dream, clips, oneTime, cooldown);
    }

    _byName = next;
    string filesBit = fileCount > 0 ? $", {fileCount} file(s)" : "";
    string dupBit = dupWarns > 0 ? $", {dupWarns} dup warn(s)" : "";
    _statusLine = skipped > 0
      ? $"{next.Count} rows loaded ({skipped} skipped{filesBit}{dupBit})"
      : $"{next.Count} rows loaded{filesBit}{dupBit}";
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Cinematics YAML: {_statusLine}.");
    CatalogInject.SyncFromStore();
  }

  internal static string FormatYaml(IReadOnlyList<CinematicRuleData> rows)
  {
    StringBuilder sb = new();
    sb.AppendLine("# Modzified_Cinematics — turn cinematics on/off or replace them. (Server-synced)");
    sb.AppendLine("# Paths are relative to this folder (BepInEx/config/modzified_cinematics/clips).");
    sb.AppendLine("# Main menu → Cinematics can still replay a slot that is disabled.");
    sb.AppendLine("# Media: .mp4 + H.264 video + AAC audio (AV1 and odd codecs often fail or play silent).");
    sb.AppendLine("# Clip paths are written double-quoted so spaces stay obvious on rewrite.");
    sb.AppendLine("# Optional: type / dream (when to play), oneTime: player/world / cooldown. Dump order: name → enabled → type/dream → clips/oneTime/cooldown.");
    sb.AppendLine("# type grammar (EWP): kind, param1 param2 — one comma after kind; params space-separated.");
    sb.AppendLine("# Loading tips/art live in modzified_loading_screens.yaml (separate file kind).");
    sb.AppendLine();
    sb.Append(FormatYamlBody(rows));
    return sb.ToString();
  }

  private static string FormatYamlBody(IReadOnlyList<CinematicRuleData> rows)
  {
    StringBuilder sb = new();
    for (int i = 0; i < rows.Count; i++)
    {
      CinematicRuleData row = rows[i];
      string name = row.name ?? "";
      bool enabled = row.enabled ?? true;

      foreach (string clueLine in VanillaCatalog.GetClueLines(name))
      {
        sb.Append("# ").AppendLine(clueLine);
      }

      sb.AppendLine("# Default: enabled true, clips empty (= vanilla video)");

      sb.Append("- name: ").AppendLine(name);
      sb.Append("  enabled: ").AppendLine(enabled ? "true" : "false");
      if (!string.IsNullOrWhiteSpace(row.type))
      {
        sb.Append("  type: ").AppendLine(row.type!.Trim());
      }

      if (row.dream == true)
      {
        sb.AppendLine("  dream: true");
      }

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

      if (!string.IsNullOrWhiteSpace(row.oneTime))
      {
        sb.Append("  oneTime: ").AppendLine(row.oneTime!.Trim());
      }

      if (row.cooldown is > 0f)
      {
        sb.Append("  cooldown: ").AppendLine(
          row.cooldown.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
