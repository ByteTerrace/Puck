namespace Puck.Commands;

/// <summary>
/// A <see cref="BindingPageEntryDefinition"/>'s activation lifecycle for a HELD digital control — the primitive a
/// player rebinds independently of which command or channel the entry targets.
/// </summary>
/// <remarks>
/// <see cref="Hold"/> is today's behavior, byte-identical: the destination is active exactly while the physical
/// control is held, and releases the instant it releases. <see cref="Toggle"/> is an INPUT-SIDE latch — the first
/// press flips the destination active and it STAYS active (re-asserted every tick, exactly like a physical hold)
/// until a second press flips it back off; the physical release in between is ignored entirely. The simulation
/// never learns the difference: a toggled-on channel reads held, exactly as a physically-held one would, because
/// the latch lives here, in the input/compose layer (<see cref="InputRouter"/>), never downstream. Only meaningful
/// on a CHANNEL destination (a dispatched command has no "held" reading for a toggle to replace) —
/// <see cref="BindingProfile.Compile"/> refuses <see cref="Toggle"/> on a command destination.
/// Author-facing auto-actions such as autorun and auto-jetpack are therefore ordinary channel bindings using
/// <see cref="Toggle"/>, not bespoke commands or simulation state.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<BindingEntryMode>))]
public enum BindingEntryMode {
    /// <summary>Active exactly while the physical control is held.</summary>
    Hold,

    /// <summary>A press flips a latch; the destination reads held until a second press flips it back.</summary>
    Toggle,
}
