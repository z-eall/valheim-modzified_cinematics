using BepInEx.Configuration;
using BepInEx.Logging;
using ServerSync;

namespace Modzified_Cinematics;

internal static class Settings
{
  internal const string SectionGeneral = "1. General";
  internal const string SectionLoading = "2. Loading screens";
  internal const string SectionLogging = "10. Logging";

  internal const LogLevel DefaultLogLevels =
    LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;

  internal static ConfigSync Sync { get; private set; } = null!;

  internal static ConfigEntry<bool>? SkipCustomCinematics { get; private set; }

  /// <summary>Temporary playtest: reset profile firstSpawn when this character name joins a world. Empty = off.</summary>
  internal static ConfigEntry<string>? DevResetFirstSpawnName { get; private set; }

  internal static ConfigEntry<float>? ArtDim { get; private set; }

  internal static ConfigEntry<bool>? IncludeVanillaTips { get; private set; }

  internal static ConfigEntry<float>? ArtZoomMax { get; private set; }

  internal static ConfigEntry<float>? ArtZoomSeconds { get; private set; }

  internal static float ArtZoomMaxValue => ArtZoomMax == null ? 1.10f : UnityEngine.Mathf.Clamp(ArtZoomMax.Value, 1f, 3f);

  internal static float ArtZoomSecondsValue => ArtZoomSeconds == null ? 25f : UnityEngine.Mathf.Clamp(ArtZoomSeconds.Value, 1f, 300f);

  internal static ConfigEntry<LogLevel>? LogLevels { get; private set; }

  /// <summary>0 = no dim, 0.7 = as dark as this gets. Capped below 1 so a mistaken high value can't black out the screen. Applies to portal art and loading art.</summary>
  internal static float ArtDimValue => ArtDim == null ? 0f : UnityEngine.Mathf.Clamp(ArtDim.Value, 0f, 0.7f);

  /// <summary>On (default) = your tips mix with vanilla tips; off = only your tips (empty list = vanilla).</summary>
  internal static bool VanillaTipsOn => IncludeVanillaTips?.Value != false;

  /// <summary>
  /// Local opt-out: skip pack/custom immersion (triggers, SoftRef clip replace, injected films).
  /// Gallery deliberate play still works via <see cref="CinematicsStore.AllowReplayOnce"/>.
  /// </summary>
  internal static bool SkipCustom => SkipCustomCinematics?.Value == true;

  internal static void Init(ConfigFile config)
  {
    Sync = new ConfigSync(ModzifiedCinematicsPlugin.ModGUID)
    {
      DisplayName = ModzifiedCinematicsPlugin.ModName,
      CurrentVersion = ModzifiedCinematicsPlugin.ModVersion,
      ModRequired = false,
      IsLocked = true
    };

    SkipCustomCinematics = BindLocal(
      config,
      SectionGeneral,
      "Skip custom cinematics",
      false,
      new ConfigDescription(
        "On = fully vanilla: no custom triggers, clips, cinematics, or loading art. Vanilla cinematics and tips still play; Main menu → Cinematics still works. Not Server-synced.",
        tags: new object[] { new ConfigurationManagerAttributes { Order = 1 } }));

    DevResetFirstSpawnName = BindLocal(
      config,
      SectionGeneral,
      "Dev reset firstSpawn name",
      "Testspawn",
      new ConfigDescription(
        "TEMPORARY playtest only. When this exact character name joins a world, force firstSpawn true so intro / type: firstSpawn can retest.\n" +
        "Leave empty to disable. Clear before publish. Not Server-synced.",
        tags: new object[] { new ConfigurationManagerAttributes { Order = 0, IsAdvanced = true } }));

    ArtDim = BindLocal(
      config,
      SectionLoading,
      "Art dim",
      0.4f,
      new ConfigDescription(
        "How dark your loading art gets. 0 = none, 0.7 = darkest. Not Server-synced.",
        new AcceptableValueRange<float>(0f, 0.7f),
        new ConfigurationManagerAttributes { Order = 4, ShowRangeAsPercent = false }));

    ArtZoomMax = BindLocal(
      config,
      SectionLoading,
      "Art zoom max",
      1.10f,
      new ConfigDescription(
        "Biggest zoom of the loading art drift: 1 = no zoom, 1.1 = 10% bigger. Not Server-synced.",
        new AcceptableValueRange<float>(1f, 3f),
        new ConfigurationManagerAttributes { Order = 3, ShowRangeAsPercent = false }));

    ArtZoomSeconds = BindLocal(
      config,
      SectionLoading,
      "Art zoom seconds",
      25f,
      new ConfigDescription(
        "Seconds for one full zoom in-and-out. Shorter = faster drift. Not Server-synced.",
        new AcceptableValueRange<float>(1f, 300f),
        new ConfigurationManagerAttributes { Order = 2, ShowRangeAsPercent = false }));

    IncludeVanillaTips = BindLocal(
      config,
      SectionLoading,
      "Include vanilla tips",
      true,
      new ConfigDescription(
        "On = mix your tips with vanilla's. Off = only yours (empty = vanilla). Not Server-synced.",
        tags: new object[] { new ConfigurationManagerAttributes { Order = 1 } }));

    LogLevels = BindLocal(
      config,
      SectionLogging,
      "Log levels",
      DefaultLogLevels,
      new ConfigDescription(
        "Same flags as BepInEx Logging.Disk / Logging.Console. Debug traces need Debug checked here and in BepInEx.cfg.",
        tags: new object[] { new ConfigurationManagerAttributes { Order = 1, IsAdvanced = true } }));

    CinematicsStore.Init(Sync);
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Skip custom cinematics: {SkipCustom}.");
    string devName = DevResetFirstSpawnName.Value?.Trim() ?? "";
    if (devName.Length > 0)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Dev reset firstSpawn name: '{devName}' (temporary playtest — clear before publish).");
    }

    ModzifiedCinematicsPlugin.LogAt(LogLevel.Info, $"Log levels: {LogLevels.Value}.");
  }

  private static ConfigEntry<T> BindLocal<T>(
    ConfigFile config,
    string section,
    string key,
    T value,
    ConfigDescription description)
  {
    ConfigEntry<T> entry = config.Bind(section, key, value, description);
    Sync.AddConfigEntry(entry).SynchronizedConfig = false;
    return entry;
  }
}

/// <summary>Dummy type Configuration Manager looks for on <see cref="ConfigDescription.Tags"/>.</summary>
internal sealed class ConfigurationManagerAttributes
{
  public int? Order = null;
  public bool? ShowRangeAsPercent = null;
  public bool? IsAdvanced = null;
}
