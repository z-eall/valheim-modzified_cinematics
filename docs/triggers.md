# Cinematics and triggers

Full syntax reference for `modzified_cinematics*.yaml` and `modzified_loading_screens*.yaml`. Start from the README's quick steps; come here for every detail.

## Keys

- `name` — a vanilla cinematic's id (to replace it) or a new unique id (to create one). If the same `name` appears twice (same file or across your `modzified_cinematics*.yaml` files), the last one read wins completely — the earlier row is dropped, and the mod logs a warning.
- `enabled` — on/off.
- `type` — which moment triggers it (table below). Omit entirely to keep the vanilla moment — only valid on a vanilla `name`.
- `dream` — `true` queues it for the next time you sleep; omit or `false` plays it immediately.
- `clips` — one or more video file names from `clips/`. Multiple files = one picked at random.
- `oneTime` — fires once, then never again.
- `cooldown` — seconds before it can fire again; overrides that type's own default (see table).

## Trigger types

`type: kind, param1 param2` — one comma after the kind; parameters space-separated. Kinds are case-insensitive.

| `type:` | When |
|---------|------|
| `kill, lasthit <prefab>` | You land the finishing blow |
| `kill, shared <prefab>` | You attacked and are within 60 m of the death |
| `state, sleep` | Sleep cinematic moment |
| `globalKey, <key>` | World key first-set |
| `firstSpawn` | New character world intro |
| `discover, biomeFirst <biome>` | First time you learn that biome |
| `discover, biomeEnter <biome>` | Enter that biome (default 10 min cooldown) |
| `discover, location <match>` | Enter a matching location volume (default 10 min cooldown) |
| `interact, runestone <match>` | Read a matching runestone (default 10 min cooldown) |
| `interact, bossstone <match>` | Trophy hang; you or anyone within 20 m (oneTime on by default) |
| `event, start <name>` | Random event begins |
| `event, end <name>` | Random event ends |
| `teleport, inPortal <tag>` | Walk into a portal with that tag (default 10 min cooldown) |
| `teleport, outPortal <tag>` | Exit to a portal with that tag (default 10 min cooldown) |
| `teleport, pos <x,z,y>` | Teleport destination within 3 m (default 10 min cooldown) |

## Examples

**Replace a vanilla cinematic** — same moment as vanilla, just your own clip. No `type:` needed.

```yaml
- name: $cinematics_intro
  enabled: true
  clips:
  - my_custom_intro.mp4
```

**New cinematic, plays immediately** — a custom trigger fires it the moment the condition is met.

```yaml
- name: my_elder_victory
  enabled: true
  type: globalKey, defeated_gdking
  clips:
  - elder_fanfare.mp4
  oneTime: true
```

**New cinematic, queued for your next sleep** — fires the next time you sleep instead of right away.

```yaml
- name: my_blackforest_intro
  enabled: true
  type: kill, lasthit $enemy_eikthyr
  dream: true
  clips:
  - my_blackforest.mp4
```

**Change when a vanilla cinematic plays, keep its own video** — a custom `type:` with no `clips:` moves the trigger moment; the original vanilla video still plays.

```yaml
- name: $biome_blackforest
  enabled: true
  type: discover, biomeFirst blackforest
  clips:
```

## Loading screens example

```yaml
# Tips — mixed with vanilla tips (turn vanilla off in the .cfg). Empty = vanilla only.
# Shown on the loading, portal, door and logout screens.
loadingTips:
- $my_tip_1
- A plain tip line

# Still art (PNG/JPG) from arts/. One picked at random for loading, portal, door, logout and menu-load. Empty = vanilla screens.
loadingArt:
- my_loading.jpg
- night/camp.jpg
```

Leave a `-` empty (or delete it) to use vanilla. Sleeping, death and cinematic screens stay vanilla; first-spawn scrolling text is always vanilla (use `type: firstSpawn` for a cinematic instead).

## Video export

Valheim can only play `.mp4` with **H.264** video and **AAC** audio. AV1, most MKV/WebM exports, and exotic audio codecs often fail silently.

| Tool | Settings |
|------|----------|
| **HandBrake** | Format: MP4. Video: H.264 (x264). Audio: AAC. |
| **Adobe Premiere** | Export → Match Source / custom. Format: H.264. Video codec: H.264. Audio: AAC. |
| **DaVinci Resolve** | Deliver → MP4. Video codec: H.264. Audio codec: AAC. |
| **CapCut** | Export → MP4. Turn **off** AV1 / "efficient" codecs if offered; pick H.264 + AAC when available. |

If the BepInEx log says playback failed or "no audio tracks," re-export with the table above.
