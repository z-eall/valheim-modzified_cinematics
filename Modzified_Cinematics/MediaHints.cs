namespace Modzified_Cinematics;

/// <summary>
/// Player-facing allow-list for replace media (Unity VideoPlayer / Windows Media Foundation).
/// </summary>
internal static class MediaHints
{
  internal const string AllowListShort = ".mp4 + H.264 video + AAC audio";

  /// <summary>Appended to playback / silent-audio warnings in the BepInEx log.</summary>
  internal const string LogFixHint =
    "Re-export as .mp4 with H.264 video and AAC audio. AV1 and many other codecs fail or play silent.";
}
