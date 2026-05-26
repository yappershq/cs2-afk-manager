namespace AfkManager.Modules;

/// <summary>
/// AFK immunity level for a player.
/// Mirrors the SourceMod AFKImmunity enum from sm-afk-manager.
/// </summary>
internal enum AhkImmunity
{
    /// <summary>No immunity — full AFK enforcement applies.</summary>
    None = 0,

    /// <summary>Immune to move-to-spectator only; can still be kicked.</summary>
    MoveImmune = 1,

    /// <summary>Immune to kick only; can still be moved to spectator.</summary>
    KickImmune = 2,

    /// <summary>Fully immune — AFK timer is not even started.</summary>
    Full = 3,
}
