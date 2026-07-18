namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The in-process link-cable transport: up to four consoles' serial controllers joined into one daisy-chain.
/// Registration order is chain position — the first console to <see cref="Connect"/> is the parent (player 0), the
/// clock source hardware elects by wiring. The cable performs the actual word exchange at the instant the driving
/// console's transfer completes: a multiplayer round latches every participating console's <c>SIOMLT_SEND</c> word
/// and delivers all four slots (absent or non-participating slots read open-bus 0xFFFF) to every other console
/// through <see cref="IAgbLinkClient.CompleteMultiplayerRound"/>, and a normal-mode master exchange swaps words with
/// the partner console's pending slave transfer. Every observable value lives in the consoles' own serial registers;
/// the cable holds no emulated state, so it is topology, never snapshot content — re-attach a fresh cable around a
/// savestate rather than serializing this object. Determinism is inherited from the caller's stepping discipline:
/// drive cable-joined machines through an <see cref="AgbLinkSession"/> (never on separate threads), and every
/// exchanged word is a pure function of the machines' states.
/// </summary>
public sealed class AgbLinkCable : IAgbLink {
    /// <summary>The maximum number of consoles a multiplayer daisy-chain carries.</summary>
    public const int MaxPlayers = 4;

    private readonly List<Node> m_nodes = new(capacity: MaxPlayers);

    /// <inheritdoc/>
    public int PlayerCount => m_nodes.Count;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The chain already carries <see cref="MaxPlayers"/> consoles, or
    /// <paramref name="client"/> is already on it.</exception>
    public IAgbLinkNode Connect(IAgbLinkClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        if (m_nodes.Count == MaxPlayers) {
            throw new InvalidOperationException(message: $"The link cable already carries {MaxPlayers} consoles; a multiplayer chain goes no deeper.");
        }

        foreach (var existing in m_nodes) {
            if (ReferenceEquals(objA: existing.Client, objB: client)) {
                throw new InvalidOperationException(message: "This console is already on the link cable; a console occupies exactly one chain position.");
            }
        }

        var node = new Node(cable: this, client: client, playerId: m_nodes.Count);

        m_nodes.Add(item: node);

        return node;
    }

    // Normal mode is a point-to-point wire: the partner is the first OTHER console on the cable (a normal-mode cable
    // physically joins exactly two consoles; extra chain members simply are not on that wire). If the partner has a
    // matching slave transfer armed the words swap; otherwise the master reads the idle-high data line, exactly as a
    // lone console does.
    private uint NormalExchange(Node from, uint outgoing, bool word) {
        foreach (var node in m_nodes) {
            if (ReferenceEquals(objA: node, objB: from)) {
                continue;
            }

            if (node.Client.TryCompleteNormalSlave(incoming: outgoing, word: word, out var reply)) {
                return reply;
            }

            break;
        }

        return (word ? 0xFFFFFFFFu : 0xFFu);
    }

    // One multiplayer round, clocked by `from` (the console whose transfer just completed — the parent): latch every
    // participating console's send word FIRST, so every delivery below observes the same completed round regardless
    // of chain position, then deliver the slots to every other participant. `from`'s own registers are set by its own
    // completion path after this returns, so it is never delivered to (no double IRQ).
    private bool MultiplayerExchange(Node from, ushort send, ushort[] slots) {
        Span<bool> participated = stackalloc bool[MaxPlayers];

        slots[0] = 0xFFFF;
        slots[1] = 0xFFFF;
        slots[2] = 0xFFFF;
        slots[3] = 0xFFFF;
        slots[from.PlayerId] = send;

        foreach (var node in m_nodes) {
            if (ReferenceEquals(objA: node, objB: from)) {
                continue;
            }

            if (node.Client.TryLatchMultiplayerWord(word: out var latched)) {
                slots[node.PlayerId] = latched;
                participated[node.PlayerId] = true;
            }
        }

        foreach (var node in m_nodes) {
            if (participated[node.PlayerId]) {
                node.Client.CompleteMultiplayerRound(slots: slots, playerId: node.PlayerId);
            }
        }

        return (m_nodes.Count > 1);
    }

    /// <summary>One console's endpoint: its chain position plus the routes back into the shared cable.</summary>
    private sealed class Node : IAgbLinkNode {
        private readonly AgbLinkCable m_cable;

        internal Node(AgbLinkCable cable, IAgbLinkClient client, int playerId) {
            Client = client;
            PlayerId = playerId;
            m_cable = cable;
        }

        /// <summary>Gets the console-side endpoint the cable delivers completed exchanges into.</summary>
        public IAgbLinkClient Client { get; }

        /// <inheritdoc/>
        public int PlayerId { get; }

        /// <inheritdoc/>
        public uint NormalExchange(uint outgoing, bool word) =>
            m_cable.NormalExchange(from: this, outgoing: outgoing, word: word);

        /// <inheritdoc/>
        public bool MultiplayerExchange(ushort send, out ushort[] slots) {
            slots = new ushort[MaxPlayers];

            return m_cable.MultiplayerExchange(from: this, send: send, slots: slots);
        }
    }
}
