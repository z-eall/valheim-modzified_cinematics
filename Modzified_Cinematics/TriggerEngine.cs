using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Modzified_Cinematics;

/// <summary>
/// Wave 2 custom triggers: EWP <c>type:</c> parse, Play / dream queue, oneTime + cooldown defaults.
/// </summary>
internal static class TriggerEngine
{
  private const string OnceKeyPrefix = "mc_onetime_";
  private const string PendingKeyPrefix = "mc_pending_";

  /// <summary>
  /// Absorbs a near-duplicate re-fire of the same rule within this window, regardless of that rule's
  /// own cooldown/oneTime — e.g. a single EWP <c>say</c> line triggers both vanilla's speech-bubble RPC
  /// and its chat-log RPC, so a <c>type: say</c> entry calls any RPC it targets twice in the same frame.
  /// Confirmed 2026-09-22 against a real playtest log + EWP's HandleRPC.cs (Say and ChatMessage both
  /// route to the same handler). Any external caller could double-fire the same way — this is a floor,
  /// not a substitute for a rule's own `cooldown:`.
  /// </summary>
  private const float MinReplayGapSeconds = 0.5f;

  private static readonly Dictionary<string, float> CooldownUntil =
    new(StringComparer.Ordinal);

  private static readonly Dictionary<string, float> LastPlayedAt =
    new(StringComparer.Ordinal);

  /// <summary>In-memory mirror of this profile's pending (combat-postponed) rule names — lazily loaded from <see cref="Player.GetUniqueKeys"/>.</summary>
  private static readonly List<string> _pendingRuleNames = new();

  private static bool _pendingLoaded;

  private static int _suppressVanillaDream;

  /// <summary>A <c>type: firstSpawn</c> film played this world intro — clip wins over TextViewer intro text.</summary>
  private static bool _firstSpawnFilmTookOver;

  /// <summary>Recent death sites for shared-kill distance on <see cref="Game.RPC_RegisterKill"/> peers.</summary>
  private static readonly List<DeathSite> RecentDeaths = new();

  private struct DeathSite
  {
    internal string EnemyName;
    internal string Prefab;
    internal Vector3 Pos;
    internal float Until;
  }

  internal static bool SuppressVanillaDreamQueue => _suppressVanillaDream > 0;

  /// <summary>Skip TextViewer when a firstSpawn film played.</summary>
  internal static bool FirstSpawnFilmTookOver => _firstSpawnFilmTookOver;

  internal static void ResetFirstSpawnSession() => _firstSpawnFilmTookOver = false;

  internal static void BeginSuppressVanillaDream() => _suppressVanillaDream++;

  internal static void EndSuppressVanillaDream()
  {
    if (_suppressVanillaDream > 0)
    {
      _suppressVanillaDream--;
    }
  }

  internal static void OnCharacterDeath(Character character)
  {
    if (character == null)
    {
      return;
    }

    RememberDeath(character);
    // Shared audience rides Game.RPC_RegisterKill (local + remote attackers) — avoid double-fire here.
    TryFireKill(character, TriggerTypeParse.KillMode.Lasthit);
  }

  internal static void OnRegisterKillCredit(string enemyName)
  {
    if (string.IsNullOrEmpty(enemyName) || Player.m_localPlayer == null)
    {
      return;
    }

    PruneDeaths();
    Vector3 localPos = Player.m_localPlayer.transform.position;

    // Prefer stashed death site (same process as OnDeath); else any matching live/dead body in range.
    if (TryFindDeathSite(enemyName, localPos, out DeathSite site))
    {
      FireMatchingKill(TriggerTypeParse.KillMode.Shared, site.EnemyName, site.Prefab, site.Pos);
      return;
    }

    foreach (Character c in Character.GetAllCharacters())
    {
      if (c == null)
      {
        continue;
      }

      if (!CharacterMatchesFilter(c, enemyName) &&
          !FilterEquals(c.m_name, enemyName))
      {
        continue;
      }

      if (Utils.DistanceXZ(localPos, c.transform.position) > TriggerTypeParse.SharedKillRadius)
      {
        continue;
      }

      string prefab = c.gameObject != null ? Utils.GetPrefabName(c.gameObject) : "";
      FireMatchingKill(TriggerTypeParse.KillMode.Shared, c.m_name, prefab, c.transform.position);
      return;
    }
  }

