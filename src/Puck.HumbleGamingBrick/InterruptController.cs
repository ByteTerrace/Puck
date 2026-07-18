using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The concrete interrupt hub. It holds the IF request mask (its low five bits meaningful) and the IE enable mask, and
/// derives the pending set from their intersection. It carries no timeline state, so it is snapshotted but not driven:
/// its whole mutable state is the two mask bytes.
/// </summary>
public sealed class InterruptController : IInterruptController, ISnapshotable {
    private const byte SourceMask = 0x1F;

    private InterruptKind m_enabled;
    private InterruptKind m_requested;

    /// <summary>Creates the controller. Without a boot ROM the request mask is seeded to the post-boot handoff; with
    /// one, both masks power on clear and the executing boot program raises whatever it raises.</summary>
    /// <param name="configuration">The machine configuration, which selects the seeded or cold start.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public InterruptController(MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        // The boot ROM runs through at least one vertical blank with IE clear, so the machine hands off with the VBlank
        // request flag already set (IF reads 0xE1) — state some titles and the timing oracles observe.
        if (configuration.BootRom is null) {
            m_requested = InterruptKind.VBlank;
        }
    }

    /// <inheritdoc/>
    public InterruptKind Requested {
        get => m_requested;
        set => m_requested = (InterruptKind)((byte)value & SourceMask);
    }
    /// <inheritdoc/>
    public InterruptKind Enabled {
        get => m_enabled;
        set => m_enabled = value;
    }
    /// <inheritdoc/>
    public InterruptKind Pending =>
        (InterruptKind)((byte)m_requested & (byte)m_enabled & SourceMask);

    /// <inheritdoc/>
    public void Request(InterruptKind kind) =>
        m_requested = (InterruptKind)((byte)m_requested | ((byte)kind & SourceMask));
    /// <inheritdoc/>
    public void Acknowledge(InterruptKind kind) =>
        m_requested = (InterruptKind)((byte)m_requested & (byte)~kind);
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteByte(value: (byte)m_requested);
        writer.WriteByte(value: (byte)m_enabled);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_requested = (InterruptKind)reader.ReadByte();
        m_enabled = (InterruptKind)reader.ReadByte();
    }
}
