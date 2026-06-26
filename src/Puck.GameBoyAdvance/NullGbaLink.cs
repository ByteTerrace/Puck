namespace Puck.GameBoyAdvance;

/// <summary>The default link transport: no cable attached. A lone console's serial lines idle high, so every
/// exchange reads all ones and reports no partner — the serial controller leaves a started transfer pending exactly
/// as hardware does (matching ARES, which never completes a cable-less transfer), rather than fabricating a result.</summary>
public sealed class NullGbaLink : IGbaLink, IGbaLinkNode {
    /// <summary>The shared instance — a lone console needs no per-machine state.</summary>
    public static readonly NullGbaLink Instance = new();

    private NullGbaLink() {
    }

    /// <inheritdoc/>
    public int PlayerCount => 1;

    /// <inheritdoc/>
    public int PlayerId => 0;

    /// <inheritdoc/>
    public IGbaLinkNode Connect() => this;

    /// <inheritdoc/>
    public uint NormalExchange(uint outgoing, bool word) => word ? 0xFFFFFFFFu : 0xFFu;

    /// <inheritdoc/>
    public bool MultiplayerExchange(ushort send, out ushort[] slots) {
        slots = new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF };

        return false;
    }
}
