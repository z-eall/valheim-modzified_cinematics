# Modzified_Cinematics

[![Find me here](https://img.shields.io/badge/Find_me_here-Discord-5865F2?logo=discord&logoColor=white&style=flat)](https://discord.gg/VFRJcPwUdm)
[![Support](https://img.shields.io/badge/Support-Ko--fi-FF5E5B?logo=ko-fi&logoColor=white&style=flat)](https://ko-fi.com/zeall)

Turn vanilla cinematics on or off, swap in your own clips, or add new cinematics that play on moments like kills, sleep, or discovering a biome. Loading, portal, door and logout screens get tips and background art too. Part of Zeall's **Modzified** series.

Install on all clients and the server. Dependency required: **YamlDotNet**.

## Features

- Turn each cinematic on or off, or swap in your own clip.
- Add brand new cinematics that play on kills, sleep, discoveries, portals, and more.
- Loading, portal, door and logout screens show tips and a background picture — pick your own or keep vanilla.

## How to use

1. Install the plugin on every client and the server (plus YamlDotNet).
2. Launch once. The mod creates, under `BepInEx/config/modzified_cinematics/`:
   - `modzified_cinematics.yaml` — which cinematics play, and when
   - `modzified_loading_screens.yaml` — loading tips and art
   - `clips/` and `arts/` folders — put your video and picture files here
3. Open a `ref_*` file for an example row, copy it into the matching `modzified_*.yaml`, point it at a file you added, and save. Changes apply live on save — no restart needed.

Full list of moments a cinematic can play on, YAML syntax, and video export help: **[docs/triggers.md](docs/triggers.md)**.

## Configuration

File: `BepInEx/config/modzified_cinematics.cfg`.

- **Skip custom cinematics** — fall back to fully vanilla on this client only. Default off.
- **Art dim** — how dark the loading picture gets. Default `0.4`.
- **Art zoom max** / **Art zoom seconds** — how much and how fast the loading picture drifts.
- **Include vanilla tips** — mix your tips with vanilla's, or show only yours. Default on.

## Credits

Source: [<img src="https://cdn.simpleicons.org/github/181717" width="16" height="16" alt="" /> GitHub](https://github.com/z-eall/valheim-modzified_cinematics)
