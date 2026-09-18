#pragma warning disable CS0649 // YamlDotNet sets public fields via reflection.

using System.Collections.Generic;

namespace Modzified_Cinematics;

/// <summary>YAML DTO for one cinematic row (camelCase via YamlDotNet).</summary>
internal sealed class CinematicRuleData
{
  public string? name;
  public bool? enabled;
  public List<string>? clips;
}
