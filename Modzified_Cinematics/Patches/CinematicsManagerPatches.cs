using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;

namespace Modzified_Cinematics.Patches;

[HarmonyPatch(typeof(CinematicsManager))]
internal static class CinematicsManagerPatches
{
  private const string ReplaceAudioChild = "ModzifiedCinematicsAudio";

  private static VideoPlayer.EventHandler? _pendingPrepared;
  private static string? _warmUrl;
  private static bool _warmReady;
  private static bool _awaitingReveal;
  private static bool _skipReplaceOnce;
  private static CinematicsManager.VideoEntry? _activeReplaceVideo;
  private static CinematicsManager.VideoCompleteAction? _activeReplaceOnStop;
  private static string? _logoHoldIntroAbs;

  /// <summary>Fejd logo-hold locks the intro file so Play promotes the same path Awake warmed.</summary>
  internal static void SetLogoHoldIntroAbs(string? absolutePath)
  {
    _logoHoldIntroAbs = absolutePath;
  }

  /// <summary>
  /// Disable firepit main cam + framebuffer; enable solid-black cinematic cam.
  /// Leaves Fejd main-menu / logo UI active so it can sit on top (screen-space overlay).
  /// </summary>
  internal static void ApplyBlackUnderLogoCover(CinematicsManager cm)
  {
    if (cm == null)
    {
      return;
    }

    if (cm.m_mainCamera == null)
    {
      cm.m_mainCamera = Utils.GetMainCamera();
    }

    if ((bool)cm.m_mainCamera)
    {
      cm.m_mainCamera.enabled = false;
    }

    if (cm.m_frameBufferScaler != null)
    {
      cm.m_frameBufferScaler.gameObject.SetActive(false);
    }

    cm.m_camera.clearFlags = CameraClearFlags.SolidColor;
    cm.m_camera.backgroundColor = Color.black;
    cm.m_camera.enabled = true;
    ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, "Cinematics intro: black-under-logo cover on.");
  }

  internal static void ClearBlackUnderLogoCover(CinematicsManager cm)
  {
    if (cm == null)
    {
      return;
    }

    if (cm.m_frameBufferScaler != null)
    {
      cm.m_frameBufferScaler.gameObject.SetActive(true);
    }

    cm.m_camera.enabled = false;
    if ((bool)cm.m_mainCamera)
    {
      cm.m_mainCamera.enabled = true;
    }
  }

  [HarmonyPostfix]
  [HarmonyPatch("Awake")]
  private static void AwakePostfix()
  {
    CinematicsStore.OnCatalogReady();
    // Earliest Prepare during logo / startup scene (aligned ticket 19). Fejd holds logo until ready.
    if (!Game.m_hasStartedOnce)
    {
      TryWarmPrepare(VanillaCatalog.IntroName);
    }
  }

  /// <summary>
  /// Replace path owns Play: never start the vanilla clip. Keep the live scene visible until the URL
  /// is prepared, then cut once — no black beat. Intro warm-prepare is Fejd-only (see FejdStartupPatches).
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(nameof(CinematicsManager.Play), typeof(CinematicsManager.VideoEntry), typeof(CinematicsManager.VideoCompleteAction))]
  private static bool PlayPrefix(
    CinematicsManager.VideoEntry video,
    CinematicsManager.VideoCompleteAction onStop,
    ref bool __result)
  {
    if (video == null || string.IsNullOrEmpty(video.m_name))
    {
      return true;
    }

    if (_skipReplaceOnce)
    {
      _skipReplaceOnce = false;
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        $"Cinematics replace: Prepare failed — falling back to vanilla for {video.m_name}.");
      return true;
    }

    bool replay = CinematicsStore.AllowReplayOnce;
    if (replay)
    {
      CinematicsStore.AllowReplayOnce = false;
    }
    else if (!CinematicsStore.IsAutoPlayEnabled(video.m_name))
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Skip auto-play (enabled: false): {video.m_name}");
      __result = false;
      return false;
    }

    if (!CinematicsStore.TryPickReplaceClip(video.m_name, out string abs, out CinematicsStore.PickFail fail))
    {
      if (fail == CinematicsStore.PickFail.Missing)
      {
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Warning,
          $"Cinematics replace: no existing files for {video.m_name} — using vanilla.");
      }

      return true;
    }

    // Prefer Fejd logo-hold path so warm promote matches the waited file.
    if (video.m_name == VanillaCatalog.IntroName && !string.IsNullOrEmpty(_logoHoldIntroAbs))
    {
      abs = _logoHoldIntroAbs!;
      _logoHoldIntroAbs = null;
    }

    // Belt-and-suspenders: if Fejd patch missed, still cover firepit before URL Prepare.
    if (video.m_name == VanillaCatalog.IntroName && (bool)FejdStartup.instance)
    {
      ApplyBlackUnderLogoCover(CinematicsManager.s_instance!);
    }

    ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, $"Cinematics replace play (no vanilla clip): {video.m_name} → {abs}");
    __result = BeginReplacePlay(video, onStop, abs);
    return false;
  }

  private static bool WantsSkipInput()
  {
    return ZInput.GetButtonDown("Escape")
           || ZInput.GetButtonDown("Jump")
           || ZInput.GetButtonDown("JoyButtonB");
  }

  // Warm-instant play is retired: Prepare with AudioSource A then recreating/reusing a warm
  // buffer left dreams silent; console `cinematic` (fresh StartUrlPlayback) had audio.
  // SleepText / OnSleep warm hold removed — same Play path as console.

  /// <summary>Warm a specific absolute path (Fejd locks the pick so wait/Play agree).</summary>
  internal static void WarmPrepareExact(string absolutePath)
  {
    if (CinematicsManager.s_instance == null || CinematicsManager.m_playing)
    {
      return;
    }

    if (string.IsNullOrEmpty(absolutePath))
    {
      return;
    }

    WarmPrepareUrl(CinematicsManager.s_instance.m_videoPlayer, absolutePath);
  }

  /// <summary>Start buffering intro replace early (startup / logo). No Fejd required.</summary>
  internal static void TryWarmPrepare(string cinematicName)
  {
    if (Game.m_hasStartedOnce)
    {
      return;
    }

    WarmPrepareIfReplace(cinematicName);
  }

  /// <summary>
  /// Dream buffer during SleepText fade (~4s before OnSleep). Muted Prepare only — world-safe.
  /// </summary>
  internal static void TryWarmDreamPrepare(string cinematicName)
  {
    if (CinematicsManager.s_instance == null || CinematicsManager.m_playing)
    {
      return;
    }

    WarmPrepareIfReplace(cinematicName);
  }

  private static void WarmPrepareIfReplace(string cinematicName)
  {
    if (CinematicsManager.s_instance == null || CinematicsManager.m_playing)
    {
      return;
    }

    if (!CinematicsStore.IsAutoPlayEnabled(cinematicName))
    {
      return;
    }

    if (!CinematicsStore.TryPickReplaceClip(cinematicName, out string abs, out _))
    {
      return;
    }

    WarmPrepareUrl(CinematicsManager.s_instance.m_videoPlayer, abs);
  }

  internal static void CancelWarmBuffer()
  {
    CancelPendingPrepare(CinematicsManager.s_instance != null ? CinematicsManager.s_instance.m_videoPlayer : null);
    SilenceReplaceAudio(CinematicsManager.s_instance != null ? CinematicsManager.s_instance.m_videoPlayer : null);
    if (CinematicsManager.s_instance != null && CinematicsManager.s_instance.m_videoPlayer != null)
    {
      VideoPlayer vp = CinematicsManager.s_instance.m_videoPlayer;
      vp.playOnAwake = false;
      if (vp.isPlaying)
      {
        vp.Stop();
      }

      vp.url = string.Empty;
      vp.clip = null;
    }

    _warmReady = false;
    _warmUrl = null;
    _awaitingReveal = false;
    _activeReplaceVideo = null;
    _activeReplaceOnStop = null;
  }

  internal static bool IsWarmReady(string absolutePath)
  {
    if (!_warmReady || CinematicsManager.s_instance == null)
    {
      return false;
    }

    string url = CinematicsStore.ToVideoPlayerUrl(absolutePath);
    VideoPlayer vp = CinematicsManager.s_instance.m_videoPlayer;
    return vp != null
      && _warmUrl == url
      && vp.source == VideoSource.Url
      && vp.url == url
      && vp.isPrepared;
  }

  private static bool BeginReplacePlay(
    CinematicsManager.VideoEntry video,
    CinematicsManager.VideoCompleteAction onStop,
    string absolutePath)
  {
    if (video == null || (video.m_videoClip == null && video.m_videoClipLow == null))
    {
      return false;
    }

    if (CinematicsManager.s_instance == null)
    {
      return false;
    }

    CinematicsManager cm = CinematicsManager.s_instance;
    string url = CinematicsStore.ToVideoPlayerUrl(absolutePath);
    bool promoteWarm = IsWarmReady(absolutePath);

    // Promote warm only when the same AudioSource is still bound (never destroy/recreate it —
    // that caused silent dreams). Otherwise console-like fresh StartUrlPlayback.
    if (promoteWarm)
    {
      EndSessionForReplace(keepPreparedUrl: url);
      CancelPendingPrepare(cm.m_videoPlayer);
    }
    else
    {
      EndSessionForReplace(keepPreparedUrl: null);
      CancelWarmBuffer();
    }

    Game.Pause();
    CinematicsManager.m_playing = true;
    _awaitingReveal = true;
    _activeReplaceVideo = video;
    _activeReplaceOnStop = onStop;
    if ((bool)Chat.instance)
    {
      Chat.instance.ClearAllNpcTexts();
    }

    if (!video.m_settings.HasFlag(CinematicsManager.Settings.ShowCursor))
    {
      ZCursor.Hide();
    }

    ZLog.Log("Playing cinematic: " + video.m_name);
    cm.m_currentVideo = video;
    cm.m_onStopped = onStop;
    cm.m_mainCamera = Utils.GetMainCamera();
    // Intro: keep black cinematic cam under logo (vanilla never Prepares — no equivalent).
    // Re-apply after EndSessionForReplace so a prior session cannot flash the firepit cam.
    if (video.m_name == VanillaCatalog.IntroName && (bool)FejdStartup.instance)
    {
      ApplyBlackUnderLogoCover(cm);
    }
    else if (cm.m_camera != null)
    {
      cm.m_camera.enabled = false;
    }

    cm.m_videoPlayer.isLooping = video.m_settings.HasFlag(CinematicsManager.Settings.Loop);
    cm.m_subtitleQueue = null;
    cm.m_activeSubtitle = null;
    if (cm.m_subtitleText != null)
    {
      cm.m_subtitleText.text = string.Empty;
    }

    if (cm.m_subtitleCanvas != null)
    {
      cm.m_subtitleCanvas.SetActive(false);
    }

    float volume = PlatformPrefs.GetFloat("MasterVolume", 1f);
    cm.m_volume = volume;
    bool muteWorld = !video.m_settings.HasFlag(CinematicsManager.Settings.NoMute);

    bool hideGui = !video.m_settings.HasFlag(CinematicsManager.Settings.NoHideGUI);

    if ((bool)FejdStartup.instance && video.m_playCredits)
    {
      FejdStartup.instance.OnCredits();
    }

    void RevealAndPlay(VideoPlayer vp, AudioSource src)
    {
      if (CinematicsManager.s_instance == null || !CinematicsManager.m_playing)
      {
        return;
      }

      CinematicsManager revealCm = CinematicsManager.s_instance;
      if ((bool)FejdStartup.instance && FejdStartup.instance.m_mainMenu != null)
      {
        FejdStartup.instance.m_mainMenu.SetActive(false);
      }

      revealCm.m_frameBufferScaler.gameObject.SetActive(value: false);
      if (hideGui)
      {
        foreach (GameObject hider in CinematicsManager.m_hiders)
        {
          if ((bool)hider && hider.activeSelf)
          {
            CinematicsManager.m_hidden.Add(hider);
            hider.SetActive(value: false);
          }
        }
      }

      if ((bool)revealCm.m_mainCamera)
      {
        revealCm.m_mainCamera.enabled = false;
      }

      revealCm.m_camera.enabled = true;

      if (muteWorld)
      {
        AudioListener.volume = 0f;
      }

      ApplyReplaceAudio(vp, src, volume);
      vp.Play();
      ApplyReplaceAudio(vp, src, volume);
      SleepTextPatches.HideSleepOverlayIfAny();
      _warmReady = false;
      _awaitingReveal = false;
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Info,
        $"Cinematics replace playing URL: tracks={vp.audioTrackCount}, srcMute={src.mute}, srcVol={src.volume:0.###}, url={url}");
    }

    AudioSource src = EnsureReplaceAudioSource(cm.m_videoPlayer.gameObject, recreate: false);

    if (promoteWarm)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics replace: promote warm buffer (same AudioSource), tracks={cm.m_videoPlayer.audioTrackCount}.");
      cm.m_videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
      cm.m_videoPlayer.controlledAudioTrackCount = 1;
      cm.m_videoPlayer.SetTargetAudioSource(0, src);
      RevealAndPlay(cm.m_videoPlayer, src);
      return true;
    }

    ConfigureReplaceAudioSource(src, volume, muted: true);
    StartUrlPlayback(cm.m_videoPlayer, absolutePath, volume, src, onReady: RevealAndPlay);
    return true;
  }

  /// <summary>
  /// Vanilla <c>Stop</c> returns early when <c>clip == null</c>. URL replace clears clip,
  /// so Esc would restore the UI while VideoPlayer + our AudioSource keep playing.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(nameof(CinematicsManager.Stop))]
  private static bool StopPrefix()
  {
    CinematicsManager? cm = CinematicsManager.s_instance;
    if (cm == null || cm.m_videoPlayer == null)
    {
      return true;
    }

    VideoPlayer vp = cm.m_videoPlayer;
    bool urlPlaying = vp.source == VideoSource.Url && !string.IsNullOrEmpty(vp.url);
    if (!urlPlaying || vp.clip != null)
    {
      return true;
    }

    StopUrlReplace(cm);
    return false;
  }

  [HarmonyPostfix]
  [HarmonyPatch(nameof(CinematicsManager.Stop))]
  private static void StopPostfix()
  {
    CancelPendingPrepare(CinematicsManager.s_instance != null ? CinematicsManager.s_instance.m_videoPlayer : null);
    SilenceReplaceAudio(CinematicsManager.s_instance != null ? CinematicsManager.s_instance.m_videoPlayer : null);
    _warmReady = false;
    _warmUrl = null;
    _awaitingReveal = false;
    _activeReplaceVideo = null;
    _activeReplaceOnStop = null;
  }

  [HarmonyPostfix]
  [HarmonyPatch("Update")]
  private static void UpdatePostfix(CinematicsManager __instance)
  {
    // Esc / skip while frozen on last frame waiting for Prepare (vanilla only listens after frame > 5 + isPlaying).
    if (CinematicsManager.m_playing && _awaitingReveal && WantsSkipInput())
    {
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, "Cinematics replace: Esc/skip during Prepare hold.");
      CinematicsManager.Stop();
      return;
    }

    VideoPlayer vp = __instance.m_videoPlayer;
    if (vp == null || !CinematicsManager.m_playing)
    {
      return;
    }

    if (vp.frame <= 5 || vp.isPlaying)
    {
      return;
    }

    if (vp.source == VideoSource.Url && !string.IsNullOrEmpty(vp.url) && vp.clip == null)
    {
      CinematicsManager.Stop();
    }
  }

  private static void EndSessionForReplace(string? keepPreparedUrl)
  {
    CinematicsManager? cm = CinematicsManager.s_instance;
    if (cm == null)
    {
      return;
    }

    if (keepPreparedUrl == null)
    {
      CancelPendingPrepare(cm.m_videoPlayer);
    }

    if (CinematicsManager.m_playing)
    {
      cm.m_frameBufferScaler.gameObject.SetActive(value: true);
      Game.Unpause();
      ZCursor.Show();
      if (cm.m_onStopped != null)
      {
        cm.m_onStopped(cm.m_currentVideo, cm.m_videoPlayer.isPlaying);
      }

      cm.m_camera.enabled = false;
      if ((bool)cm.m_mainCamera)
      {
        cm.m_mainCamera.enabled = true;
      }

      cm.m_currentVideo = null;
      cm.m_onStopped = null;
      AudioListener.volume = 1f;
      foreach (GameObject item in CinematicsManager.m_hidden)
      {
        if ((bool)item)
        {
          item.SetActive(value: true);
        }
      }

      CinematicsManager.m_hidden.Clear();
      CinematicsManager.m_playing = false;
      _awaitingReveal = false;
      _activeReplaceVideo = null;
      _activeReplaceOnStop = null;
    }

    if (keepPreparedUrl == null)
    {
      SilenceReplaceAudio(cm.m_videoPlayer);
      cm.m_videoPlayer.playOnAwake = false;
      cm.m_videoPlayer.Stop();
      cm.m_videoPlayer.url = string.Empty;
      cm.m_videoPlayer.clip = null;
      _warmReady = false;
      _warmUrl = null;
    }
    // else: leave prepared / in-flight URL intact (do not VideoPlayer.Stop).
  }

  private static void StopUrlReplace(CinematicsManager cm)
  {
    CancelPendingPrepare(cm.m_videoPlayer);
    cm.m_frameBufferScaler.gameObject.SetActive(value: true);
    CinematicsManager.m_playing = false;
    Game.Unpause();
    ZCursor.Show();
    if (cm.m_onStopped != null)
    {
      cm.m_onStopped(cm.m_currentVideo, cm.m_videoPlayer.isPlaying);
    }

    SilenceReplaceAudio(cm.m_videoPlayer);
    cm.m_videoPlayer.playOnAwake = false;
    cm.m_videoPlayer.Stop();
    cm.m_videoPlayer.url = string.Empty;
    cm.m_videoPlayer.clip = null;
    cm.m_camera.enabled = false;
    if ((bool)cm.m_mainCamera)
    {
      cm.m_mainCamera.enabled = true;
    }

    cm.m_currentVideo = null;
    AudioListener.volume = 1f;
    foreach (GameObject item in CinematicsManager.m_hidden)
    {
      if ((bool)item)
      {
        item.SetActive(value: true);
      }
    }

    CinematicsManager.m_hidden.Clear();
    _warmReady = false;
    _warmUrl = null;
    _awaitingReveal = false;
    _activeReplaceVideo = null;
    _activeReplaceOnStop = null;
    if ((bool)FejdStartup.instance)
    {
      FejdStartup.instance.ResetStartupMusic();
      FejdStartup.instance.m_mainMenu.SetActive(value: true);
    }

    ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, "Cinematics replace: URL Stop completed (vanilla early-out bypassed).");
  }

  private static void WarmPrepareUrl(VideoPlayer player, string absolutePath)
  {
    if (player == null)
    {
      return;
    }

    string url = CinematicsStore.ToVideoPlayerUrl(absolutePath);
    if (_warmReady && _warmUrl == url && player.isPrepared && player.url == url)
    {
      return;
    }

    CancelPendingPrepare(player);
    _warmReady = false;
    _warmUrl = url;

    AudioSource src = EnsureReplaceAudioSource(player.gameObject);
    // Stay silent until RevealAndPlay — Prepare must not leak audio.
    ConfigureReplaceAudioSource(src, volume: 0f, muted: true);

    player.playOnAwake = false;
    player.Stop();
    player.clip = null;
    player.source = VideoSource.Url;
    player.url = url;
    player.audioOutputMode = VideoAudioOutputMode.AudioSource;
    player.controlledAudioTrackCount = 1;
    player.SetTargetAudioSource(0, src);

    void OnWarmPrepared(VideoPlayer vp)
    {
      if (_pendingPrepared != null)
      {
        vp.prepareCompleted -= _pendingPrepared;
        _pendingPrepared = null;
      }

      if (CinematicsManager.m_playing || vp.url != url)
      {
        return;
      }

      // Do not Pause() a prepared player — Pause after Prepare correlated with silent 2nd dream Play.
      ConfigureReplaceAudioSource(EnsureReplaceAudioSource(vp.gameObject), volume: 0f, muted: true);
      _warmReady = true;
      _warmUrl = url;
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, $"Cinematics replace warm-prepared: {url}");
    }

    _pendingPrepared = OnWarmPrepared;
    player.prepareCompleted += _pendingPrepared;
    player.Prepare();
  }

  private static void StartUrlPlayback(
    VideoPlayer player,
    string absolutePath,
    float volume,
    AudioSource src,
    Action<VideoPlayer, AudioSource> onReady)
  {
    CancelPendingPrepare(player);
    _warmReady = false;

    string url = CinematicsStore.ToVideoPlayerUrl(absolutePath);
    ConfigureReplaceAudioSource(src, volume, muted: true);

    player.playOnAwake = false;
    player.Stop();
    player.clip = null;
    player.source = VideoSource.Url;
    player.url = url;
    player.audioOutputMode = VideoAudioOutputMode.AudioSource;
    player.controlledAudioTrackCount = 1;
    player.SetTargetAudioSource(0, src);

    void OnPrepared(VideoPlayer vp)
    {
      if (_pendingPrepared != null)
      {
        vp.prepareCompleted -= _pendingPrepared;
        _pendingPrepared = null;
      }

      if (!CinematicsManager.m_playing)
      {
        return;
      }

      ushort trackCount = vp.audioTrackCount;
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Info,
        $"Cinematics replace prepared: tracks={trackCount}, mode={vp.audioOutputMode}, masterVolume={volume:0.###}, url={url}");

      if (trackCount == 0)
      {
        ModzifiedCinematicsPlugin.LogAt(
          LogLevel.Warning,
          $"Cinematics replace: no audio tracks (video will be silent). {MediaHints.LogFixHint}");
      }

      onReady(vp, src);
    }

    _pendingPrepared = OnPrepared;
    player.prepareCompleted += _pendingPrepared;
    player.Prepare();
  }

  private static void ApplyReplaceAudio(VideoPlayer vp, AudioSource src, float volume)
  {
    vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
    vp.controlledAudioTrackCount = 1;

    try
    {
      if (vp.audioTrackCount > 0)
      {
        vp.EnableAudioTrack(0, true);
      }
    }
    catch
    {
      // Track count can be stale while switching sources.
    }

    vp.SetTargetAudioSource(0, src);
    ConfigureReplaceAudioSource(src, volume, muted: false);
  }

  private static void ConfigureReplaceAudioSource(AudioSource src, float volume, bool muted)
  {
    src.ignoreListenerVolume = true;
    src.ignoreListenerPause = true;
    src.mute = muted;
    src.volume = muted ? 0f : Mathf.Clamp01(volume);
    src.spatialBlend = 0f;
    src.playOnAwake = false;
    src.enabled = true;
  }

  private static void CancelPendingPrepare(VideoPlayer? player)
  {
    if (_pendingPrepared == null || player == null)
    {
      _pendingPrepared = null;
      return;
    }

    player.prepareCompleted -= _pendingPrepared;
    _pendingPrepared = null;
  }

  private static AudioSource EnsureReplaceAudioSource(GameObject host, bool recreate = false)
  {
    Transform existing = host.transform.Find(ReplaceAudioChild);
    if (recreate && existing != null)
    {
      UnityEngine.Object.Destroy(existing.gameObject);
      existing = null;
    }

    GameObject child;
    if (existing == null)
    {
      child = new GameObject(ReplaceAudioChild);
      child.transform.SetParent(host.transform, false);
    }
    else
    {
      child = existing.gameObject;
    }

    AudioSource src = child.GetComponent<AudioSource>();
    if (src == null)
    {
      src = child.AddComponent<AudioSource>();
    }

    return src;
  }

  private static void SilenceReplaceAudio(VideoPlayer? player)
  {
    if (player == null)
    {
      return;
    }

    // Do not DisableAudioTrack — that stuck muted across the next warm-instant dream.
    Transform child = player.transform.Find(ReplaceAudioChild);
    if (child == null)
    {
      return;
    }

    AudioSource src = child.GetComponent<AudioSource>();
    if (src == null)
    {
      return;
    }

    src.Stop();
    src.mute = true;
    src.volume = 0f;
  }

  /// <summary>
  /// Vanilla <c>GetMusic</c> does <c>m_currentVideo.m_seperateMusic</c> while <c>IsPlaying()</c>.
  /// URL replace / warm buffer can leave the VideoPlayer playing with a null current video.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(nameof(CinematicsManager.GetMusic))]
  private static bool GetMusicPrefix(ref string __result)
  {
    if (CinematicsManager.s_instance == null || CinematicsManager.s_instance.m_currentVideo == null)
    {
      __result = "";
      return false;
    }

    return true;
  }

  [HarmonyPrefix]
  [HarmonyPatch("OnPlaybackError")]
  private static bool OnPlaybackErrorPrefix(VideoPlayer source, string message)
  {
    string label = "?";
    if (source != null)
    {
      if (source.clip != null)
      {
        label = source.clip.name;
      }
      else if (!string.IsNullOrEmpty(source.url))
      {
        label = source.url;
      }
    }

    ModzifiedCinematicsPlugin.LogAt(
      LogLevel.Error,
      $"Cinematic playback failed ({label}): {message}. {MediaHints.LogFixHint}");

    // Prepare / URL fail before reveal → drop freeze and play vanilla once.
    if (_awaitingReveal && _activeReplaceVideo != null)
    {
      CinematicsManager.VideoEntry video = _activeReplaceVideo;
      CinematicsManager.VideoCompleteAction? onStop = _activeReplaceOnStop;
      CancelWarmBuffer();
      if (CinematicsManager.s_instance != null)
      {
        CinematicsManager.m_playing = false;
        Game.Unpause();
        ZCursor.Show();
        CinematicsManager.s_instance.m_camera.enabled = false;
        if ((bool)CinematicsManager.s_instance.m_mainCamera)
        {
          CinematicsManager.s_instance.m_mainCamera.enabled = true;
        }

        CinematicsManager.s_instance.m_currentVideo = null;
        CinematicsManager.s_instance.m_onStopped = null;
        AudioListener.volume = 1f;
      }

      _skipReplaceOnce = true;
      CinematicsManager.Play(video, onStop);
      return false;
    }

    CinematicsManager.Stop();
    return false;
  }
}
