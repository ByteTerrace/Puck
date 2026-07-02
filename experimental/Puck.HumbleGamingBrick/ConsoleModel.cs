namespace Puck.HumbleGamingBrick;

/// <summary>The hardware model a machine emulates, which selects its boot behaviour, register handoff state, and the
/// availability of Color features.</summary>
public enum ConsoleModel {
    /// <summary>The original monochrome Game Boy (DMG).</summary>
    Dmg = 0,
    /// <summary>The Game Boy Color (CGB).</summary>
    Cgb = 1,
}