  internal static bool ShouldReplaceVanillaDream(Character character)
  {
    if (character == null || string.IsNullOrEmpty(character.m_dreamCinematic))
    {
      return false;
    }

    if (!CinematicsStore.TryGetRule(character.m_dreamCinematic, out CinematicsStore.CompiledRule rule))
    {
      return false;
    }

    return rule.Enabled && rule.Parsed != null;
  }

  internal static void OnSleepMoment()
  {
    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.State))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.State != TriggerTypeParse.StateMode.Sleep)
      {
        continue;
      }

      // Sleep event = play now (dream: true would defer to next sleep — pointless).
      Fire(rule, parsed, forceNow: true);
    }
  }

  internal static void OnGlobalKeyFirstSet(string key)
  {
    if (string.IsNullOrEmpty(key))
    {
      return;
    }

    if (key.StartsWith(OnceKeyPrefix, StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.GlobalKey))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed))
      {
        continue;
      }

      if (!FilterEquals(key, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnFirstSpawn()
  {
    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.FirstSpawn))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed))
      {
        continue;
      }

      Fire(rule, parsed, forceNow: true);
    }
  }

  internal static void OnBiomeFirst(BiomeSector sector)
  {
    if (sector == null)
    {
      return;
    }

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Discover))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.Discover != TriggerTypeParse.DiscoverMode.BiomeFirst)
      {
        continue;
      }

      if (!MatchesBiome(sector, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnBiomeEnter(BiomeSector sector)
  {
    if (sector == null)
    {
      return;
    }

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Discover))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.Discover != TriggerTypeParse.DiscoverMode.BiomeEnter)
      {
        continue;
      }

      if (!MatchesBiome(sector, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnLocationEnter(Location location)
  {
    if (location == null)
    {
      return;
    }

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Discover))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.Discover != TriggerTypeParse.DiscoverMode.Location)
      {
        continue;
      }

      if (!MatchesLocation(location, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnRunestoneInteract(RuneStone stone)
  {
    if (stone == null)
    {
      return;
    }

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Interact))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.Interact != TriggerTypeParse.InteractMode.Runestone)
      {
        continue;
      }

      if (!MatchesRunestone(stone, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnBossstoneActivated(BossStone stone)
  {
    if (stone == null || Player.m_localPlayer == null)
    {
      return;
    }

    float dist = Utils.DistanceXZ(Player.m_localPlayer.transform.position, stone.transform.position);
    if (dist > TriggerTypeParse.BossstoneAudienceRadius)
    {
      return;
    }

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Interact))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.Interact != TriggerTypeParse.InteractMode.Bossstone)
      {
        continue;
      }

      if (!MatchesBossstone(stone, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnRandomEvent(string eventName, bool starting)
  {
    if (string.IsNullOrEmpty(eventName))
    {
      return;
    }

    TriggerTypeParse.EventMode want =
      starting ? TriggerTypeParse.EventMode.Start : TriggerTypeParse.EventMode.End;

    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Event))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) || parsed.Event != want)
      {
        continue;
      }

      if (!FilterEquals(eventName, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  internal static void OnPortalTeleport(string? inTag, string? outTag)
  {
    if (!string.IsNullOrEmpty(inTag))
    {
      FireTeleportTag(TriggerTypeParse.TeleportMode.InPortal, inTag!);
    }

    if (!string.IsNullOrEmpty(outTag))
    {
      FireTeleportTag(TriggerTypeParse.TeleportMode.OutPortal, outTag!);
    }
  }

  internal static void OnTeleportTo(Vector3 targetPos)
  {
    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Teleport))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) ||
          parsed.Teleport != TriggerTypeParse.TeleportMode.Pos ||
          parsed.Pos == null)
      {
        continue;
      }

      if (Vector3.Distance(targetPos, parsed.Pos.Value) > TriggerTypeParse.TeleportPosRadius)
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  /// <summary>True if SoftRef intro row has a custom <c>type:</c> (replaces that SoftRef's vanilla when).</summary>
  internal static bool IntroRowReplacesVanillaWhen()
  {
    if (!CinematicsStore.TryGetRule(VanillaCatalog.IntroName, out CinematicsStore.CompiledRule rule))
    {
      return false;
    }

    return rule.Enabled && rule.Parsed != null;
  }

  private static void FireTeleportTag(TriggerTypeParse.TeleportMode mode, string tag)
  {
    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Teleport))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) || parsed.Teleport != mode)
      {
        continue;
      }

      if (!FilterEquals(tag, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  private static void TryFireKill(Character character, TriggerTypeParse.KillMode mode)
  {
    if (Player.m_localPlayer == null || mode != TriggerTypeParse.KillMode.Lasthit)
    {
      return;
    }

    Character? attacker = character.m_lastHit?.GetAttacker();
    if (attacker != Player.m_localPlayer)
    {
      return;
    }

    string prefab = character.gameObject != null ? Utils.GetPrefabName(character.gameObject) : "";
    FireMatchingKill(mode, character.m_name, prefab, character.transform.position);
  }

  private static void FireMatchingKill(
    TriggerTypeParse.KillMode mode,
    string enemyName,
    string prefab,
    Vector3 _)
  {
    foreach (CinematicsStore.CompiledRule rule in RulesOfKind(TriggerTypeParse.Kind.Kill))
    {
      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) || parsed.Kill != mode)
      {
        continue;
      }

      if (!FilterEquals(enemyName, parsed.Filter) && !FilterEquals(prefab, parsed.Filter))
      {
        continue;
      }

      Fire(rule, parsed);
    }
  }

  private static void RememberDeath(Character character)
  {
    PruneDeaths();
    string prefab = character.gameObject != null ? Utils.GetPrefabName(character.gameObject) : "";
    RecentDeaths.Add(new DeathSite
    {
      EnemyName = character.m_name ?? "",
      Prefab = prefab,
      Pos = character.transform.position,
      Until = Time.time + 8f
    });
  }

  private static void PruneDeaths()
  {
    float now = Time.time;
    for (int i = RecentDeaths.Count - 1; i >= 0; i--)
    {
      if (RecentDeaths[i].Until < now)
      {
        RecentDeaths.RemoveAt(i);
      }
    }
  }

  private static bool TryFindDeathSite(string enemyName, Vector3 localPos, out DeathSite site)
  {
    site = default;
    float best = float.MaxValue;
    bool found = false;
    foreach (DeathSite d in RecentDeaths)
    {
      if (!FilterEquals(d.EnemyName, enemyName) && !FilterEquals(d.Prefab, enemyName))
      {
        continue;
      }

      float dist = Utils.DistanceXZ(localPos, d.Pos);
      if (dist > TriggerTypeParse.SharedKillRadius || dist >= best)
      {
        continue;
      }

      best = dist;
      site = d;
      found = true;
    }

    return found;
  }

  private static bool CharacterMatchesFilter(Character character, string filter)
  {
    string prefab = character.gameObject != null ? Utils.GetPrefabName(character.gameObject) : "";
    return FilterEquals(character.m_name, filter) || FilterEquals(prefab, filter);
  }

  private static bool MatchesBiome(BiomeSector sector, string filter)
  {
    Heightmap.Biome biome = sector.Biome;
    if (FilterEquals(biome.ToString(), filter))
    {
      return true;
    }

    string token = "$biome_" + biome.ToString().ToLowerInvariant();
    if (FilterEquals(token, filter))
    {
      return true;
    }

    // Localized display name — weak but author-friendly for alt biomes.
    try
    {
      if (FilterEquals(sector.GetName(), filter))
      {
        return true;
      }
    }
    catch
    {
      // Localization may be unavailable early.
    }

    return false;
  }

  private static bool MatchesLocation(Location location, string filter)
  {
    if (FilterEquals(location.m_discoverLabel, filter))
    {
      return true;
    }

    string prefab = location.gameObject != null ? Utils.GetPrefabName(location.gameObject) : "";
    if (FilterEquals(prefab, filter) || FilterEquals(location.gameObject?.name, filter))
    {
      return true;
    }

    Heightmap.Biome biome = location.m_biome;
    if (biome != Heightmap.Biome.None)
    {
      if (FilterEquals(biome.ToString(), filter))
      {
        return true;
      }

      if (FilterEquals("$biome_" + biome.ToString().ToLowerInvariant(), filter))
      {
        return true;
      }

      // Flag enum: accept if filter names a bit that is set.
      if (Enum.TryParse(filter, ignoreCase: true, out Heightmap.Biome parsed) &&
          parsed != Heightmap.Biome.None &&
          (biome & parsed) != 0)
      {
        return true;
      }
    }

    return false;
  }

  private static bool MatchesRunestone(RuneStone stone, string filter)
  {
    string prefab = stone.gameObject != null ? Utils.GetPrefabName(stone.gameObject) : "";
    if (FilterEquals(prefab, filter) ||
        FilterEquals(stone.gameObject?.name, filter) ||
        FilterEquals(stone.m_name, filter) ||
        FilterEquals(stone.m_topic, filter) ||
        FilterEquals(stone.m_label, filter) ||
        FilterEquals(stone.m_locationName, filter))
    {
      return true;
    }

    return false;
  }

  private static bool MatchesBossstone(BossStone stone, string filter)
  {
    string prefab = stone.gameObject != null ? Utils.GetPrefabName(stone.gameObject) : "";
    if (FilterEquals(prefab, filter) ||
        FilterEquals(stone.gameObject?.name, filter) ||
        FilterEquals(stone.m_setsWorldKey, filter))
    {
      return true;
    }

    if (stone.m_itemStand != null && ObjectDB.instance != null)
    {
      int hash = stone.m_itemStand.GetAttachedItem();
      if (hash != 0)
      {
        GameObject? item = ObjectDB.instance.GetItemPrefab(hash);
        if (item != null)
        {
          string itemPrefab = Utils.GetPrefabName(item);
          if (FilterEquals(itemPrefab, filter) || FilterEquals(item.name, filter))
          {
            return true;
          }

          ItemDrop? drop = item.GetComponent<ItemDrop>();
          if (drop?.m_itemData?.m_shared != null &&
              FilterEquals(drop.m_itemData.m_shared.m_name, filter))
          {
            return true;
          }
        }
      }
    }

    return false;
  }

  private static IEnumerable<CinematicsStore.CompiledRule> RulesOfKind(TriggerTypeParse.Kind kind)
  {
    foreach (KeyValuePair<string, CinematicsStore.CompiledRule> kv in CinematicsStore.AllRules())
    {
      CinematicsStore.CompiledRule rule = kv.Value;
      if (!rule.Enabled || string.IsNullOrEmpty(rule.Type))
      {
        continue;
      }

      if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) || parsed.Kind != kind)
      {
        continue;
      }

      yield return rule;
    }
  }

  private static bool TryGetParsed(CinematicsStore.CompiledRule rule, out TriggerTypeParse.Parsed parsed)
  {
    if (rule.Parsed != null)
    {
      parsed = rule.Parsed.Value;
      return true;
    }

    parsed = default;
    return false;
  }

  private static bool FilterEquals(string? actual, string filter)
  {
    if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(filter))
    {
      return false;
    }

    return string.Equals(actual!.Trim(), filter.Trim(), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Entry point for <c>RPC_PlayModzifiedCinematics</c> — see Patches/RpcPatches.cs.</summary>
  internal static void OnClientRpc(string ruleName)
  {
    if (string.IsNullOrWhiteSpace(ruleName))
    {
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Warning, "Cinematics RPC: empty rule name — ignored.");
      return;
    }

    string trimmed = ruleName.Trim();
    if (!CinematicsStore.TryGetRule(trimmed, out CinematicsStore.CompiledRule rule) || !rule.Enabled)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Cinematics RPC: no enabled rule named '{trimmed}' — ignored.");
      return;
    }

    if (!TryGetParsed(rule, out TriggerTypeParse.Parsed parsed) || parsed.Kind != TriggerTypeParse.Kind.ClientRpc)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Cinematics RPC: '{trimmed}' is not type: clientRpc — ignored.");
      return;
    }

    Fire(rule, parsed);
  }

  private static void Fire(
    CinematicsStore.CompiledRule rule,
    TriggerTypeParse.Parsed parsed,
    bool forceNow = false)
  {
    if (!PassFrequency(rule, parsed))
    {
      return;
    }

    // Combat protection: every kind except dream: true (already vanilla-gated safe via the bed check).
    bool blockedByCombat = !forceNow && !rule.Dream &&
                           Player.m_localPlayer != null && Player.m_localPlayer.IsSensed();

    if (blockedByCombat)
    {
      EnqueuePending(rule.Name);
      return;
    }

    // Debounce floor: absorbs a near-duplicate re-fire of the same rule (e.g. a caller's own quirk
    // invoking the same RPC twice for one action) even when the rule has no cooldown of its own.
    if (LastPlayedAt.TryGetValue(rule.Name, out float lastPlayed) &&
        Time.time - lastPlayed < MinReplayGapSeconds)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics trigger: '{rule.Name}' duplicate call within {MinReplayGapSeconds}s — ignored.");
      return;
    }

    // Safe to resolve now — covers a fresh trigger and, when this rule was already queued,
    // opportunistic early resolution (removing from a queue it was never in is a harmless no-op).
    DequeuePending(rule.Name);
    PlayNow(rule, parsed, forceNow);
  }

  private static void PlayNow(CinematicsStore.CompiledRule rule, TriggerTypeParse.Parsed parsed, bool forceNow)
  {
    LastPlayedAt[rule.Name] = Time.time;

    bool dream = !forceNow && rule.Dream;
    ModzifiedCinematicsPlugin.LogAt(
      LogLevel.Info,
      dream
        ? $"Cinematics trigger: queue dream '{rule.Name}' ({parsed.Canonical})"
        : $"Cinematics trigger: play now '{rule.Name}' ({parsed.Canonical})");

    if (dream)
    {
      // Queued for next sleep, not playing now — no UI to release yet.
      CinematicsManager.SetDreamCinematic(rule.Name);
    }
    else
    {
      // Release any open container/inventory UI first — CinematicsManager.Play() only hides panels by
      // switching their GameObject off directly, never through their own Close(), so a container's
      // "in use" flag is never cleared and gets stuck forever otherwise. Confirmed against
      // CinematicsManager.cs's m_hiders handling and Container.cs's SetInUse/RPC_RequestOpen.
      //
      // Tried and reverted 2026-09-22: registering InventoryGui's GameObject into CinematicsManager's
      // own m_hiders list instead of calling Hide() (to make the container look like it stayed open).
      // That only toggles the panel's GameObject active/inactive — it never calls Container.SetInUse(),
      // since Container is a separate script on the physical chest, not a child of InventoryGui. The
      // "in use" ZDO flag was never released, reproducing this exact stuck-forever bug in the field.
      // Confirmed via a real playtest (chest stuck again with 0 code path calling SetInUse(false)).
      //
      // Deliberately NOT reopened after the video ends: Container.SetInUse() writes straight into the
      // ZDO (ZDOVars.s_inUse, Container.cs's UpdateUseVisual), the same field an EWP `type: change,
      // InUse 1` entry watches — reopening would flip it 0->1 again and could refire that same entry,
      // looping forever with no way to break out. Confirmed 2026-09-22 against a real EWP test script
      // wired exactly that way. Closing (and staying closed) is the only safe option found so far.
      if (InventoryGui.instance != null)
      {
        InventoryGui.instance.Hide();
      }

      if (parsed.Kind == TriggerTypeParse.Kind.FirstSpawn)
      {
        _firstSpawnFilmTookOver = true;
      }

      CinematicsManager.Play(rule.Name);
    }

    // Moved here from Fire()-time: a postponed trigger must not be marked "used" before it plays.
    MarkFired(rule, parsed);
  }

  /// <summary>Rule names currently postponed by combat protection, waiting for a safe-and-home resume.</summary>
  internal static IReadOnlyList<string> PendingRuleNames
  {
    get
    {
      EnsurePendingLoaded();
      return _pendingRuleNames;
    }
  }

  private static void EnsurePendingLoaded()
  {
    if (_pendingLoaded || Player.m_localPlayer == null)
    {
      return;
    }

    _pendingLoaded = true;
    foreach (string key in Player.m_localPlayer.GetUniqueKeys())
    {
      if (!key.StartsWith(PendingKeyPrefix, StringComparison.Ordinal))
      {
        continue;
      }

      string name = key.Substring(PendingKeyPrefix.Length);
      if (!_pendingRuleNames.Contains(name))
      {
        _pendingRuleNames.Add(name);
      }
    }
  }

  /// <summary>Reset the in-memory mirror on a fresh login — reloaded lazily from that profile's own keys.</summary>
  internal static void ResetPendingSession()
  {
    _pendingLoaded = false;
    _pendingRuleNames.Clear();
  }

  private static void EnqueuePending(string ruleName)
  {
    EnsurePendingLoaded();
    if (Player.m_localPlayer == null)
    {
      // No profile to persist against — nothing safe to do but drop it.
      return;
    }

    string key = PendingKeyPrefix + ruleName;
    if (!Player.m_localPlayer.HaveUniqueKey(key))
    {
      Player.m_localPlayer.AddUniqueKey(key);
    }

    if (!_pendingRuleNames.Contains(ruleName))
    {
      _pendingRuleNames.Add(ruleName);
    }

    ModzifiedCinematicsPlugin.LogAt(
      LogLevel.Info,
      $"Cinematics trigger: '{ruleName}' postponed — combat detected, queued for later.");
  }

  private static void DequeuePending(string ruleName)
  {
    if (Player.m_localPlayer != null)
    {
      string key = PendingKeyPrefix + ruleName;
      if (Player.m_localPlayer.HaveUniqueKey(key))
      {
        Player.m_localPlayer.RemoveUniqueKey(key);
      }
    }

    _pendingRuleNames.Remove(ruleName);
  }

  private static bool _resumePromptActive;

  /// <summary>
  /// Resume/reactivation check — call from the mod's existing ~1 Hz poll (TriggerPatches.UpdateBiomePatch).
  /// One prompt at a time, oldest queued rule first; the next one only shows once this one is answered
  /// (Yes or No) — never while a video is actually playing.
  /// </summary>
  internal static void OnResumeCheckTick()
  {
    EnsurePendingLoaded();
    if (_pendingRuleNames.Count == 0 || _resumePromptActive)
    {
      return;
    }

    if (Player.m_localPlayer == null ||
        CinematicsManager.IsPlaying() ||
        CinematicsManager.IsStartedPlaying())
    {
      return;
    }

    if (Player.m_localPlayer.IsSensed())
    {
      return;
    }

    if (EffectArea.IsPointInsideArea(Player.m_localPlayer.transform.position, EffectArea.Type.PlayerBase) == null)
    {
      return;
    }

    ShowResumePrompt(_pendingRuleNames[0]);
  }

  private static void ShowResumePrompt(string ruleName)
  {
    _resumePromptActive = true;
    int count = _pendingRuleNames.Count;
    string plural = count == 1 ? "" : "s";
    string text = $"You have {count} pending video{plural}, watch '{ruleName}' now?";

    UnifiedPopup.Push(new YesNoPopup(
      "Cinematics",
      text,
      delegate
      {
        _resumePromptActive = false;
        UnifiedPopup.Pop();
        ResolvePendingPlay(ruleName);
      },
      delegate
      {
        _resumePromptActive = false;
        UnifiedPopup.Pop();
        DequeuePending(ruleName);
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Info,
          $"Cinematics trigger: '{ruleName}' dismissed from queue (No).");
      },
      localizeText: false));
  }

  private static void ResolvePendingPlay(string ruleName)
  {
    DequeuePending(ruleName);
    if (CinematicsStore.TryGetRule(ruleName, out CinematicsStore.CompiledRule rule) &&
        TryGetParsed(rule, out TriggerTypeParse.Parsed parsed))
    {
      PlayNow(rule, parsed, forceNow: false);
    }
  }

  private static bool PassFrequency(CinematicsStore.CompiledRule rule, TriggerTypeParse.Parsed parsed)
  {
    float? cooldown = rule.Cooldown;
    if (cooldown == null && parsed.HasDefaultCooldown)
    {
      cooldown = TriggerTypeParse.DefaultCooldownSeconds;
    }

    if (cooldown is > 0f &&
        CooldownUntil.TryGetValue(rule.Name, out float until) &&
        Time.time < until)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics trigger: '{rule.Name}' on cooldown.");
      return false;
    }

    TriggerTypeParse.OneTimeScope? scope = rule.OneTime ?? parsed.DefaultOneTimeScope;
    if (scope == null)
    {
      return true;
    }

    string key = OnceKeyPrefix + Sanitize(rule.Name);

    if (scope == TriggerTypeParse.OneTimeScope.Player)
    {
      if (Player.m_localPlayer != null && Player.m_localPlayer.HaveUniqueKey(key))
      {
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Debug,
          $"Cinematics trigger: '{rule.Name}' oneTime already (player).");
        return false;
      }

      return true;
    }

    // World scope, falling back to the player store when there's no ZoneSystem (e.g. main menu preview).
    if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key))
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics trigger: '{rule.Name}' oneTime already (world).");
      return false;
    }

    if (ZoneSystem.instance == null &&
        Player.m_localPlayer != null &&
        Player.m_localPlayer.HaveUniqueKey(key))
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics trigger: '{rule.Name}' oneTime already (player fallback).");
      return false;
    }

    return true;
  }

  private static void MarkFired(CinematicsStore.CompiledRule rule, TriggerTypeParse.Parsed parsed)
  {
    float? cooldown = rule.Cooldown;
    if (cooldown == null && parsed.HasDefaultCooldown)
    {
      cooldown = TriggerTypeParse.DefaultCooldownSeconds;
    }

    if (cooldown is > 0f)
    {
      CooldownUntil[rule.Name] = Time.time + cooldown.Value;
    }

    TriggerTypeParse.OneTimeScope? scope = rule.OneTime ?? parsed.DefaultOneTimeScope;
    if (scope == null)
    {
      return;
    }

    string key = OnceKeyPrefix + Sanitize(rule.Name);

    if (scope == TriggerTypeParse.OneTimeScope.Player)
    {
      if (Player.m_localPlayer != null && !Player.m_localPlayer.HaveUniqueKey(key))
      {
        Player.m_localPlayer.AddUniqueKey(key);
      }

      return;
    }

    if (ZoneSystem.instance != null && !ZoneSystem.instance.GetGlobalKey(key))
    {
      ZoneSystem.instance.SetGlobalKey(key);
      return;
    }

    if (ZoneSystem.instance == null &&
        Player.m_localPlayer != null &&
        !Player.m_localPlayer.HaveUniqueKey(key))
    {
      Player.m_localPlayer.AddUniqueKey(key);
    }
  }

  private static string Sanitize(string name)
  {
    char[] chars = name.ToCharArray();
    for (int i = 0; i < chars.Length; i++)
    {
      char c = chars[i];
      if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
      {
        chars[i] = '_';
      }
    }

    return new string(chars);
  }
}
