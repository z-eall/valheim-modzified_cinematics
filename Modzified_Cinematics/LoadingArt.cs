using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modzified_Cinematics;

/// <summary>
/// One <c>loadingArt</c> list for every loading-style black screen: Hud world load, Hud portal, door teleport,
/// logout, and the main-menu → world load. Drawn as our own fullscreen image (cover-fit, no vignette, slow zoom);
/// vanilla full-width images are hidden while it shows. Sleeping, dead and cinematic screens stay vanilla.
/// </summary>
internal static class LoadingArt
{
  /// <summary>Vanilla images at least this share of the screen width count as art / vignette / swirl and are hidden.</summary>
  private const float VanillaArtWidthShare = 0.9f;

  /// <summary>Menu load, world load and the respawn load after the first-spawn intro share one picture within this window.</summary>
  private const float JoinSequenceSeconds = 300f;

  /// <summary>One place art can show: the Hud black screen, or the main-menu loading object.</summary>
  private sealed class Surface
  {
    internal string Name = "";
    internal Transform? Root;
    internal bool Active;
    internal bool Failed;
    /// <summary>Kinds already layout-dumped (once each — "loading" alone used to swallow the flag for portal/door/logout).</summary>
    internal readonly HashSet<string> Dumped = new();
    internal bool Probed;
    internal string Kind = "";
    internal float Start;
    internal GameObject? Overlay;
    internal Image? Image;
    internal RectTransform? Rect;
    internal GameObject? Spinner;
    /// <summary>Cloned tip text for screens that have none of their own (portal, door, logout).</summary>
    internal GameObject? TipObj;
    internal TMP_Text? TipLabel;
    internal int TipIndex;
    internal bool TipStarted;
    internal Sprite? Sprite;
    internal Texture2D? Tex;
    internal readonly List<(Graphic graphic, bool wasEnabled)> Hidden = new();
  }

  private static readonly Surface HudSurface = new() { Name = "hud" };
  private static readonly Surface MenuSurface = new() { Name = "menu" };

  /// <summary>Own generator: the game re-seeds <c>UnityEngine.Random</c>, which made the same picture repeat.</summary>
  private static readonly System.Random Rng = new(Environment.TickCount ^ Guid.NewGuid().GetHashCode());

  private static string _lastPick = "";
  private static string _joinPick = "";
  private static float _joinPickTime = -1000f;

  /// <summary>
  /// Runtime bind — compile against ImageConversionModule pulls netstandard 2.1 on net48.
  /// </summary>
  private static MethodInfo? _loadImage;

  // ---------- Hud black screen ----------

  internal static void Tick(Hud hud, Player? player)
  {
    if (hud == null || hud.m_loadingScreen == null)
    {
      return;
    }

    Surface s = HudSurface;
    Transform root = hud.m_loadingScreen.transform;
    if (s.Root != root)
    {
      // The Hud is rebuilt for every world: drop state that pointed at the old one.
      Reset(s);
      s.Root = root;
    }

    // A visit lasts while the vanilla screen object is on (it switches off only when fully faded out);
    // alpha only gates the start. Ending on an alpha dip restarted the visit with a new picture.
    bool on = hud.m_loadingScreen.gameObject.activeSelf;
    if (!on)
    {
      s.Failed = false;
      if (s.Active)
      {
        End(s);
      }

      return;
    }

    string kind = ClassifyHud(hud, player);
    if (!s.Active)
    {
      if (kind.Length == 0 || hud.m_loadingScreen.alpha <= 0.001f || s.Failed || LoadingScreensStore.LoadingArts.Count == 0)
      {
        return;
      }

      if (!Begin(s, root, kind, hud))
      {
        s.Failed = true;
        return;
      }
    }
    else if (kind.Length > 0 && !string.Equals(kind, s.Kind, StringComparison.Ordinal))
    {
      s.Kind = kind;
      Restyle(s, hud);
    }

    Layout(s);
    TickTip(s, hud);

    if (s.Kind == "loading" && !s.Probed && Time.unscaledTime - s.Start > 1.5f)
    {
      s.Probed = true;
      ProbeTip(s, hud);
    }
  }

