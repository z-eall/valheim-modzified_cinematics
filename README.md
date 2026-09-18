# Modzified_Cinematics

[![Find me here](https://img.shields.io/badge/Find_me_here-Discord-5865F2?logo=discord&logoColor=white&style=flat)](https://discord.gg/VFRJcPwUdm)
[![Support](https://img.shields.io/badge/Support-Ko--fi-FF5E5B?logo=ko-fi&logoColor=white&style=flat)](https://ko-fi.com/zeall)

Turn each vanilla story cinematic on or off, or replace it with your own video file. Part of Zeall's **Modzified** series.

Install on all clients and the server. Server-synced rules follow the host when the server has the mod. Video files are not synced — every playing client needs the same media.

Also install **YamlDotNet** (`ValheimModding-YamlDotNet` on Thunderstore) — one shared copy for the whole load order.

## Features

- Per-cinematic **on/off** and optional **clips** replace via YAML.
- Defaults match vanilla (everything on, original clips).
- Main menu → **Cinematics** can still replay a slot that is off.
- Host YAML live-reloads when you save the file (restart only if reload fails — check the BepInEx log).
- New-world intro off or unavailable keeps the vanilla text intro fallback.
- Custom clips may be any duration.
- Replace volume follows your **Master** slider (read-only; the mod does not change audio settings).

## How to use

1. Install the plugin on every client and the server (plus YamlDotNet).
2. Launch once so `BepInEx/config/modzified_cinematics/modzified_cinematics.yaml` is created.
3. Edit that YAML. Each block starts with plain-English comments (what it is + defaults). Put replace media in `BepInEx/config/modzified_cinematics/clips/` (for example `my_intro.mp4`). Paths in YAML are relative to that `clips` folder.
4. Use **`.mp4` + H.264 video + AAC audio** (see **Export settings** below). List multiple files under `clips:` to pick one at **random** each play (like Expand World Music). Only files present on this client are candidates. Prefer simple file names (letters, numbers, underscore, spaces OK; avoid `:` and odd punctuation). Paths with spaces stay **double-quoted** when the YAML is rewritten.
5. Save the host file to apply; copy the same media onto every client that should see replaces.

Example:

```yaml
# Main-menu story intro: Plays when you launch the game
# Once per Valheim start, until you enter a world
# Default: enabled true, clips empty (= vanilla video)
- name: $cinematics_intro
  enabled: true
  clips:

# Black Forest video: Plays on the next sleep after you defeat Eikthyr
# Queues again if you defeat him again
# Default: enabled true, clips empty (= vanilla video)
- name: $biome_blackforest
  enabled: true
  clips:
  - filename_without_space.mp4
  - "filename with space use quotes.mp4"
```

## Export settings

Valheim can only play what Windows / Unity allow. **Allowed:** `.mp4` container, **H.264** (AVC) video, **AAC** audio. **Often fail or silent:** AV1, many MKV/WebM exports, exotic audio codecs.

Re-export with these knobs (names vary slightly by app):

| Tool | Settings |
|------|----------|
| **HandBrake** | Format: MP4. Video: H.264 (x264). Audio: AAC. |
| **Adobe Premiere** | Export → Match Source / custom. Format: H.264. Video codec: H.264. Audio: AAC. |
| **DaVinci Resolve** | Deliver → MP4. Video codec: H.264. Audio codec: AAC. |
| **CapCut** | Export → MP4. Turn **off** AV1 / “efficient” codecs if offered; pick H.264 + AAC when available. |

If the BepInEx log says playback failed or “no audio tracks,” re-export with the table above and try again.

## Configuration

File: `BepInEx/config/modzified_cinematics.cfg` — **Log levels** only (advanced).

Rules file (not cfg keys): `BepInEx/config/modzified_cinematics/modzified_cinematics.yaml`

- `name` — vanilla id (keep as-is; the `#` comments above each block explain which video it is).
- `enabled` — `true` allows auto-play; `false` skips auto-play (main menu → Cinematics can still replay).
- `clips` — empty = vanilla video; one or more paths = replace (random among files that exist on this client).

## Mod compatibility

May fight other mods that also skip or replace story intros (for example SkipIntroVideo, CustomMainMenu, ModsmithCycle). Prefer one intro controller.

## Credits

Source: [<img src="https://cdn.simpleicons.org/github/181717" width="16" height="16" alt="" /> GitHub](https://github.com/z-eall/valheim-modzified_cinematics)
