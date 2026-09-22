#pragma warning disable CS0649 // YamlDotNet sets public fields via reflection.

using System.Collections.Generic;

namespace Modzified_Cinematics;

/// <summary>
/// YAML DTO as written: each key is a list, a single value, or empty. Read only through
/// <see cref="LoadingScreenData"/> (<see cref="LoadingScreensStore"/> normalizes).
/// </summary>
internal sealed class LoadingScreenRaw
{
  /// <summary>Every key the loading-screens file accepts; anything else is flagged as ignored.</summary>
  internal static readonly string[] ValidKeys = { "loadingTips", "loadingArt" };

  public object? loadingTips;
  public object? loadingArt;
}

/// <summary>Normalized <c>modzified_loading_screens.yaml</c> content. Empty list = key omitted = vanilla.</summary>
internal sealed class LoadingScreenData
{
  public List<string> loadingTips = new();
  public List<string> loadingArt = new();

  /// <summary>List, single value, or nothing (empty <c>-</c>, blank entry, key with no <c>-</c>) → clean list.</summary>
  internal static List<string> Normalize(object? raw)
  {
    List<string> list = new();
    switch (raw)
    {
      case null:
        break;
      case string s:
        Add(list, s);
        break;
      case IEnumerable<object> seq:
        foreach (object? item in seq)
        {
          if (item is string text)
          {
            Add(list, text);
          }
        }

        break;
    }

    return list;
  }

  private static void Add(List<string> list, string value)
  {
    string t = value.Trim();
    if (t.Length > 0)
    {
      list.Add(t);
    }
  }
}
