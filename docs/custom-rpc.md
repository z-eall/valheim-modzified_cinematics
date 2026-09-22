# Calling a cinematic from another tool

A custom-made RPC that you can call to play custom cinematics outside this mod.

## RPC

```yaml
clientRpc:
- name: RPC_PlayModzifiedCinematics
  target: <picked by the calling tool>
  1: string, <entry's name>
```

- `name` — fixed, always `RPC_PlayModzifiedCinematics`.
- `target` — who watches it; picked by whichever tool is calling it, not this mod. Accepted values (Expand World Prefabs):
  - `all` — everyone on the server.
  - `owner` — only the player whose action triggered this entry.
  - `<zdo>` — only the player with that specific ZDO id.
- `1` — the `name:` of a `type: clientRpc` entry in `modzified_cinematics.yaml`.

## How to use

1. Write a normal entry in `modzified_cinematics.yaml`, but set `type: clientRpc`. It never plays on its own — only when called by name.
2. Call it with the fixed RPC name and the entry's name as the one parameter (see RPC shape above).

## Examples

```yaml
# modzified_cinematics.yaml
- name: my_shrine_cinematic
  type: clientRpc
  clips:
  - shrine_reveal.mp4
```

One tool that can call it is [Expand World Prefabs](https://github.com/JereKuusela/valheim-expand_world_prefabs/):

```yaml
# Expand World Prefabs script
# This entry makes this Player say 'watch' and watch a video
- prefab: Player
  type: say, watch
  clientRpc:
  - name: RPC_PlayModzifiedCinematics
    target: <zdo> # Referring to this triggering player's ZDO id
    1: string, my_shrine_cinematic
```
