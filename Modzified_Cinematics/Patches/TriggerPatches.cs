using System;
using HarmonyLib;
using UnityEngine;

namespace Modzified_Cinematics.Patches;

/// <summary>Wave 2 trigger seams: kill, state/sleep, keys, spawn, discover, interact, event, teleport.</summary>
internal static class TriggerPatches
{
  private static bool _firstSpawnHandled;
  private static Location? _lastLocation;
  private static bool _biomeEnterPrimed;
  private static Heightmap.Biome _lastBiome = Heightmap.Biome.None;

  [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
  private static class CharacterOnDeathPatch
  {
    [HarmonyPrefix]
    private static void Prefix(Character __instance, out bool __state)
    {
      TriggerEngine.OnCharacterDeath(__instance);
      __state = TriggerEngine.ShouldReplaceVanillaDream(__instance);
      if (__state)
      {
        TriggerEngine.BeginSuppressVanillaDream();
      }
    }

    [HarmonyFinalizer]
    private static void Finalizer(bool __state)
    {
      if (__state)
      {
        TriggerEngine.EndSuppressVanillaDream();
      }
    }
  }

  [HarmonyPatch(typeof(Game), nameof(Game.RPC_RegisterKill))]
  private static class RegisterKillPatch
  {
    [HarmonyPostfix]
    private static void Postfix(string enemyName)
    {
      TriggerEngine.OnRegisterKillCredit(enemyName);
    }
  }

  [HarmonyPatch(typeof(CinematicsManager), nameof(CinematicsManager.SetDreamCinematic))]
  private static class SetDreamCinematicPatch
  {
    [HarmonyPrefix]
    private static bool Prefix(string name)
    {
      if (!TriggerEngine.SuppressVanillaDreamQueue)
      {
        return true;
      }

      ModzifiedCinematicsPlugin.LogAt(
        BepInEx.Logging.LogLevel.Debug,
        $"Cinematics trigger: suppress vanilla dream queue '{name}' (replaced by type:).");
      return false;
    }
  }

  [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
  private static class GlobalKeyPatch
  {
    [HarmonyPrefix]
    private static void Prefix(ZoneSystem __instance, string name, out bool __state)
    {
      __state = __instance != null && !string.IsNullOrEmpty(name) && !__instance.m_globalKeys.Contains(name);
    }

    [HarmonyPostfix]
    private static void Postfix(string name, bool __state)
    {
      if (__state)
      {
        TriggerEngine.OnGlobalKeyFirstSet(name);
      }
    }
  }

  [HarmonyPatch(typeof(CinematicsManager), nameof(CinematicsManager.Play), typeof(CinematicsManager.Settings), typeof(CinematicsManager.VideoCompleteAction))]
  private static class PlayIntroSettingsPatch
  {
    [HarmonyPrefix]
    private static bool Prefix(CinematicsManager.Settings firstWithSetting, ref bool __result)
    {
      if (firstWithSetting != CinematicsManager.Settings.Intro)
      {
        return true;
      }

      if (!IsWorldFirstSpawnIntro())
      {
        return true;
      }

      if (!_firstSpawnHandled)
      {
        _firstSpawnHandled = true;
        TriggerEngine.OnFirstSpawn();
      }

      if (TriggerEngine.IntroRowReplacesVanillaWhen())
      {
        __result = true;
        return false;
      }

      return true;
    }
  }

  [HarmonyPatch(typeof(Game), nameof(Game.ShowIntro))]
  private static class ShowIntroPatch
  {
    [HarmonyPrefix]
    private static bool Prefix()
    {
      if (!IsWorldFirstSpawnIntro())
      {
        return true;
      }

      if (!_firstSpawnHandled)
      {
        _firstSpawnHandled = true;
        TriggerEngine.OnFirstSpawn();
      }

      if (TriggerEngine.FirstSpawnFilmTookOver)
      {
        return false;
      }

      return true;
    }
  }

  [HarmonyPatch(typeof(Game), "Start")]
  private static class GameStartPatch
  {
    [HarmonyPrefix]
    private static void Prefix(Game __instance)
    {
      TryDevResetFirstSpawn(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
      _firstSpawnHandled = false;
      _lastLocation = null;
      _biomeEnterPrimed = false;
      _lastBiome = Heightmap.Biome.None;
      TriggerEngine.ResetFirstSpawnSession();
    }
  }

  [HarmonyPatch(typeof(Player), "AddKnownBiome")]
  private static class AddKnownBiomePatch
  {
    [HarmonyPrefix]
    private static void Prefix(Player __instance, BiomeSector biome, out bool __state)
    {
      __state = __instance == Player.m_localPlayer && biome != null && !__instance.IsBiomeKnown(biome);
    }

    [HarmonyPostfix]
    private static void Postfix(BiomeSector biome, bool __state)
    {
      if (__state)
      {
        TriggerEngine.OnBiomeFirst(biome);
      }
    }
  }

  [HarmonyPatch(typeof(Player), "UpdateBiome")]
  private static class UpdateBiomePatch
  {
    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
      if (__instance != Player.m_localPlayer)
      {
        return;
      }

      BiomeSector? sector = __instance.GetCurrentBiomeData();
      Heightmap.Biome biome = __instance.GetCurrentBiome();

      if (!_biomeEnterPrimed)
      {
        _biomeEnterPrimed = true;
        _lastBiome = biome;
      }
      else if (biome != _lastBiome)
      {
        _lastBiome = biome;
        if (sector != null)
        {
          TriggerEngine.OnBiomeEnter(sector);
        }
      }

      // Location enter: volume = m_exteriorRadius (GetLocation already radius-tests).
      Location? loc = Location.GetLocation(__instance.transform.position, checkDungeons: false);
      if (!ReferenceEquals(loc, _lastLocation))
      {
        _lastLocation = loc;
        if (loc != null)
        {
          float radius = loc.m_exteriorRadius > 0f
            ? loc.m_exteriorRadius
            : TriggerTypeParse.LocationDefaultRadius;
          if (Utils.DistanceXZ(__instance.transform.position, loc.transform.position) <= radius)
          {
            TriggerEngine.OnLocationEnter(loc);
          }
        }
      }
    }
  }

  [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.Interact))]
  private static class RuneStoneInteractPatch
  {
    [HarmonyPostfix]
    private static void Postfix(RuneStone __instance, Humanoid character, bool hold)
    {
      if (hold || character != Player.m_localPlayer)
      {
        return;
      }

      TriggerEngine.OnRunestoneInteract(__instance);
    }
  }

