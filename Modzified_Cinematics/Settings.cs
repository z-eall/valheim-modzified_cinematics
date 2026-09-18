using BepInEx.Configuration;
using BepInEx.Logging;
using ServerSync;

namespace Modzified_Cinematics;

internal static class Settings
{
  internal const string SectionLogging = "10. Logging";

  internal const LogLevel DefaultLogLevels =
    LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;

  internal static ConfigSync Sync { get; private set; } = null!;

  internal static ConfigEntry<LogLevel>? LogLevels { get; private set; }

  internal static void Init(ConfigFile config)
  {
    Sync = new ConfigSync(ModzifiedCinematicsPlugin.ModGUID)
    {
      DisplayName = ModzifiedCinematicsPlugin.ModName,
      CurrentVersion = ModzifiedCinematicsPlugin.ModVersion,
      ModRequired = false,
      IsLocked = true
    };

    LogLevels = BindLocal(
      config,
      SectionLogging,
      "Log levels",
      DefaultLogLevels,
      new ConfigDescription(
        "Same flags as BepInEx Logging.Disk / Logging.Console. Cinematic load/replace traces are Debug; they only reach LogOutput.log when Debug is checked here and in BepInEx.cfg.",
        tags: new object[] { new ConfigurationManagerAttributes { Order = 1, IsAdvanced = true } }));

    CinematicsStore.Init(Sync);
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
