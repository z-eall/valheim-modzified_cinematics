using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.Rendering;

namespace Modzified_Cinematics;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class ModzifiedCinematicsPlugin : BaseUnityPlugin
{
  internal const string ModName = "Modzified_Cinematics";
  internal const string ModVersion = "0.1.9";
  /// <summary>Jere-style snake_case GUID. Thunderstore author when published: Zeall.</summary>
  internal const string ModGUID = "modzified_cinematics";

  internal static ModzifiedCinematicsPlugin Instance { get; private set; } = null!;
  internal static ManualLogSource Log { get; private set; } = null!;
  internal static bool IsHeadless { get; private set; }

  internal static bool Allows(LogLevel level)
  {
    return Settings.LogLevels == null || (Settings.LogLevels.Value & level) != LogLevel.None;
  }

  internal static void LogAt(LogLevel level, string message)
  {
    if (!Allows(level))
    {
      return;
    }

    Log.Log(level, message);
  }

  private readonly Harmony _harmony = new(ModGUID);

  private void Awake()
  {
    Instance = this;
    Log = Logger;
    IsHeadless = UnityEngine.SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    Settings.Init(Config);

    try
    {
      // Per-type: one dead target must not abort the rest (0.1.0 PatchAll died on PlayIntroCinematic).
      // Do not skip IsAbstract — C# `static class` is abstract+sealed; that filter skipped every patch in 0.1.2.
      foreach (System.Type type in Assembly.GetExecutingAssembly().GetTypes())
      {
        if (!type.IsClass)
        {
          continue;
        }

        if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: true).Length == 0)
        {
          continue;
        }

        try
        {
          _harmony.CreateClassProcessor(type).Patch();
        }
        catch (System.Exception ex)
        {
          Log.LogError($"Harmony patch failed for {type.FullName}: {ex.Message}");
        }
      }
    }
    catch (System.Exception ex)
    {
      Log.LogError($"Harmony patching failed: {ex}");
    }

    if (IsHeadless)
    {
      LogAt(LogLevel.Info, $"{ModName} v{ModVersion} loaded on dedicated/headless (GUID {ModGUID}).");
    }
    else
    {
      LogAt(LogLevel.Info, $"{ModName} v{ModVersion} loaded (GUID {ModGUID}).");
    }
  }

  private void Update()
  {
    CinematicsStore.Tick();
  }

  private void OnDestroy()
  {
    _harmony.UnpatchSelf();
  }
}
