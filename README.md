# cs2-afk-manager

CS2 ModSharp port of [sm-afk-manager](https://github.com/Radioactive-Gaming/sm-afk-manager) by Radioactive-Gaming / Rothgar.

Detects AFK players and moves them to spectator or kicks them after configurable idle thresholds.

## Credits

Original SourceMod plugin: **sm-afk-manager** by Rothgar, maintained by [Radioactive-Gaming](https://github.com/Radioactive-Gaming/sm-afk-manager).  
This port: yappershq — adapted for CS2 / [ModSharp](https://github.com/Kxnrl/modsharp-public).

## Features

- Idle detection via RunCommand button-state tracking (circular buffer)
- Chat input resets AFK timer
- Move AFK players to spectator after configurable timeout
- Kick AFK players after configurable timeout
- Per-player warning messages before each action
- Spawn-position AFK check (move if player hasn't left spawn after N seconds)
- Admin immunity — full / kick-only / move-only levels
- Minimum player thresholds for move and kick independently
- Announcement to all / admins only / target only
- Admin `!afk_spec` chat command and `afk_spec` server/RCON command
- Locale support (`en-US` via ModSharp LocalizerManager)
- Dead-player exclusion option

## ConVars

| ConVar | Default | Description |
|---|---|---|
| `afk_enabled` | `true` | Plugin master switch |
| `afk_move_spec` | `true` | Move AFK players to spectator |
| `afk_move_time` | `60` | Seconds idle before moving to spectator (0 = disabled) |
| `afk_move_warn_time` | `30` | Seconds before move at which warning fires |
| `afk_move_announce` | `1` | Announce moves: 0 = target only, 1 = everyone, 2 = admins only |
| `afk_move_min_players` | `4` | Minimum human player count to activate move feature |
| `afk_kick_mode` | `1` | 0 = disabled, 1 = all, 2 = active teams only, 3 = spectators only |
| `afk_kick_time` | `120` | Seconds idle before kicking (0 = disabled) |
| `afk_kick_warn_time` | `30` | Seconds before kick at which warning fires |
| `afk_kick_announce` | `1` | Announce kicks: 0 = target only, 1 = everyone, 2 = admins only |
| `afk_kick_min_players` | `6` | Minimum human player count to activate kick feature |
| `afk_spawn_time` | `20` | Seconds after spawn player must begin moving (0 = disabled) |
| `afk_spawn_warn_time` | `15` | Seconds before spawn-move at which warning fires |
| `afk_admins_immune` | `1` | Admin immunity: 0 = none, 1 = full, 2 = kick only, 3 = move only |
| `afk_admins_permission` | `` | Permission required for immunity (empty = any admin) |
| `afk_buttons_buffer` | `5` | Distinct button-state changes to track (0 = disabled) |
| `afk_exclude_dead` | `false` | Skip dead players in AFK checks |

## Commands

| Command | Access | Description |
|---|---|---|
| `!afk_spec <slot\|name>` | Admin (`@afkmanager/spec`) | Move a player to spectator |
| `afk_spec <slot>` | Server/RCON | Move a player to spectator by slot |

## Known Differences from SourceMod Version

- **Mouse-only AFK**: SourceMod detects raw mouse delta (`mouse[0]`, `mouse[1]`). CS2's RunCommand hook does not expose raw mouse movement; idle detection uses button-state changes only. Players who move their mouse without pressing any key will still be marked AFK. In practice on CS2, any mouse look involves subtick movement which triggers key events, so this is rarely a problem.
- **TF2/CSGO/CSS multi-game support**: Not ported — this plugin targets CS2 only.
- **SourceMod API / Natives / Forwards**: Not ported — no equivalent in ModSharp.
- **Log file system**: Logging goes to ModSharp's standard logger instead of daily rotating flat files.
- **Force-language mode**: Removed — ModSharp's LocalizerManager handles per-client language automatically.

## License

MIT — same as the original [sm-afk-manager](https://github.com/Radioactive-Gaming/sm-afk-manager).
