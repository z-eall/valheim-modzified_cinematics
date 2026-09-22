#pragma warning disable CS0649 // YamlDotNet sets public fields via reflection.

using System.Collections.Generic;

namespace Modzified_Cinematics;

/// <summary>YAML DTO for one cinematic row (camelCase via YamlDotNet).</summary>
internal sealed class CinematicRuleData
{
  /// <summary>Every key a film row accepts; anything else is flagged as ignored.</summary>
  internal static readonly string[] ValidKeys = { "name", "enabled", "type", "dream", "clips", "oneTime", "cooldown" };

  public string? name;
  public bool? enabled;
  /// <summary>Trigger: EWP <c>kind, param1 param2 …</c> (see Wave 2 spec).</summary>
  public string? type;
  /// <summary>True = queue for next sleep; omit/false = play now.</summary>
  public bool? dream;
  public List<string>? clips;
  public bool? oneTime;
  public float? cooldown;
}
