using System;
using System.Globalization;

namespace Modzified_Cinematics;

/// <summary>
/// EWP-shaped <c>type:</c> grammar: <c>kind, param1 param2 …</c> — one comma after the kind;
/// parameters space-separated; commas inside a parameter are multi-value / coord lists.
/// Mentors: EWP <c>InfoType</c> / scripting.md (study, don’t paste).
/// </summary>
internal static class TriggerTypeParse
{
  internal const float DefaultCooldownSeconds = 600f;
  internal const float SharedKillRadius = 60f;
  internal const float BossstoneAudienceRadius = 20f;
  internal const float TeleportPosRadius = 3f;
  internal const float LocationDefaultRadius = 20f;

  internal enum Kind
  {
    Kill,
    State,
    GlobalKey,
    FirstSpawn,
    Discover,
    Interact,
    Event,
    Teleport,
    ClientRpc
  }

  /// <summary><c>oneTime: player</c> tracks per-player (<see cref="Player.AddUniqueKey"/>); <c>oneTime: world</c> tracks server-wide (<see cref="ZoneSystem.SetGlobalKey"/>).</summary>
  internal enum OneTimeScope
  {
    Player,
    World
  }

  internal enum KillMode
  {
    Lasthit,
    Shared
  }

  internal enum StateMode
  {
    Sleep
  }

  internal enum DiscoverMode
  {
    BiomeFirst,
    BiomeEnter,
    Location
  }

  internal enum InteractMode
  {
    Runestone,
    Bossstone
  }

  internal enum EventMode
  {
    Start,
    End
  }

  internal enum TeleportMode
  {
    InPortal,
    OutPortal,
    Pos
  }

  internal readonly struct Parsed
  {
    internal Parsed(
      Kind kind,
      string canonical,
      string filter,
      KillMode? kill = null,
      StateMode? state = null,
      DiscoverMode? discover = null,
      InteractMode? interact = null,
      EventMode? eventMode = null,
      TeleportMode? teleport = null,
      UnityEngine.Vector3? pos = null)
    {
      Kind = kind;
      Canonical = canonical;
      Filter = filter;
      Kill = kill;
      State = state;
      Discover = discover;
      Interact = interact;
      Event = eventMode;
      Teleport = teleport;
      Pos = pos;
    }

    internal Kind Kind { get; }
    /// <summary>Canonical spelling for logs / errors.</summary>
    internal string Canonical { get; }
    internal string Filter { get; }
    internal KillMode? Kill { get; }
    internal StateMode? State { get; }
    internal DiscoverMode? Discover { get; }
    internal InteractMode? Interact { get; }
    internal EventMode? Event { get; }
    internal TeleportMode? Teleport { get; }
    internal UnityEngine.Vector3? Pos { get; }

    /// <summary>Null = no default scope (not one-time unless the rule says so). bossstone carries over its real shipped default: world.</summary>
    internal OneTimeScope? DefaultOneTimeScope =>
      Kind == Kind.Interact && Interact == InteractMode.Bossstone ? OneTimeScope.World : (OneTimeScope?)null;

    internal bool HasDefaultCooldown =>
      (Kind == Kind.Discover && Discover is DiscoverMode.BiomeEnter or DiscoverMode.Location) ||
      (Kind == Kind.Interact && Interact == InteractMode.Runestone) ||
      Kind == Kind.Teleport;

    internal string FrequencyKey => Canonical;
  }

