using Microsoft.Extensions.Logging;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace AfkManager.Configuration;

internal interface IAfkConfig
{
    /// <summary>Plugin master switch.</summary>
    bool Enabled { get; }

    /// <summary>Seconds a player must be idle before being moved to spectator. 0 = disabled.</summary>
    int TimeToMove { get; }

    /// <summary>Seconds remaining before move at which a warning is shown.</summary>
    int WarnTimeToMove { get; }

    /// <summary>
    /// Whether AFK players should be moved to spectator.
    /// </summary>
    bool MoveToSpec { get; }

    /// <summary>
    /// Announce AFK moves: 0 = no, 1 = everyone, 2 = admins only.
    /// </summary>
    int MoveAnnounce { get; }

    /// <summary>Seconds a player must be idle before being kicked. 0 = disabled.</summary>
    int TimeToKick { get; }

    /// <summary>Seconds remaining before kick at which a warning is shown.</summary>
    int WarnTimeToKick { get; }

    /// <summary>
    /// Who to kick for AFK: 0 = disabled, 1 = all, 2 = active teams only (not spec), 3 = spectators only.
    /// </summary>
    int KickMode { get; }

    /// <summary>
    /// Announce AFK kicks: 0 = no, 1 = everyone, 2 = admins only.
    /// </summary>
    int KickAnnounce { get; }

    /// <summary>Minimum human player count before the move feature activates.</summary>
    int MinPlayersMove { get; }

    /// <summary>Minimum human player count before the kick feature activates.</summary>
    int MinPlayersKick { get; }

    /// <summary>
    /// Admin immunity level: 0 = none, 1 = full, 2 = kick immunity, 3 = move immunity.
    /// </summary>
    int AdminsImmune { get; }

    /// <summary>Permission flag required for immunity. Empty = any admin.</summary>
    string AdminsPermission { get; }

    /// <summary>
    /// How many distinct button-state changes to track before clearing AFK status.
    /// 0 = button tracking disabled (mouse-only detection).
    /// </summary>
    int ButtonsBuffer { get; }

    /// <summary>Exclude dead players from AFK checks.</summary>
    bool ExcludeDead { get; }

    /// <summary>
    /// Time in seconds after spawn that a player must begin moving.
    /// 0 = spawn AFK check disabled.
    /// </summary>
    int SpawnTime { get; }

    /// <summary>Seconds remaining before spawn-kick at which a warning is shown.</summary>
    int WarnSpawnTime { get; }
}

internal sealed class AfkConfig : IAfkConfig
{
    private readonly IConVar? _cvEnabled;
    private readonly IConVar? _cvTimeToMove;
    private readonly IConVar? _cvWarnTimeToMove;
    private readonly IConVar? _cvMoveToSpec;
    private readonly IConVar? _cvMoveAnnounce;
    private readonly IConVar? _cvTimeToKick;
    private readonly IConVar? _cvWarnTimeToKick;
    private readonly IConVar? _cvKickMode;
    private readonly IConVar? _cvKickAnnounce;
    private readonly IConVar? _cvMinPlayersMove;
    private readonly IConVar? _cvMinPlayersKick;
    private readonly IConVar? _cvAdminsImmune;
    private readonly IConVar? _cvAdminsPermission;
    private readonly IConVar? _cvButtonsBuffer;
    private readonly IConVar? _cvExcludeDead;
    private readonly IConVar? _cvSpawnTime;
    private readonly IConVar? _cvWarnSpawnTime;