  /// <summary>
  /// Which vanilla screen is up. Empty = leave vanilla (sleeping, dead, cinematic, nothing).
  /// Once a visit has started it stays covered through the fade-out (see <see cref="Tick"/>).
  /// </summary>
  private static string ClassifyHud(Hud hud, Player? player)
  {
    if (hud.m_sleepingProgress != null && hud.m_sleepingProgress.activeSelf)
    {
      return "";
    }

    if (hud.m_loadingProgress != null && hud.m_loadingProgress.activeSelf)
    {
      return "loading";
    }

    if (hud.m_teleportingProgress != null && hud.m_teleportingProgress.activeSelf)
    {
      return "portal";
    }

    if (Game.instance != null && Game.instance.IsShuttingDown())
    {
      return "logout";
    }

    if (player != null && player.IsTeleporting())
    {
      return "door";
    }

    return "";
  }

  // ---------- Main-menu loading screen ----------

  /// <summary><c>FejdStartup.LoadMainScene</c> just switched <c>m_loading</c> on.</summary>
  internal static void BeginMenu(GameObject? loading)
  {
    if (loading == null || LoadingScreensStore.LoadingArts.Count == 0)
    {
      return;
    }

    Surface s = MenuSurface;
    if (s.Root != loading.transform)
    {
      Reset(s);
      s.Root = loading.transform;
    }

    if (s.Active)
    {
      return;
    }

    if (!Begin(s, loading.transform, "menu", null))
    {
      s.Failed = true;
    }
  }

  /// <summary>Called from the plugin <c>Update</c>: animates the menu overlay and ends it when the scene switches.</summary>
  internal static void MenuTick()
  {
    Surface s = MenuSurface;
    if (!s.Active)
    {
      return;
    }

    if (s.Root == null || !s.Root.gameObject.activeInHierarchy)
    {
      End(s);
      return;
    }

    Layout(s);
  }

  // ---------- Visit lifecycle ----------

  private static bool Begin(Surface s, Transform root, string kind, Hud? hud)
  {
    string pick = ChoosePick(LoadingScreensStore.LoadingArts, kind);
    DestroySprite(s);
    s.Sprite = TryLoadSprite(pick, out s.Tex);
    if (s.Sprite == null)
    {
      return false;
    }

    EnsureOverlay(s, root);
    if (s.Overlay == null || s.Image == null)
    {
      return false;
    }

    if (s.Dumped.Add(kind))
    {
      DumpLayout(root, $"{s.Name}/{kind}");
    }

    s.Image.sprite = s.Sprite;
    s.Overlay.SetActive(true);
    s.Overlay.transform.SetAsFirstSibling();
    s.Kind = kind;
    s.Start = Time.unscaledTime;
    s.Probed = false;
    s.Active = true;
    Restyle(s, hud);
    Layout(s); // menu loading blocks the main thread: size it now, not on the next Update
    ModzifiedCinematicsPlugin.LogAt(
      LogLevel.Debug,
      $"Cinematics art: {s.Name}/{kind} visit -> '{Path.GetFileName(pick)}' " +
      $"(list={LoadingScreensStore.LoadingArts.Count}, hidden={s.Hidden.Count}, spinner={(s.Spinner != null ? "clone" : "vanilla")}).");
    return true;
  }

  private static void End(Surface s)
  {
    s.Active = false;
    s.Kind = "";
    if (s.Overlay != null)
    {
      s.Overlay.SetActive(false);
    }

    RestoreHidden(s);
    DestroySpinner(s);
    DestroyTip(s);
    DestroySprite(s);
  }

  private static void Reset(Surface s)
  {
    s.Active = false;
    s.Failed = false;
    s.Kind = "";
    s.Dumped.Clear();
    s.Probed = false;
    s.Hidden.Clear();
    DestroySpinner(s);
    DestroyTip(s);
    DestroySprite(s);
    if (s.Overlay != null)
    {
      UnityEngine.Object.Destroy(s.Overlay);
    }

    s.Overlay = null;
    s.Image = null;
    s.Rect = null;
  }