  internal static bool TryParse(string? raw, out Parsed parsed, out string error)
  {
    parsed = default;
    error = "";
    if (string.IsNullOrWhiteSpace(raw))
    {
      error = "empty type";
      return false;
    }

    string trimmed = raw!.Trim();

    int comma = trimmed.IndexOf(',');
    string kindRaw;
    string paramRaw;
    if (comma < 0)
    {
      kindRaw = trimmed;
      paramRaw = "";
    }
    else
    {
      kindRaw = trimmed.Substring(0, comma).Trim();
      paramRaw = trimmed.Substring(comma + 1).Trim();
    }

    string[] parts = string.IsNullOrEmpty(paramRaw)
      ? Array.Empty<string>()
      : paramRaw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

    if (Eq(kindRaw, "clientRpc"))
    {
      if (parts.Length != 0)
      {
        error = "clientRpc takes no parameters — the RPC names this rule by its own name field";
        return false;
      }

      parsed = new Parsed(Kind.ClientRpc, "clientRpc", "");
      return true;
    }

    if (Eq(kindRaw, "firstSpawn"))
    {
      if (parts.Length != 0)
      {
        error = "firstSpawn takes no parameters";
        return false;
      }

      parsed = new Parsed(Kind.FirstSpawn, "firstSpawn", "");
      return true;
    }

    if (Eq(kindRaw, "globalKey"))
    {
      if (parts.Length != 1)
      {
        error = "globalKey requires exactly one key (globalKey, <key>)";
        return false;
      }

      parsed = new Parsed(Kind.GlobalKey, "globalKey, " + parts[0], parts[0]);
      return true;
    }

    if (Eq(kindRaw, "kill"))
    {
      if (parts.Length != 2)
      {
        error = "kill requires mode and prefab (kill, lasthit|shared <prefab>)";
        return false;
      }

      KillMode mode;
      string modeCanon;
      if (Eq(parts[0], "lasthit"))
      {
        mode = KillMode.Lasthit;
        modeCanon = "lasthit";
      }
      else if (Eq(parts[0], "shared"))
      {
        mode = KillMode.Shared;
        modeCanon = "shared";
      }
      else
      {
        error = "kill mode must be lasthit or shared";
        return false;
      }

      parsed = new Parsed(
        Kind.Kill,
        "kill, " + modeCanon + " " + parts[1],
        parts[1],
        kill: mode);
      return true;
    }

    if (Eq(kindRaw, "state"))
    {
      if (parts.Length != 1)
      {
        error = "state requires one mode (state, sleep)";
        return false;
      }

      if (Eq(parts[0], "death"))
      {
        error = "state, death is not shipped — parked";
        return false;
      }

      if (!Eq(parts[0], "sleep"))
      {
        error = "state mode must be sleep";
        return false;
      }

      parsed = new Parsed(Kind.State, "state, sleep", "", state: StateMode.Sleep);
      return true;
    }

    if (Eq(kindRaw, "discover"))
    {
      if (parts.Length != 2)
      {
        error = "discover requires mode and filter (discover, biomeFirst|biomeEnter|location <filter>)";
        return false;
      }

      DiscoverMode mode;
      string modeCanon;
      if (Eq(parts[0], "biomeFirst"))
      {
        mode = DiscoverMode.BiomeFirst;
        modeCanon = "biomeFirst";
      }
      else if (Eq(parts[0], "biomeEnter"))
      {
        mode = DiscoverMode.BiomeEnter;
        modeCanon = "biomeEnter";
      }
      else if (Eq(parts[0], "location"))
      {
        mode = DiscoverMode.Location;
        modeCanon = "location";
      }
      else
      {
        error = "discover mode must be biomeFirst, biomeEnter, or location";
        return false;
      }

      parsed = new Parsed(
        Kind.Discover,
        "discover, " + modeCanon + " " + parts[1],
        parts[1],
        discover: mode);
      return true;
    }

    if (Eq(kindRaw, "interact"))
    {
      if (parts.Length != 2)
      {
        error = "interact requires mode and filter (interact, runestone|bossstone <filter>)";
        return false;
      }

      InteractMode mode;
      string modeCanon;
      if (Eq(parts[0], "runestone"))
      {
        mode = InteractMode.Runestone;
        modeCanon = "runestone";
      }
      else if (Eq(parts[0], "bossstone"))
      {
        mode = InteractMode.Bossstone;
        modeCanon = "bossstone";
      }
      else
      {
        error = "interact mode must be runestone or bossstone";
        return false;
      }

      parsed = new Parsed(
        Kind.Interact,
        "interact, " + modeCanon + " " + parts[1],
        parts[1],
        interact: mode);
      return true;
    }

    if (Eq(kindRaw, "event"))
    {
      if (parts.Length != 2)
      {
        error = "event requires mode and name (event, start|end <name>)";
        return false;
      }

      EventMode mode;
      string modeCanon;
      if (Eq(parts[0], "start"))
      {
        mode = EventMode.Start;
        modeCanon = "start";
      }
      else if (Eq(parts[0], "end"))
      {
        mode = EventMode.End;
        modeCanon = "end";
      }
      else
      {
        error = "event mode must be start or end";
        return false;
      }

      parsed = new Parsed(
        Kind.Event,
        "event, " + modeCanon + " " + parts[1],
        parts[1],
        eventMode: mode);
      return true;
    }

    if (Eq(kindRaw, "teleport"))
    {
      if (parts.Length != 2)
      {
        error = "teleport requires mode and value (teleport, inPortal|outPortal|pos <value>)";
        return false;
      }

      if (Eq(parts[0], "inPortal"))
      {
        parsed = new Parsed(
          Kind.Teleport,
          "teleport, inPortal " + parts[1],
          parts[1],
          teleport: TeleportMode.InPortal);
        return true;
      }

      if (Eq(parts[0], "outPortal"))
      {
        parsed = new Parsed(
          Kind.Teleport,
          "teleport, outPortal " + parts[1],
          parts[1],
          teleport: TeleportMode.OutPortal);
        return true;
      }

      if (Eq(parts[0], "pos"))
      {
        if (!TryParsePos(parts[1], out UnityEngine.Vector3 pos))
        {
          error = "teleport pos needs x,z,y (teleport, pos 100,30,-200)";
          return false;
        }

        parsed = new Parsed(
          Kind.Teleport,
          "teleport, pos " + parts[1],
          parts[1],
          teleport: TeleportMode.Pos,
          pos: pos);
        return true;
      }

      error = "teleport mode must be inPortal, outPortal, or pos";
      return false;
    }

    error = "unknown type kind '" + kindRaw + "'";
    return false;
  }

