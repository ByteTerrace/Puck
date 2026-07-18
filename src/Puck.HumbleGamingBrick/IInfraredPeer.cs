namespace Puck.HumbleGamingBrick;

/// <summary>
/// The far end of an infrared link a machine's <see cref="InfraredPort"/> reads its received light from — the IR analogue
/// of <see cref="ISerialPeer"/>. Where the serial peer exchanges one clocked bit, the IR medium carries a light LEVEL:
/// this peer simply exposes whether its LED is emitting right now, and the reader samples it. It is host wiring, never
/// emulated state — the peer reference is not serialized, so attaching a peer cannot perturb determinism, and each side's
/// own LED/register state lives in its own snapshotted fields.
/// </summary>
internal interface IInfraredPeer {
    /// <summary>Gets whether this peer's IR LED is currently emitting light (its RP LED bit or a cart IR-mode LED
    /// write) — the level the linked reader detects on its received-light line.</summary>
    bool EmittedLight { get; }
}
