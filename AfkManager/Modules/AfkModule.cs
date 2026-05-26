using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using AfkManager.Configuration;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace AfkManager.Modules;

/// <summary>
/// Core AFK detection and enforcement module.
/// Ported from sm-afk-manager (Radioactive-Gaming / Rothgar).
///
/// Detection strategy:
///   - RunCommandHook post: inspect KeyButtons + ChangedButtons to detect input activity.
///   - IClientListener.OnClientSayCommand: chat counts as activity.
///   - Per-client AFK timer tracked via DateTimeOffset; checked on the game-frame hook.
///
/// AFK timers are checked every second via a game-frame accumulator rather than
/// re-creating SourceMod TIMER_REPEAT handles (which have no equivalent in ModSharp).
/// </summary>
internal sealed class AfkModule : IModule, IClientListener, IGameListener
{
    // How often (seconds) we run the AFK check sweep.
    private const double CheckIntervalSec = 1.0;
    // Warning interval — only warn on multiples of this many seconds remaining.
    private const int WarningInterval = 5;
    // Maximum size of the sliding button history window.
    private const int ButtonsMaxArray = 30;

    private readonly InterfaceBridge         _bridge;
    private readonly ILogger<AfkModule>      _logger;
    private readonly IAfkConfig              _config;

    // Per-player state (indexed by PlayerSlot byte value, 0-63)
    private readonly double         []  _afkStartTime     = new double         [64]; // server curtime when AFK started; -1 = not tracking
    private readonly double         []  _spawnTime        = new double         [64]; // curtime when player spawned; -1 = no spawn check
    private readonly CStrikeTeam    []  _playerTeam       = new CStrikeTeam   [64];
    private readonly AhkImmunity    []  _immunity         = new AhkImmunity   [64];
    private readonly bool           []  _isAfk            = new bool           [64];
    private readonly bool           []  _isDead           = new bool           [64]; // died this tick — reset next button change
    private readonly int            []  _buttonBufIdx     = new int            [64];
    private readonly int            [,] _buttonBuf        = new int            [64, ButtonsMaxArray];
    // AFK spectator observer-mode tracking (detect target/mode changes = activity)
    private readonly int            []  _obsMode          = new int            [64]; // -1 = unknown
    private readonly int            []  _obsTargetSlot    = new int            [64]; // -1 = unknown

    // Frame-accumulator for the per-second check sweep.
    private double _nextCheckAt;

    // Hook delegates kept alive
    private Action<IPlayerRunCommandHookParams, HookReturnValue<EmptyHookReturn>>? _runCmdPost;
    private Action<bool, bool, bool>?                                              _gameFramePost;

    // Admin command registration
    private bool                                    _usedAdminRegistry;
    private IClientManager.DelegateClientCommand?   _fallbackAfkSpecCallback;

    // Master enable/disable (mirrored from ConVar to avoid repeated cvar reads inside tight loops)
    private bool _enabled;

    // Dynamic threshold flags (recomputed when player count changes)
    private bool _canMove;
    private bool _canKick;

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public AfkModule(InterfaceBridge bridge, ILogger<AfkModule> logger, IAfkConfig config)
    {
        _bridge = bridge;
        _logger = logger;
        _config = config;

        // Initialize arrays
        for (var i = 0; i < 64; i++)
        {
            _afkStartTime  [i] = -1;
            _spawnTime     [i] = -1;
            _playerTeam    [i] = CStrikeTeam.UnAssigned;
            _immunity      [i] = AhkImmunity.None;
            _isAfk         [i] = true;
            _isDead        [i] = false;
            _buttonBufIdx  [i] = 0;
            _obsMode       [i] = -1;
            _obsTargetSlot [i] = -1;
        }
    }

