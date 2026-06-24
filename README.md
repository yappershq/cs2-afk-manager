<div align="center">
  <h1><strong>AFK Manager</strong></h1>
  <p>Detects idle players in CS2 and moves them to spectator or kicks them after configurable thresholds.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/license/yappershq/cs2-afk-manager" alt="License">
  <img src="https://img.shields.io/github/stars/yappershq/cs2-afk-manager?style=flat&logo=github" alt="Stars">
</p>

---

A CS2 / [ModSharp](https://github.com/Kxnrl/modsharp-public) port of [sm-afk-manager](https://github.com/Radioactive-Gaming/sm-afk-manager) (Rothgar / Radioactive-Gaming). Tracks per-player input activity and enforces idle thresholds: warn, move to spectator, then kick. Includes a spawn-position check (move players who never leave spawn), per-feature minimum player gates, and admin immunity.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/AfkManager/` | `<sharp>/modules/AfkManager/` |
| `.build/locales/afkmanager.json` | `<sharp>/locales/afkmanager.json` |

Restart the server (or change map) to load. The editable config `<sharp>/configs/afkmanager.cfg` is generated on first run.

All cross-plugin integrations are **optional** — the plugin runs standalone. If [AdminManager](https://github.com/Kxnrl/modsharp-public) is present, `!afk_spec` is permission-gated; if [AdminPanel](https://github.com/yappershq) is present, an in-game "AFK → spec" menu action is registered; LocalizerManager provides per-client localized messages.

## ⌨️ Commands

| Command | Access | Description |
|---------|--------|-------------|
| `!afk_spec <slot\|name>` | Admin (`afkmanager:spec`) | Move a player to spectator. Permission enforced only when AdminManager is installed. |
| `afk_spec <slot>` | Server / RCON | Move a player to spectator by slot. |

## ⚙️ Configuration

`<sharp>/configs/afkmanager.cfg` (auto-generated on first run):

| ConVar | Default | Meaning |
|--------|---------|---------|
| `afk_enabled` | `1` | Plugin master switch |
| `afk_move_spec` | `1` | Move AFK players to spectator |
| `afk_move_time` | `75` | Seconds idle before moving to spectator (0 = disabled) |
| `afk_move_warn_time` | `30` | Seconds before move at which warning fires |
| `afk_move_announce` | `1` | Announce moves: 0 = none, 1 = everyone, 2 = admins only |
| `afk_move_min_players` | `4` | Minimum players required to activate the move feature |
| `afk_kick_mode` | `0` | Who to kick: 0 = disabled, 1 = all, 2 = active teams only, 3 = spectators only |
| `afk_kick_time` | `120` | Seconds idle before kicking (0 = disabled) |
| `afk_kick_warn_time` | `30` | Seconds before kick at which warning fires |
| `afk_kick_announce` | `1` | Announce kicks: 0 = none, 1 = everyone, 2 = admins only |
| `afk_kick_min_players` | `6` | Minimum players required to activate the kick feature |
| `afk_spawn_time` | `45` | Seconds after spawn a player must begin moving (0 = disabled) |
| `afk_spawn_warn_time` | `15` | Seconds before spawn-kick at which warning fires |
| `afk_admins_immune` | `1` | Admin immunity: 0 = none, 1 = full, 2 = kick immunity, 3 = move immunity |
| `afk_admins_permission` | `` | Permission required for immunity (empty = any admin) |
| `afk_buttons_buffer` | `5` | Distinct button-state changes to track before clearing AFK (0 = disabled) |
| `afk_exclude_dead` | `1` | Exclude dead players from AFK checks |

## 🔧 How it works

A `PlayerRunCommand` post-hook inspects each player's button state and view angles every command; chat messages also count as activity. A once-per-second sweep over connected players advances per-player idle timers and fires warnings, the move-to-spectator action, and the kick action as their thresholds are crossed. A separate spawn check moves players who haven't left spawn within `afk_spawn_time`. Admins matching `afk_admins_permission` are skipped according to `afk_admins_immune`.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/AfkManager/AfkManager.dll` and `.build/locales/afkmanager.json`.

## 🙏 Credits

Port of [Radioactive-Gaming/sm-afk-manager](https://github.com/Radioactive-Gaming/sm-afk-manager) (original by Rothgar, maintained by Radioactive-Gaming). MIT licensed, same as upstream.

### Differences from the SourceMod version

- **No raw mouse-delta detection.** ModSharp's RunCommand hook doesn't expose raw mouse movement, so idle detection uses button-state and view-angle changes. In practice CS2 mouse-look produces subtick movement that registers as activity.
- **CS2 only** — the original's TF2/CS:GO/CSS multi-game support is not ported.
- **No SourceMod natives / forwards / flat-file logging** — logging goes to ModSharp's standard logger; localization is handled by LocalizerManager.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
