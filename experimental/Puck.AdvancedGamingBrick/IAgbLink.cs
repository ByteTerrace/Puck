namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The physical link-cable transport between Advanced GamingBrick consoles. A <see cref="AgbSerialController"/> drives
/// all of its serial I/O through this seam, so two or more in-process <c>AdvancedGamingBrickMachine</c> instances can be
/// wired together (and a future networked transport can slot in) without the SIO subsystem knowing how the wire is
/// realised. The default <see cref="NullAgbLink"/> models a lone console: the lines idle exactly as hardware leaves
/// them with no cable attached, so a single console behaves correctly without magic auto-completion.
/// </summary>
public interface IAgbLink {
    /// <summary>Gets the number of consoles on the link, including this one (1 = no cable attached).</summary>
    int PlayerCount { get; }

    /// <summary>Gets a value indicating whether a real partner is attached (more than one console on the link).</summary>
    bool HasPartner => PlayerCount > 1;

    /// <summary>Registers a console on the link and returns the assigned node, through which it sends and receives.
    /// The first to register is the parent (id 0).</summary>
    /// <returns>The node representing this console's endpoint on the link.</returns>
    IAgbLinkNode Connect();
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