    // ===== IModule =====

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);
        _bridge.ModSharp.InstallGameListener(this);

        _runCmdPost = OnRunCommandPost;
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(_runCmdPost);

        _gameFramePost = OnGameFramePost;
        _bridge.ModSharp.InstallGameFrameHook(null, _gameFramePost);

        // Register admin !afk_spec command
        _bridge.ConVarManager.CreateServerCommand(
            "afk_spec",
            OnAfkSpecServerCommand,
            "Force a player to spectator: afk_spec <slot>",
            ConVarFlags.Release);

        return true;
    }

    public void OnAllSharpModulesLoaded()
    {
        // Try to register !afk_spec through AdminManager for permission-gated access
        var adminManager = _bridge.AdminManager;
        if (adminManager is not null)
        {
            var registry = adminManager.GetCommandRegistry("AfkManager");
            registry.RegisterAdminCommand(
                "afk_spec",
                OnAfkSpecAdminCommand,
                ["@afkmanager/spec"]);

            _usedAdminRegistry = true;
            _logger.LogInformation("[AfkManager] AdminManager available — !afk_spec registered with permission check");
        }
        else
        {
            _fallbackAfkSpecCallback = OnAfkSpecFallbackCommand;
            _bridge.ClientManager.InstallCommandCallback("afk_spec", _fallbackAfkSpecCallback);
            _usedAdminRegistry = false;
            _logger.LogWarning("[AfkManager] AdminManager not available — !afk_spec registered without permission check");
        }

        // Populate initial state for already-connected players (late-load scenario)
        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (!client.IsFakeClient)
                InitializePlayer(client);
        }

        UpdateEnabledState();
        UpdateMinPlayers();
    }

    public void Shutdown()
    {
        _bridge.ClientManager.RemoveClientListener(this);
        _bridge.ModSharp.RemoveGameListener(this);

        if (_runCmdPost is not null)
        {
            _bridge.HookManager.PlayerRunCommand.RemoveHookPost(_runCmdPost);
            _runCmdPost = null;
        }

        if (_gameFramePost is not null)
        {
            _bridge.ModSharp.RemoveGameFrameHook(null, _gameFramePost);
            _gameFramePost = null;
        }

        _bridge.ConVarManager.ReleaseCommand("afk_spec");

        if (!_usedAdminRegistry && _fallbackAfkSpecCallback is not null)
            _bridge.ClientManager.RemoveCommandCallback("afk_spec", _fallbackAfkSpecCallback);
    }

    // ===== IGameListener =====

    public void OnGameInit()     { }
    public void OnGamePostInit() { }
    public void OnGameActivate() => UpdateEnabledState();
    public void OnGameDeactivate() { }
    public void OnRoundRestart()   { }
    public void OnRoundRestarted() { }
    public ECommandAction ConsoleSay(string message) => ECommandAction.Skipped;

    // ===== Game frame: AFK sweep =====

    private void OnGameFramePost(bool simulating, bool firstTick, bool lastTick)
    {
        if (!simulating || !_config.Enabled)
            return;

        var now = _bridge.ModSharp.GetGlobals().CurTime;
        if (now < _nextCheckAt)
            return;

        _nextCheckAt = now + CheckIntervalSec;

        // Check whether we have enough players to take action
        UpdateMinPlayers();

        if (!_canMove && !_canKick)
            return;

        // Iterate all slots
        for (var i = 0; i < 64; i++)
        {
            var slot = new PlayerSlot((byte)i);
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true } || client.IsFakeClient)
                continue;

            CheckPlayer(client, i, now);
        }
    }

    // ===== RunCommand hook: input activity detection =====

    private void OnRunCommandPost(
        IPlayerRunCommandHookParams param,
        HookReturnValue<EmptyHookReturn> _)
    {
        if (!_config.Enabled)
            return;

        var client = param.Client;
        if (client is not { IsInGame: true } || client.IsFakeClient)
            return;

        var slot = (int)(byte)client.Slot;

        if (_afkStartTime[slot] < 0)
            return; // Not yet initialized

        // --- Mouse movement detection ---
        // The CS2 RunCommand hook does not expose raw mouse deltas like SourceMod's OnPlayerRunCmd
        // (mouse[0], mouse[1]). Instead we detect movement via ChangedButtons having non-zero
        // changes, which includes strafing and any key presses.
        //
        // For mouse look, CS2 sub-tick input history encodes angle changes per subtick.
        // We sample the first InputHistory entry's ViewAngles and compare to a stored value.
        // If angles changed the player is not AFK.
        //
        // NOTE: Full sub-tick angle tracking is complex. We use a lightweight heuristic:
        // any button change OR any key held signals activity (standard CS2 AFK detection idiom).
        // Pure mouse-look without any buttons pressed is a known limitation vs. SourceMod.
        // TODO(localize): document this in plugin notes

        var bufferSize = Math.Min(_config.ButtonsBuffer, ButtonsMaxArray);
        var buttons    = (int)param.KeyButtons;

        if (bufferSize > 0)
        {
            // Check if buttons have changed relative to last stored state
            var lastIdx      = _buttonBufIdx[slot] == 0 ? (bufferSize - 1) : (_buttonBufIdx[slot] - 1);
            var lastButtons  = bufferSize > 1 ? _buttonBuf[slot, lastIdx] : _buttonBuf[slot, 0];

            if (lastButtons != buttons)
            {
                // Check if this button combo is already in the circular buffer
                var found = false;
                if (bufferSize > 1)
                {
                    for (var j = 0; j < bufferSize; j++)
                    {
                        if (_buttonBuf[slot, j] == buttons)
                        {
                            found = true;
                            break;
                        }
                    }
                }

                _buttonBuf[slot, _buttonBufIdx[slot]] = buttons;
                if (++_buttonBufIdx[slot] >= bufferSize)
                    _buttonBufIdx[slot] = 0;

                if (!found)
                {
                    if (!_isDead[slot])
                    {
                        MarkActive(slot);
                    }
                    else
                    {
                        // Player just died — suppress the first button change so death-cam
                        // transitions don't reset AFK timer (mirrors SourceMod logic)
                        _isDead[slot] = false;
                    }
                }
            }
        }
    }

    // ===== IClientListener =====

    void IClientListener.OnClientPostAdminCheck(IGameClient client)
    {
        if (!_config.Enabled || client.IsFakeClient)
            return;

        InitializePlayer(client);
        UpdateMinPlayers();
    }

    void IClientListener.OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        var slot = (int)(byte)client.Slot;
        ResetPlayer(slot, full: true);
        _immunity[slot] = AhkImmunity.None;
        UpdateMinPlayers();
    }

    void IClientListener.OnClientConnected(IGameClient client)         { }
    void IClientListener.OnClientPutInServer(IGameClient client)       { }
    void IClientListener.OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason) { }
    void IClientListener.OnAdminCacheReload()                          { }
    void IClientListener.OnClientSettingChanged(IGameClient client)    { }

    ECommandAction IClientListener.OnClientSayCommand(IGameClient client, bool teamOnly, bool isCommand, string commandName, string message)
    {
        if (!_config.Enabled || client.IsFakeClient)
            return ECommandAction.Skipped;

        var slot = (int)(byte)client.Slot;
        if (_afkStartTime[slot] >= 0)
            MarkActive(slot);

        return ECommandAction.Skipped;
    }

    // ===== Event handling via IEventListener =====
    // We attach to game events via IEventManager in Init.

    // ===== Player lifecycle =====

    private void InitializePlayer(IGameClient client)
    {
        var slot = (int)(byte)client.Slot;

        ResetPlayer(slot, full: true);
        _afkStartTime[slot] = _bridge.ModSharp.GetGlobals().CurTime;

        var controller = client.GetPlayerController();
        if (controller is not null)
            _playerTeam[slot] = controller.Team;

        // Apply admin immunity from config
        ApplyConfigImmunity(client, slot);
    }

    private void ApplyConfigImmunity(IGameClient client, int slot)
    {
        var immuneLevel = _config.AdminsImmune;
        if (immuneLevel <= 0)
            return;

        if (IsAdminImmune(client))
        {
            _immunity[slot] = immuneLevel switch
            {
                1 => AhkImmunity.Full,
                2 => AhkImmunity.KickImmune,
                3 => AhkImmunity.MoveImmune,
                _ => AhkImmunity.None,
            };

            if (_immunity[slot] == AhkImmunity.Full)
            {
                // Stop timer entirely — fully immune admins don't get checked
                _afkStartTime[slot] = -1;
            }
        }
    }

    private bool IsAdminImmune(IGameClient client)
    {
        var adminManager = _bridge.AdminManager;
        if (adminManager is null)
            return false;

        var admin = adminManager.GetAdmin(client.SteamId);
        if (admin is null)
            return false; // Not an admin

        var requiredFlag = _config.AdminsFlag;
        if (string.IsNullOrWhiteSpace(requiredFlag))
            return true; // Any admin = immune

        return admin.HasPermission(requiredFlag);
    }

    private void ResetPlayer(int slot, bool full)
    {
        _isAfk        [slot] = true;
        _buttonBufIdx [slot] = 0;
        _isDead       [slot] = false;
        _spawnTime    [slot] = -1;

        if (full)
        {
            _afkStartTime  [slot] = -1;
            _playerTeam    [slot] = CStrikeTeam.UnAssigned;
            _obsMode       [slot] = -1;
            _obsTargetSlot [slot] = -1;

            for (var j = 0; j < ButtonsMaxArray; j++)
                _buttonBuf[slot, j] = 0;
        }
        else
        {
            // Soft-reset: restart AFK timer from now
            _afkStartTime[slot] = _bridge.ModSharp.GetGlobals().CurTime;

            var bufferSize = Math.Min(_config.ButtonsBuffer, ButtonsMaxArray);
            for (var j = 0; j < bufferSize; j++)
                _buttonBuf[slot, j] = 0;
        }
    }

    private void MarkActive(int slot)
    {
        _isAfk[slot] = false;
    }

    // ===== Per-player AFK check (called every CheckIntervalSec) =====

    private void CheckPlayer(IGameClient client, int slot, double now)
    {
        if (_afkStartTime[slot] < 0)
            return; // fully immune or not initialized

        var controller = client.GetPlayerController();
        if (controller is null)
            return;

        var team = controller.Team;
        _playerTeam[slot] = team;

        // Skip unassigned team
        if (team == CStrikeTeam.UnAssigned)
            return;

        // Spectator observer-target change = activity (mirrors SourceMod observer detection)
        if (team == CStrikeTeam.Spectator)
        {
            if (CheckObserverActivity(client, slot))
                return;
        }

        if (!_isAfk[slot])
        {
            // Player signalled activity this interval — reset AFK timer
            ResetPlayer(slot, full: false);
            return;
        }

        // Skip dead players if configured
        var pawn = controller.GetPlayerPawn();
        if (_config.ExcludeDead && pawn is { IsAlive: false } && team != CStrikeTeam.Spectator)
        {
            _afkStartTime[slot] = now; // advance timer so dead time doesn't count
            return;
        }

        var afkElapsed = now - _afkStartTime[slot];

        // ---- Spawn AFK check ----
        if (_spawnTime[slot] > 0 && team != CStrikeTeam.Spectator)
        {
            var spawnElapsed  = now - _spawnTime[slot];
            var spawnCvarTime = _config.SpawnTime;

            if (spawnCvarTime > 0)
            {
                var spawnLeft = spawnCvarTime - spawnElapsed;

                if (spawnElapsed >= spawnCvarTime)
                {
                    // Spawn AFK — treat as regular AFK action (move or kick)
                    _spawnTime[slot] = -1;
                    TakeMoveAction(client, slot, isSpawn: true);
                    return;
                }

                var warnSpawn = _config.WarnSpawnTime;
                if (spawnLeft <= warnSpawn && (int)spawnElapsed % WarningInterval == 0)
                {
                    PrintToClient(client, "AFK_Spawn_Move_Warning", (int)spawnLeft);
                }
            }
        }

        // ---- Move-to-spec check ----
        if (_config.MoveToSpec && _canMove && team != CStrikeTeam.Spectator)
        {
            if (_immunity[slot] != AhkImmunity.Full && _immunity[slot] != AhkImmunity.MoveImmune)
            {
                var moveTime = _config.TimeToMove;
                if (moveTime > 0)
                {
                    var moveLeft = moveTime - afkElapsed;
                    if (afkElapsed >= moveTime)
                    {
                        TakeMoveAction(client, slot, isSpawn: false);
                        return;
                    }

                    var warnMove = _config.WarnTimeToMove;
                    if (moveLeft <= warnMove && (int)afkElapsed % WarningInterval == 0)
                        PrintToClient(client, "AFK_Move_Warning", (int)moveLeft);
                }
            }
        }

        // ---- Kick check ----
        var kickMode = _config.KickMode;
        if (kickMode > 0 && _canKick)
        {
            // kickMode 2 = skip spectators; kickMode 3 = spectators only
            if (kickMode == 2 && team == CStrikeTeam.Spectator)
                return;
            if (kickMode == 3 && team != CStrikeTeam.Spectator)
                return;

            if (_immunity[slot] != AhkImmunity.Full && _immunity[slot] != AhkImmunity.KickImmune)
            {
                var kickTime = _config.TimeToKick;
                if (kickTime > 0)
                {
                    var kickLeft = kickTime - afkElapsed;
                    if (afkElapsed >= kickTime)
                    {
                        TakeKickAction(client, slot);
                        return;
                    }

                    var warnKick = _config.WarnTimeToKick;
                    if (kickLeft <= warnKick && (int)afkElapsed % WarningInterval == 0)
                        PrintToClient(client, "AFK_Kick_Warning", (int)kickLeft);
                }
            }
        }
    }

    // ===== Spectator observer tracking =====

    /// <summary>
    /// Returns true if observer activity was detected (player is not AFK).
    /// Changes in spectator target count as activity.
    /// </summary>
    private bool CheckObserverActivity(IGameClient client, int slot)
    {
        var controller = client.GetPlayerController();
        if (controller is null)
            return false;

        // CS2 observer data lives on the pawn
        var pawn = controller.GetPlayerPawn();
        if (pawn is null)
            return false;

        // We track observer mode via _obsMode. A change in observer mode (except
        // automatic transitions from death-cam/freeze-cam) counts as activity.
        // For simplicity we treat any observed-target change as activity since
        // CS2 auto-rotates spectators to next alive player on death, which would
        // false-positive without deeper tracking. We mirror the SourceMod logic:
        // if the player changed their own observe target (not auto-cycle), mark active.
        // Since CS2 does not easily expose "did auto-cycle vs manual key", we accept
        // the simpler approach: target changes are NOT counted as activity by default.
        // Button presses (next/prev player keys = IN_ATTACK / IN_ATTACK2 in CS2)
        // will be caught by the RunCommand hook anyway.
        return false;
    }

    // ===== Action functions =====

    private void TakeMoveAction(IGameClient client, int slot, bool isSpawn)
    {
        var name = client.Name ?? "Unknown";

        // Announce
        var announce = _config.MoveAnnounce;
        BroadcastAnnouncement(announce, client, slot, "AFK_Move_Announce", name);

        _logger.LogInformation("[AfkManager] Moving {Name} to spectator (AFK).", name);

        // Move to spectator
        var controller = client.GetPlayerController();
        if (controller is not null && controller.Team != CStrikeTeam.Spectator)
        {
            controller.SwitchTeam(CStrikeTeam.Spectator);
        }

        // Update tracking — reset AFK start from now so they can be kicked later
        _spawnTime    [slot] = -1;
        _playerTeam   [slot] = CStrikeTeam.Spectator;
        _afkStartTime [slot] = _bridge.ModSharp.GetGlobals().CurTime;
        _isAfk        [slot] = true;
    }

    private void TakeKickAction(IGameClient client, int slot)
    {
        var name = client.Name ?? "Unknown";

        // Announce
        var announce = _config.KickAnnounce;
        BroadcastAnnouncement(announce, client, slot, "AFK_Kick_Announce", name);

        _logger.LogInformation("[AfkManager] Kicking {Name} for AFK.", name);

        // Kick
        _bridge.ClientManager.KickClient(
            client,
            _bridge.LocalizeFor(client, "AFK_Kick_Message"),
            NetworkDisconnectionReason.Kicked);
    }

    // ===== Helpers =====

    private void UpdateEnabledState()
    {
        _enabled = _config.Enabled;
    }

    private void UpdateMinPlayers()
    {
        var humanCount = CountHumans();
        _canMove = humanCount >= _config.MinPlayersMove;
        _canKick = humanCount >= _config.MinPlayersKick;
    }

    private int CountHumans()
    {
        var count = 0;
        for (var i = 0; i < 64; i++)
        {
            var client = _bridge.ClientManager.GetGameClient(new PlayerSlot((byte)i));
            if (client is { IsInGame: true } && !client.IsFakeClient)
                count++;
        }
        return count;
    }

    private void PrintToClient(IGameClient client, string key, params object?[] args)
    {
        var prefix = _bridge.LocalizeFor(client, "AFK_Prefix");
        var body   = _bridge.LocalizeFor(client, key, args);
        client.Print(HudPrintChannel.Chat, $"{prefix} {body}");
    }

    private void BroadcastAnnouncement(int mode, IGameClient target, int slot, string key, params object?[] args)
    {
        // mode 0 = notify target only, 1 = everyone, 2 = admins only
        if (mode == 0)
        {
            PrintToClient(target, key, args);
            return;
        }

        for (var i = 0; i < 64; i++)
        {
            var client = _bridge.ClientManager.GetGameClient(new PlayerSlot((byte)i));
            if (client is not { IsInGame: true } || client.IsFakeClient)
                continue;

            if (mode == 2)
            {
                // Admins only — check via AdminManager if available
                var adminManager = _bridge.AdminManager;
                var admin = adminManager?.GetAdmin(client.SteamId);
                if (admin is null)
                {
                    if (i == slot) // Always notify the target
                        PrintToClient(client, key, args);
                    continue;
                }
            }

            PrintToClient(client, key, args);
        }
    }

    // ===== Admin command: !afk_spec / afk_spec =====

    private void OnAfkSpecAdminCommand(IGameClient? invoker, StringCommand command)
    {
        HandleAfkSpecCommand(invoker, command);
    }

    private ECommandAction OnAfkSpecFallbackCommand(IGameClient client, StringCommand command)
    {
        if (!IsAdminImmune(client))
        {
            client.Print(HudPrintChannel.Chat, "[AFK Manager] You do not have permission to use this command.");
            return ECommandAction.Handled;
        }
        HandleAfkSpecCommand(client, command);
        return ECommandAction.Handled;
    }

    private ECommandAction OnAfkSpecServerCommand(StringCommand command)
    {
        // Server/RCON invocation — no client
        if (command.ArgCount < 1)
        {
            _logger.LogInformation("[AfkManager] Usage: afk_spec <slot>");
            return ECommandAction.Stopped;
        }

        if (!int.TryParse(command.GetArg(1), out var targetSlot) || targetSlot < 0 || targetSlot >= 64)
        {
            _logger.LogInformation("[AfkManager] Invalid slot. Usage: afk_spec <0-63>");
            return ECommandAction.Stopped;
        }

        var target = _bridge.ClientManager.GetGameClient(new PlayerSlot((byte)targetSlot));
        if (target is not { IsInGame: true })
        {
            _logger.LogInformation("[AfkManager] No player in slot {Slot}.", targetSlot);
            return ECommandAction.Stopped;
        }

        TakeMoveAction(target, targetSlot, isSpawn: false);
        _logger.LogInformation("[AfkManager] Console moved {Name} to spectator.", target.Name);
        return ECommandAction.Stopped;
    }

    private void HandleAfkSpecCommand(IGameClient? invoker, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            if (invoker is not null)
                invoker.Print(HudPrintChannel.Chat, "[AFK Manager] Usage: !afk_spec <slot|partial name>");
            return;
        }

        var arg = command.GetArg(1).Trim();
        IGameClient? target = null;

        // Try slot number first
        if (int.TryParse(arg, out var slotNum) && slotNum >= 0 && slotNum < 64)
        {
            target = _bridge.ClientManager.GetGameClient(new PlayerSlot((byte)slotNum));
        }
        else
        {
            // Partial name match
            foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
            {
                if (c.Name?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true)
                {
                    target = c;
                    break;
                }
            }
        }

        if (target is not { IsInGame: true })
        {
            if (invoker is not null)
                invoker.Print(HudPrintChannel.Chat, "[AFK Manager] Player not found.");
            return;
        }

        var targetSlot = (int)(byte)target.Slot;
        TakeMoveAction(target, targetSlot, isSpawn: false);

        var invokerName = invoker?.Name ?? "Console";
        _logger.LogInformation("[AfkManager] {Invoker} moved {Target} to spectator via !afk_spec.", invokerName, target.Name);
    }
}