  /// <summary>
  /// Random entry, never the same twice in a row when the list has two or more; own RNG.
  /// Join sequence (menu load → world load → first-spawn intro → respawn load) keeps one picture;
  /// portal, door and logout screens pick fresh and end the sequence.
  /// </summary>
  private static string ChoosePick(IReadOnlyList<string> files, string kind)
  {
    bool joinKind = kind == "menu" || kind == "loading";
    float now = Time.realtimeSinceStartup;
    if (kind == "loading" &&
        _joinPick.Length > 0 &&
        now - _joinPickTime < JoinSequenceSeconds &&
        ContainsPath(files, _joinPick))
    {
      _joinPickTime = now;
      _lastPick = _joinPick;
      return _joinPick;
    }

    string pick = files[0];
    if (files.Count > 1)
    {
      for (int guard = 0; guard < 16; guard++)
      {
        pick = files[Rng.Next(files.Count)];
        if (!string.Equals(pick, _lastPick, StringComparison.OrdinalIgnoreCase))
        {
          break;
        }
      }
    }

    _lastPick = pick;
    _joinPick = joinKind ? pick : "";
    _joinPickTime = now;
    return pick;
  }

  private static bool ContainsPath(IReadOnlyList<string> files, string path)
  {
    foreach (string f in files)
    {
      if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    return false;
  }

  // ---------- Look ----------

  private static void EnsureOverlay(Surface s, Transform root)
  {
    if (s.Overlay != null)
    {
      if (s.Overlay.transform.parent != root)
      {
        s.Overlay.transform.SetParent(root, false);
      }

      return;
    }

    s.Overlay = new GameObject("Modzified_Art", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    s.Overlay.transform.SetParent(root, false);
    s.Rect = s.Overlay.GetComponent<RectTransform>();
    s.Rect.anchorMin = new Vector2(0.5f, 0.5f);
    s.Rect.anchorMax = new Vector2(0.5f, 0.5f);
    s.Rect.pivot = new Vector2(0.5f, 0.5f);
    s.Image = s.Overlay.GetComponent<Image>();
    s.Image.raycastTarget = false;
    s.Image.preserveAspect = false;
    s.Overlay.SetActive(false);
  }

  /// <summary>Hide vanilla full-width art for the screen now showing; add the spinner where vanilla has none.</summary>
  private static void Restyle(Surface s, Hud? hud)
  {
    RestoreHidden(s);
    if (s.Root == null)
    {
      return;
    }

    float screenWidth = ScreenSize(s).x;
    if (screenWidth > 0f)
    {
      foreach (Graphic graphic in s.Root.GetComponentsInChildren<Graphic>(includeInactive: false))
      {
        if (graphic == null || !graphic.enabled || IsOurs(s, graphic.transform))
        {
          continue;
        }

        if (graphic is not Image && graphic is not RawImage)
        {
          continue;
        }

        if (graphic.rectTransform.rect.width >= screenWidth * VanillaArtWidthShare)
        {
          s.Hidden.Add((graphic, graphic.enabled));
          graphic.enabled = false;
        }
      }
    }

    if (hud == null)
    {
      return;
    }

    // The portal panel has its own small spinner; the loading spinner (bottom right) is the one we show everywhere.
    if (string.Equals(s.Kind, "portal", StringComparison.Ordinal) && hud.m_teleportingProgress != null)
    {
      Transform? own = hud.m_teleportingProgress.transform.Find("loading");
      Graphic? ownGraphic = own != null ? own.GetComponent<Graphic>() : null;
      if (ownGraphic != null && ownGraphic.enabled)
      {
        s.Hidden.Add((ownGraphic, ownGraphic.enabled));
        ownGraphic.enabled = false;
      }
    }

    bool needSpinner = !string.Equals(s.Kind, "loading", StringComparison.Ordinal);
    if (needSpinner && s.Spinner == null)
    {
      Transform? source = hud.m_loadingProgress != null
        ? hud.m_loadingProgress.transform.Find("LoadingIndicator/Center/Spinner")
        : null;
      if (source != null)
      {
        s.Spinner = UnityEngine.Object.Instantiate(source.gameObject, s.Root, true);
        s.Spinner.name = "Modzified_Spinner";
        s.Spinner.SetActive(true);
      }
    }
    else if (!needSpinner)
    {
      DestroySpinner(s);
    }

    // Every custom screen shows a tip (human decision, 2026-09-22) except menu-load (no Hud yet there).
    // "loading" already has vanilla's own tip text working; the rest get a clone.
    bool needTip = s.Kind is "portal" or "door" or "logout";
    if (needTip && s.TipObj == null && hud.m_loadingTip != null)
    {
      GameObject clone = UnityEngine.Object.Instantiate(hud.m_loadingTip.gameObject, s.Root, true);
      clone.name = "Modzified_Tip";
      clone.SetActive(true);
      s.TipObj = clone;
      s.TipLabel = clone.GetComponent<TMP_Text>();
      s.TipStarted = false;
    }
    else if (!needTip)
    {
      DestroyTip(s);
    }
  }

  private static bool IsOurs(Surface s, Transform t)
  {
    return (s.Overlay != null && t.IsChildOf(s.Overlay.transform)) ||
           (s.Spinner != null && t.IsChildOf(s.Spinner.transform)) ||
           (s.TipObj != null && t.IsChildOf(s.TipObj.transform));
  }

  /// <summary>
  /// Mirrors vanilla <c>Hud.UpdateShownTip</c> (five button checks + an array index — cheap, so we imitate
  /// it rather than drop cycling) on our cloned tip label. Uses the same weighted pool the loading screen
  /// already built via <see cref="Hud.m_loadingTips"/>; if that is empty (art shown before any load screen
  /// ever ran once), the clone just keeps its last text.
  /// </summary>
  private static void TickTip(Surface s, Hud hud)
  {
    if (s.TipLabel == null)
    {
      return;
    }

    List<string> tips = hud.m_loadingTips;
    if (tips == null || tips.Count == 0)
    {
      return;
    }

    int num = s.TipIndex;
    if (ZInput.GetButtonDown("JoyButtonA") || ZInput.GetKeyDown(KeyCode.Space) ||
        ZInput.GetMouseButtonDown(0) || ZInput.GetButtonDown("JoyDPadRight") || ZInput.GetKeyDown(KeyCode.RightArrow))
    {
      num++;
    }

    if (ZInput.GetButtonDown("JoyDPadLeft") || ZInput.GetKeyDown(KeyCode.LeftArrow))
    {
      num--;
    }

    if (num >= tips.Count)
    {
      num = 0;
    }
    else if (num < 0)
    {
      num = tips.Count - 1;
    }

    bool changed = num != s.TipIndex || !s.TipStarted;
    s.TipIndex = num;
    if (!changed)
    {
      return;
    }

    s.TipStarted = true;
    s.TipLabel.text = Localization.instance.Localize(tips[num]);
  }

  private static void DestroyTip(Surface s)
  {
    if (s.TipObj != null)
    {
      UnityEngine.Object.Destroy(s.TipObj);
    }

    s.TipObj = null;
    s.TipLabel = null;
    s.TipStarted = false;
  }

  private static void RestoreHidden(Surface s)
  {
    foreach ((Graphic graphic, bool wasEnabled) in s.Hidden)
    {
      if (graphic != null)
      {
        graphic.enabled = wasEnabled;
      }
    }

    s.Hidden.Clear();
  }

  private static void DestroySpinner(Surface s)
  {
    if (s.Spinner != null)
    {
      UnityEngine.Object.Destroy(s.Spinner);
    }

    s.Spinner = null;
  }

  private static Vector2 ScreenSize(Surface s)
  {
    if (s.Root == null)
    {
      return Vector2.zero;
    }

    RectTransform? rt = s.Root.GetComponent<RectTransform>();
    if (rt != null && rt.rect.width > 0f && rt.rect.height > 0f)
    {
      return new Vector2(rt.rect.width, rt.rect.height);
    }

    Canvas? canvas = s.Root.GetComponentInParent<Canvas>();
    if (canvas != null)
    {
      RectTransform? top = canvas.rootCanvas.GetComponent<RectTransform>();
      if (top != null)
      {
        return new Vector2(top.rect.width, top.rect.height);
      }
    }

    return Vector2.zero;
  }

  private static void Layout(Surface s)
  {
    if (s.Rect == null || s.Image == null || s.Sprite == null)
    {
      return;
    }

    Vector2 screen = ScreenSize(s);
    float tw = s.Sprite.rect.width;
    float th = s.Sprite.rect.height;
    if (screen.x <= 0f || screen.y <= 0f || tw <= 0f || th <= 0f)
    {
      return;
    }

    // Cover-fit: fill the screen, crop the overflow. Same zoom on every screen.
    float scale = Mathf.Max(screen.x / tw, screen.y / th);
    float zoom = Zoom(Time.unscaledTime - s.Start);
    s.Rect.anchoredPosition = Vector2.zero;
    s.Rect.localRotation = Quaternion.identity;
    s.Rect.sizeDelta = new Vector2(tw * scale * zoom, th * scale * zoom);

    float shade = 1f - Settings.ArtDimValue;
    s.Image.color = new Color(shade, shade, shade, 1f);
  }

  /// <summary>Slow sine drift: starts at 1, peaks at Art zoom max halfway through the cycle.</summary>
  private static float Zoom(float seconds)
  {
    float phase = seconds / Settings.ArtZoomSecondsValue * Mathf.PI * 2f;
    return 1f + (Settings.ArtZoomMaxValue - 1f) * 0.5f * (1f - Mathf.Cos(phase));
  }

  // ---------- Image loading ----------

  private static Sprite? TryLoadSprite(string path, out Texture2D? tex)
  {
    tex = null;
    try
    {
      byte[] bytes = File.ReadAllBytes(path);
      tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
      if (!TryLoadImage(tex, bytes))
      {
        UnityEngine.Object.Destroy(tex);
        tex = null;
        ModzifiedCinematicsPlugin.LogWarnOnce($"Art file could not be read as an image: {path}. Re-save it as PNG or JPG.");
        return null;
      }

      return Sprite.Create(
        tex,
        new Rect(0f, 0f, tex.width, tex.height),
        new Vector2(0.5f, 0.5f),
        100f);
    }
    catch (Exception ex)
    {
      if (tex != null)
      {
        UnityEngine.Object.Destroy(tex);
        tex = null;
      }

      ModzifiedCinematicsPlugin.LogWarnOnce($"Art file could not be loaded: {path} ({ex.Message}).");
      return null;
    }
  }

  private static bool TryLoadImage(Texture2D tex, byte[] bytes)
  {
    if (_loadImage == null)
    {
      Type? type = Type.GetType(
        "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
      _loadImage = type?.GetMethod(
        "LoadImage",
        BindingFlags.Public | BindingFlags.Static,
        binder: null,
        types: new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
        modifiers: null);
    }

    if (_loadImage == null)
    {
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Warning,
        "Cinematics art: ImageConversion.LoadImage not found at runtime.");
      return false;
    }

    object? result = _loadImage.Invoke(null, new object[] { tex, bytes, true });
    return result is true;
  }

  private static void DestroySprite(Surface s)
  {
    if (s.Image != null)
    {
      s.Image.sprite = null;
    }

    if (s.Sprite != null)
    {
      UnityEngine.Object.Destroy(s.Sprite);
    }

    if (s.Tex != null)
    {
      UnityEngine.Object.Destroy(s.Tex);
    }

    s.Sprite = null;
    s.Tex = null;
  }

  // ---------- Debug layout dump ----------

  /// <summary>Debug, once per surface: every Image / RawImage under the screen, so layering can be tuned from a log.</summary>
  private static void DumpLayout(Transform top, string label)
  {
    if (!ModzifiedCinematicsPlugin.Allows(LogLevel.Debug))
    {
      return;
    }

    try
    {
      StringBuilder sb = new();
      sb.AppendLine($"Cinematics art: layout while {label} is showing (Debug, once).");
      foreach (Graphic g in top.GetComponentsInChildren<Graphic>(includeInactive: true))
      {
        if (g.gameObject.name == "Modzified_Art")
        {
          continue;
        }

        Rect r = g.rectTransform.rect;
        string sprite = g is Image img && img.sprite != null ? img.sprite.name : "-";
        if (g is TMP_Text tmp)
        {
          sprite = "text:" + Trim(tmp.text);
        }
        StringBuilder comps = new();
        foreach (Component c in g.gameObject.GetComponents<Component>())
        {
          if (c != null && c is not Transform && c is not CanvasRenderer && c is not Graphic)
          {
            comps.Append(c.GetType().Name).Append(' ');
          }
        }

        sb.AppendLine(
          $"  {PathFrom(top, g.transform)} | {g.GetType().Name} enabled={g.enabled} active={g.gameObject.activeInHierarchy} " +
          $"size={r.width:0}x{r.height:0} alpha={g.color.a:0.00} rotZ={g.transform.localEulerAngles.z:0} " +
          $"sprite={sprite} other=[{comps.ToString().Trim()}]");
      }

      ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, sb.ToString().TrimEnd());
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, $"Cinematics art: layout dump failed ({ex.Message}).");
    }
  }