    public AfkConfig(InterfaceBridge bridge)
    {
        var cv = bridge.ConVarManager;

        _cvEnabled        = cv.CreateConVar("afk_enabled",          true,  "Enable AFK Manager [0=off, 1=on]");
        _cvTimeToMove     = cv.CreateConVar("afk_move_time",        60,    "Seconds idle before moving to spectator (0=disabled)");
        _cvWarnTimeToMove = cv.CreateConVar("afk_move_warn_time",   30,    "Seconds before move at which warning fires");
        _cvMoveToSpec     = cv.CreateConVar("afk_move_spec",        true,  "Move AFK players to spectator [0=off, 1=on]");
        _cvMoveAnnounce   = cv.CreateConVar("afk_move_announce",    1,     "Announce AFK moves: 0=none, 1=everyone, 2=admins only");
        _cvTimeToKick     = cv.CreateConVar("afk_kick_time",        120,   "Seconds idle before kicking (0=disabled)");
        _cvWarnTimeToKick = cv.CreateConVar("afk_kick_warn_time",   30,    "Seconds before kick at which warning fires");
        _cvKickMode       = cv.CreateConVar("afk_kick_mode",        0,     "Who to kick: 0=disabled (default), 1=all, 2=active teams only (skip spectators), 3=spectators only");
        _cvKickAnnounce   = cv.CreateConVar("afk_kick_announce",    1,     "Announce AFK kicks: 0=none, 1=everyone, 2=admins only");
        _cvMinPlayersMove = cv.CreateConVar("afk_move_min_players", 4,     "Minimum players required for move feature");
        _cvMinPlayersKick = cv.CreateConVar("afk_kick_min_players", 6,     "Minimum players required for kick feature");
        _cvAdminsImmune   = cv.CreateConVar("afk_admins_immune",    1,     "Admin immunity: 0=none, 1=full, 2=kick immunity, 3=move immunity");
        _cvAdminsPermission = cv.CreateConVar("afk_admins_permission", "",    "Admin permission required for AFK immunity (empty = any admin). IAdmin.HasPermission style: @afkmanager/immune, admin:slay etc.");
        _cvButtonsBuffer  = cv.CreateConVar("afk_buttons_buffer",   5,     "Distinct button-state changes to track before clearing AFK (0=disabled)");
        _cvExcludeDead    = cv.CreateConVar("afk_exclude_dead",     true,  "Exclude dead players from AFK checks");
        _cvSpawnTime      = cv.CreateConVar("afk_spawn_time",       45,    "Seconds after spawn a player must begin moving (0=disabled)");
        _cvWarnSpawnTime  = cv.CreateConVar("afk_spawn_warn_time",  15,    "Seconds before spawn-kick at which warning fires (warns every 5s during this window)");

        // Generate/load editable config at sharp/configs/afkmanager.cfg (NukoLevelRank style).
        var logger = bridge.LoggerFactory.CreateLogger("AfkManager.Config");
        IConVar?[] all = [_cvEnabled, _cvTimeToMove, _cvWarnTimeToMove, _cvMoveToSpec, _cvMoveAnnounce,
                          _cvTimeToKick, _cvWarnTimeToKick, _cvKickMode, _cvKickAnnounce,
                          _cvMinPlayersMove, _cvMinPlayersKick, _cvAdminsImmune, _cvAdminsPermission,
                          _cvButtonsBuffer, _cvExcludeDead, _cvSpawnTime, _cvWarnSpawnTime];
        ConVarConfigFile.Sync(bridge.SharpPath, "afkmanager.cfg", "AfkManager", logger,
            System.Array.FindAll(all, c => c is not null)!);
    }

    public bool   Enabled        => _cvEnabled?.GetBool()     ?? true;
    public int    TimeToMove     => _cvTimeToMove?.GetInt32() ?? 60;
    public int    WarnTimeToMove => _cvWarnTimeToMove?.GetInt32() ?? 30;
    public bool   MoveToSpec     => _cvMoveToSpec?.GetBool()  ?? true;
    public int    MoveAnnounce   => _cvMoveAnnounce?.GetInt32() ?? 1;
    public int    TimeToKick     => _cvTimeToKick?.GetInt32() ?? 120;
    public int    WarnTimeToKick => _cvWarnTimeToKick?.GetInt32() ?? 30;
    public int    KickMode       => _cvKickMode?.GetInt32()   ?? 0;
    public int    KickAnnounce   => _cvKickAnnounce?.GetInt32() ?? 1;
    public int    MinPlayersMove => _cvMinPlayersMove?.GetInt32() ?? 4;
    public int    MinPlayersKick => _cvMinPlayersKick?.GetInt32() ?? 6;
    public int    AdminsImmune   => _cvAdminsImmune?.GetInt32() ?? 1;
    public string AdminsPermission     => _cvAdminsPermission?.GetString() ?? "";
    public int    ButtonsBuffer  => _cvButtonsBuffer?.GetInt32() ?? 5;
    public bool   ExcludeDead    => _cvExcludeDead?.GetBool()  ?? true;
    public int    SpawnTime      => _cvSpawnTime?.GetInt32()   ?? 45;
    public int    WarnSpawnTime  => _cvWarnSpawnTime?.GetInt32() ?? 15;
}
