using System;
using System.Collections.Generic;
using System.Text;
using YamlDotNet.Serialization;

namespace Modzified_Cinematics;

/// <summary>
/// Comment-blind view of YAML text (AGENTS.md, Mod config text scans): every raw-text
/// check reads this, never the file as written, so a commented example cannot raise a warning.
/// </summary>
internal static class YamlText
{
  /// <summary>Drop <c>#</c> lines and <c>#</c> tails (outside quotes). Line count is kept.</summary>
  internal static string StripComments(string text)
  {
    if (string.IsNullOrEmpty(text))
    {
      return "";
    }

    StringBuilder sb = new(text.Length);
    string[] lines = text.Replace("\r\n", "\n").Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
      sb.Append(StripLine(lines[i]));
      if (i < lines.Length - 1)
      {
        sb.Append('\n');
      }
    }

    return sb.ToString();
  }

  private static string StripLine(string line)
  {
    bool single = false;
    bool dbl = false;
    for (int i = 0; i < line.Length; i++)
    {
      char c = line[i];
      if (c == '\'' && !dbl)
      {
        single = !single;
      }
      else if (c == '"' && !single)
      {
        dbl = !dbl;
      }
      else if (c == '#' && !single && !dbl && (i == 0 || char.IsWhiteSpace(line[i - 1])))
      {
        return line.Substring(0, i).TrimEnd();
      }
    }

    return line;
  }

  /// <summary>
  /// Flag every key the current YAML system does not accept: it is ignored and does nothing (a typo, a wrong case,
  /// or a key from an older version). Reads the parsed document, so comments never count. Warns once per reload.
  /// Real syntax errors are reported by the loader, not here.
  /// </summary>
  internal static void WarnUnknownKeys(string text, string source, IReadOnlyCollection<string> valid, bool expectList, string rowLabelKey)
  {
    try
    {
      object? root = new DeserializerBuilder().Build().Deserialize<object>(text);
      if (root == null)
      {
        return;
      }

      if (expectList)
      {
        if (root is not List<object> rows)
        {
          ModzifiedCinematicsPlugin.LogWarnOnce(
            $"{source}: expected a list of rows, each starting with '- {rowLabelKey}: …'. Nothing in this file will load.");
          return;
        }

        int index = 0;
        foreach (object? row in rows)
        {
          index++;
          if (row is not Dictionary<object, object> map)
          {
            ModzifiedCinematicsPlugin.LogWarnOnce($"{source}: entry {index} is not a row (start it with '- {rowLabelKey}: …'). Ignored.");
            continue;
          }

          string label = map.TryGetValue(rowLabelKey, out object? n) && n is string s && s.Length > 0
            ? $"row '{s}'"
            : $"row {index}";
          CheckKeys(map, valid, source, label);
        }

        return;
      }

      if (root is not Dictionary<object, object> top)
      {
        ModzifiedCinematicsPlugin.LogWarnOnce($"{source}: expected 'key:' lines at the top level. Nothing in this file will load.");
        return;
      }

      CheckKeys(top, valid, source, "the top level");
    }
    catch
    {
      // Malformed YAML: the loader logs the parse error.
    }
  }

  private static void CheckKeys(Dictionary<object, object> map, IReadOnlyCollection<string> valid, string source, string where)
  {
    foreach (object key in map.Keys)
    {
      string name = Convert.ToString(key) ?? "";
      if (Contains(valid, name, StringComparison.Ordinal))
      {
        continue;
      }

      string hint = "";
      foreach (string v in valid)
      {
        if (string.Equals(v, name, StringComparison.OrdinalIgnoreCase))
        {
          hint = $" Did you mean '{v}'?";
        }
      }

      ModzifiedCinematicsPlugin.LogWarnOnce(
        $"{source}: unknown key '{name}' in {where} — ignored, it does nothing. Valid keys: {string.Join(", ", valid)}.{hint}");
    }
  }

  private static bool Contains(IReadOnlyCollection<string> valid, string name, StringComparison comparison)
  {
    foreach (string v in valid)
    {
      if (string.Equals(v, name, comparison))
      {
        return true;
      }
    }

    return false;
  }
}
