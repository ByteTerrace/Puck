namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The physical link-cable transport between Advanced GamingBrick consoles. A <see cref="AgbSerialController"/> drives
/// all of its serial I/O through this seam, so two or more in-process <c>AdvancedGamingBrickMachine</c> instances can be
/// wired together, including a transport-backed implementation, without the SIO subsystem knowing how the wire is
/// realised. The default <see cref="NullAgbLink"/> models a lone console: the lines idle exactly as hardware leaves
/// them with no cable attached, so a single console behaves correctly without magic auto-completion. The in-process
/// realisation is <see cref="AgbLinkCable"/>, and <see cref="AgbLinkSession"/> is the deterministic multi-machine
/// stepper that drives a cable-joined set of machines as one unit.
/// </summary>
public interface IAgbLink {
    /// <summary>Gets the number of consoles on the link, including this one (1 = no cable attached).</summary>
    int PlayerCount { get; }

    /// <summary>Gets a value indicating whether a real partner is attached (more than one console on the link).</summary>
    bool HasPartner => (PlayerCount > 1);

    /// <summary>Registers a console on the link and returns the assigned node, through which it sends and receives.
    /// The first to register is the parent (id 0).</summary>
    /// <param name="client">The console-side endpoint the link drives back into — the seam through which a completed
    /// round reaches the consoles that did not clock it.</param>
    /// <returns>The node representing this console's endpoint on the link.</returns>
    IAgbLinkNode Connect(IAgbLinkClient client);
}

/// <summary>One console's endpoint on an <see cref="IAgbLink"/>. The serial controller exchanges words through it.</summary>
public interface IAgbLinkNode {
    /// <summary>Gets this console's player id on the link (0 = parent).</summary>
    int PlayerId { get; }

    /// <summary>Performs a normal-mode (8- or 32-bit) master exchange: shifts <paramref name="outgoing"/> out to the
    /// partner and returns what it shifted back. With no partner the open data line reads all ones.</summary>
    /// <param name="outgoing">The word the master shifts out.</param>
    /// <param name="word">True for a 32-bit transfer, false for 8-bit.</param>
    /// <returns>The word shifted in from the partner (all ones if none).</returns>
    uint NormalExchange(uint outgoing, bool word);

    /// <summary>Performs a multiplayer exchange: every console latches its <c>SIOMLT_SEND</c> word, the parent
    /// clocks the round, and all four slots are returned (unused/absent slots read 0xFFFF).</summary>
    /// <param name="send">This console's outgoing 16-bit word.</param>
    /// <param name="slots">Receives the four players' words (slot index = player id).</param>
    /// <returns>True if the round completed against real partners; false for a lone console.</returns>
    bool MultiplayerExchange(ushort send, out ushort[] slots);
}

/// <summary>
/// The console-side half of the link seam: the callbacks a link drives back into a serial controller when the console
/// that clocks a transfer completes it. Where <see cref="IAgbLinkNode"/> is the direction a console pushes its own
/// transfer OUT (master/parent-driven), this is the direction a completed exchange lands ON a console that merely sat
/// on the wire — the parent's multiplayer clock delivering the round to every child, or a master's normal-mode shift
/// draining a slave's pending word. Implemented by <see cref="AgbSerialController"/>; the lone-console
/// <see cref="NullAgbLink"/> never calls back.
/// </summary>
public interface IAgbLinkClient {
    /// <summary>Latches this console's outgoing multiplayer word for a round the parent is clocking. Returns whether
    /// the console is actually participating — its SIO in Multiplayer mode with the pins owned by SIO — so a console
    /// in another mode leaves its slot reading open-bus all ones on every other console.</summary>
    /// <param name="word">This console's current <c>SIOMLT_SEND</c> word.</param>
    /// <returns><see langword="true"/> if the console participates in the round.</returns>
    bool TryLatchMultiplayerWord(out ushort word);

    /// <summary>Delivers a completed multiplayer round: all four players' words land in <c>SIOMULTI0..3</c>, the
    /// assigned daisy-chain id lands in the SIOCNT id bits, the busy (start) bit clears, and the serial IRQ is
    /// requested if enabled — exactly what hardware does on every connected console when the parent's clock finishes
    /// the round. The parent's own registers are set by its own completion path, never through this.</summary>
    /// <param name="slots">The four players' words (slot index = player id; absent slots 0xFFFF).</param>
    /// <param name="playerId">This console's assigned daisy-chain position (0 = parent).</param>
    void CompleteMultiplayerRound(ReadOnlySpan<ushort> slots, int playerId);

    /// <summary>Attempts to complete a pending normal-mode slave transfer against a master's shift: if this console
    /// has an external-clock transfer of the matching width armed (start bit held), the exchange swaps —
    /// <paramref name="incoming"/> lands in its data register, its outgoing word is returned to the master, the busy
    /// bit clears, and the serial IRQ is requested if enabled. Otherwise nothing changes and the master reads the
    /// idle-high line.</summary>
    /// <param name="incoming">The word the master shifted out.</param>
    /// <param name="word">True for a 32-bit transfer, false for 8-bit.</param>
    /// <param name="outgoing">The word this slave shifted back, when the exchange happened.</param>
    /// <returns><see langword="true"/> if a pending slave transfer completed.</returns>
    bool TryCompleteNormalSlave(uint incoming, bool word, out uint outgoing);
}