  /// <summary>Unrecognized token: warn, treat as omitted (matches how an invalid <c>type:</c> token is handled).</summary>
  internal static bool TryParseOneTimeScope(string? raw, out OneTimeScope? scope, out string error)
  {
    scope = null;
    error = "";
    if (string.IsNullOrWhiteSpace(raw))
    {
      return true;
    }

    string trimmed = raw!.Trim();
    if (Eq(trimmed, "player"))
    {
      scope = OneTimeScope.Player;
      return true;
    }

    if (Eq(trimmed, "world"))
    {
      scope = OneTimeScope.World;
      return true;
    }

    error = "oneTime must be player or world (got '" + trimmed + "')";
    return false;
  }

  /// <summary>Spec order: x,z,y (Valheim XZ ground + Y height).</summary>
  internal static bool TryParsePos(string raw, out UnityEngine.Vector3 pos)
  {
    pos = default;
    string[] bits = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    if (bits.Length != 3)
    {
      return false;
    }

    if (!float.TryParse(bits[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
        !float.TryParse(bits[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
        !float.TryParse(bits[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
    {
      return false;
    }

    pos = new UnityEngine.Vector3(x, y, z);
    return true;
  }

  private static bool Eq(string a, string b) =>
    string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

  private static bool StartsWithKind(string raw, string kind)
  {
    if (!raw.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    if (raw.Length == kind.Length)
    {
      return true;
    }

    char next = raw[kind.Length];
    return next == ',' || char.IsWhiteSpace(next);
  }

  private static bool IsBareOrKind(string raw, string kind)
  {
    if (Eq(raw, kind))
    {
      return true;
    }

    return StartsWithKind(raw, kind);
  }
}
