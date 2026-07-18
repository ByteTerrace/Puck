namespace Puck.HumbleGamingBrick;

/// <summary>
/// The far end of a serial cable a <see cref="SerialComponent"/> driving its INTERNAL clock exchanges bits with. On each
/// shift the master hands its outgoing bit in and reads this peer's outgoing bit back, mirroring the simultaneous
/// exchange of the two hardware shift registers — the one method a linked port (another <see cref="SerialComponent"/>),
/// a modeled link device (the link-cable printer, <see cref="GamePrinterDevice"/>), or any future serial slave
/// implements.
/// It is host wiring, never emulated state: the peer reference is not serialized, so attaching a peer cannot perturb
/// determinism, and each side's transfer progress lives in its own snapshotted fields.
/// </summary>
internal interface ISerialPeer {
    /// <summary>Exchanges one serial bit clocked by the master's internal clock: shifts <paramref name="incoming"/> into
    /// this peer's shift register and returns the bit this peer drives back onto the line (its shift register's MSB).</summary>
    /// <param name="incoming">The bit the master is shifting out this edge.</param>
    /// <returns>This peer's outgoing bit.</returns>
    bool ShiftBit(bool incoming);
}
