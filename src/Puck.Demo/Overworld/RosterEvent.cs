namespace Puck.Demo.Overworld;

/// <summary>Whether a roster change adds or removes a player.</summary>
public enum RosterEventKind : byte {
    /// <summary>A player joined (a controller connected).</summary>
    Join = 1,
    /// <summary>A player left (a controller disconnected).</summary>
    Leave = 2,
}

/// <summary>
/// A roster mutation applied at the START of a tick, before that tick's intents — a player joining or leaving. Roster
/// events ride their OWN channel (separate from the intent stream) so the simulation stays a pure function of
/// <c>(seed, intents, roster events)</c>; recording + replaying both channels reproduces a session through join, leave,
/// and slot recycling. <see cref="Slot"/> is the dynamic-transform slot the event resolved to — informational for a
/// join (replay re-derives it and cross-checks; a mismatch is a desync tripwire), identifying for a leave.
/// </summary>
public readonly record struct RosterEvent(RosterEventKind Kind, Guid PlayerId, int Slot);