  [HarmonyPatch(typeof(BossStone), "SetActivated")]
  private static class BossStoneActivatedPatch
  {
    [HarmonyPrefix]
    private static void Prefix(BossStone __instance, bool active, out bool __state)
    {
      // Fire only on false→true (trophy hang). Read private m_active via Traverse.
      bool wasActive = Traverse.Create(__instance).Field<bool>("m_active").Value;
      __state = active && !wasActive;
    }

    [HarmonyPostfix]
    private static void Postfix(BossStone __instance, bool __state)
    {
      if (__state)
      {
        TriggerEngine.OnBossstoneActivated(__instance);
      }
    }
  }

  [HarmonyPatch(typeof(RandEventSystem), "SetActiveEvent")]
  private static class RandomEventPatch
  {
    [HarmonyPrefix]
    private static void Prefix(RandEventSystem __instance, RandomEvent ev, out string? __state)
    {
      __state = null;
      RandomEvent? current = Traverse.Create(__instance).Field<RandomEvent>("m_activeEvent").Value;
      if (ev != null && current != null && ev.m_name == current.m_name)
      {
        return;
      }

      // Vanilla may end one event and start another in the same call.
      string endName = current != null ? current.m_name : "";
      string startName = ev != null ? ev.m_name : "";
      if (endName.Length == 0 && startName.Length == 0)
      {
        return;
      }

      __state = endName + "\n" + startName;
    }

    [HarmonyPostfix]
    private static void Postfix(string? __state)
    {
      if (string.IsNullOrEmpty(__state))
      {
        return;
      }

      string[] parts = __state!.Split('\n');
      string endName = parts.Length > 0 ? parts[0] : "";
      string startName = parts.Length > 1 ? parts[1] : "";
      if (endName.Length > 0)
      {
        TriggerEngine.OnRandomEvent(endName, starting: false);
      }

      if (startName.Length > 0)
      {
        TriggerEngine.OnRandomEvent(startName, starting: true);
      }
    }
  }

  [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport))]
  private static class PortalTeleportPatch
  {
    [HarmonyPostfix]
    private static void Postfix(TeleportWorld __instance, Player player)
    {
      if (player != Player.m_localPlayer || __instance == null || !player.IsTeleporting())
      {
        return;
      }

      string inTag = "";
      string outTag = "";
      ZNetView? nv = Traverse.Create(__instance).Field<ZNetView>("m_nview").Value;
      if (nv != null && nv.IsValid())
      {
        inTag = nv.GetZDO().GetString(ZDOVars.s_tag) ?? "";
        ZDOID connected = nv.GetZDO().GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
        ZDO? dest = ZDOMan.instance?.GetZDO(connected);
        if (dest != null)
        {
          outTag = dest.GetString(ZDOVars.s_tag) ?? "";
        }
      }

      TriggerEngine.OnPortalTeleport(inTag, outTag);
    }
  }

  [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
  private static class TeleportToPatch
  {
    [HarmonyPostfix]
    private static void Postfix(Player __instance, Vector3 pos, bool __result)
    {
      if (!__result || __instance != Player.m_localPlayer)
      {
        return;
      }

      TriggerEngine.OnTeleportTo(pos);
    }
  }

  private static void TryDevResetFirstSpawn(Game game)
  {
    string want = Settings.DevResetFirstSpawnName?.Value?.Trim() ?? "";
    if (want.Length == 0 || game?.m_playerProfile == null)
    {
      return;
    }

    PlayerProfile profile = game.m_playerProfile;
    if (!string.Equals(profile.GetName(), want, StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    if (profile.m_firstSpawn)
    {
      ModzifiedCinematicsPlugin.LogAt(
        BepInEx.Logging.LogLevel.Debug,
        $"Dev reset firstSpawn: '{want}' already firstSpawn.");
      return;
    }

    profile.m_firstSpawn = true;
    ModzifiedCinematicsPlugin.LogAt(
      BepInEx.Logging.LogLevel.Warning,
      $"Dev reset firstSpawn: forced true for '{want}' (temporary playtest).");
  }

  private static bool IsWorldFirstSpawnIntro()
  {
    return Game.instance != null && Game.instance.m_inIntro;
  }
}
