namespace Puck.AdvancedGamingBrick;

/// <summary>The default link transport: no cable attached. A lone console's serial lines idle high, so every
/// exchange reads all ones and reports no partner — the serial controller leaves a started transfer pending exactly
/// as hardware does (a cable-less transfer never completes), rather than fabricating a result.</summary>
public sealed class NullAgbLink : IAgbLink, IAgbLinkNode {
    /// <summary>The shared instance — a lone console needs no per-machine state.</summary>
    public static readonly NullAgbLink Instance = new();

    private NullAgbLink() {
    }

    /// <inheritdoc/>
    public int PlayerCount => 1;

    /// <inheritdoc/>
    public int PlayerId => 0;

    /// <inheritdoc/>
    public IAgbLinkNode Connect() => this;

    /// <inheritdoc/>
    public uint NormalExchange(uint outgoing, bool word) => word ? 0xFFFFFFFFu : 0xFFu;

    /// <inheritdoc/>
    public bool MultiplayerExchange(ushort send, out ushort[] slots) {
        slots = new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF };

        return false;
    }
}