  private static string Trim(string s)
  {
    s = (s ?? "").Replace("\n", " ").Replace("\r", " ");
    return s.Length > 40 ? s.Substring(0, 40) : s;
  }

  /// <summary>Debug, once per loading visit: where the tip text sits and whether anything could cover it.</summary>
  private static void ProbeTip(Surface s, Hud hud)
  {
    if (!ModzifiedCinematicsPlugin.Allows(LogLevel.Debug))
    {
      return;
    }

    try
    {
      TMP_Text? tip = hud.m_loadingTip;
      if (tip == null || s.Root == null)
      {
        ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, "Cinematics art: tip probe — Hud.m_loadingTip is null.");
        return;
      }

      StringBuilder chain = new();
      for (Transform? p = tip.transform; p != null && p != s.Root.parent; p = p.parent)
      {
        chain.Insert(0, $"{p.name}[{p.GetSiblingIndex()}]/");
      }

      Canvas? canvas = tip.canvas;
      Vector3[] corners = new Vector3[4];
      tip.rectTransform.GetWorldCorners(corners);
      ModzifiedCinematicsPlugin.LogAt(
        LogLevel.Debug,
        $"Cinematics art: tip probe — {chain}{tip.name} enabled={tip.enabled} active={tip.gameObject.activeInHierarchy} " +
        $"color={tip.color} rendererAlpha={tip.canvasRenderer.GetAlpha():0.00} text='{Trim(tip.text)}' " +
        $"size={tip.rectTransform.rect.width:0}x{tip.rectTransform.rect.height:0} " +
        $"worldY={corners[0].y:0}..{corners[1].y:0} canvasSort={(canvas != null ? canvas.sortingOrder : -1)} " +
        $"override={(canvas != null && canvas.overrideSorting)}; overlay[{(s.Overlay != null ? s.Overlay.transform.GetSiblingIndex() : -1)}] " +
        $"of {s.Root.childCount} children under {s.Root.name}.");
    }
    catch (Exception ex)
    {
      ModzifiedCinematicsPlugin.LogAt(LogLevel.Debug, $"Cinematics art: tip probe failed ({ex.Message}).");
    }
  }

  private static string PathFrom(Transform top, Transform t)
  {
    StringBuilder sb = new(t.name);
    for (Transform? p = t.parent; p != null && p != top.parent; p = p.parent)
    {
      sb.Insert(0, p.name + "/");
    }

    return sb.ToString();
  }
}
