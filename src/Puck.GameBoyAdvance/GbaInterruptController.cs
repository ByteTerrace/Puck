namespace Puck.GameBoyAdvance;

/// <summary>The default interrupt controller: plain IE/IF/IME state with a write-one-to-clear IF register.</summary>
public sealed class GbaInterruptController : IGbaInterruptController {
    private const ushort SourceMask = 0x3FFF;

    private ushort m_enable;
    private ushort m_requested;
    private bool m_masterEnable;

    /// <inheritdoc/>
    public bool LineAsserted => m_masterEnable && ((m_enable & m_requested & SourceMask) != 0);

    /// <inheritdoc/>
    public bool HasPendingInterrupt => (m_enable & m_requested & SourceMask) != 0;

    /// <inheritdoc/>
    public void Request(InterruptSource source) {
        m_requested |= (ushort)(1u << (int)source);
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) => offset switch {
        0x200u => m_enable,
        0x202u => m_requested,
        0x208u => (ushort)(m_masterEnable ? 1u : 0u),
        _ => 0,
    };

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        switch (offset) {
            case 0x200u:
                m_enable = (ushort)(value & SourceMask);

                break;
            case 0x202u:
                // Writing a one to an IF bit acknowledges (clears) that request.
                m_requested &= (ushort)~value;

                break;
            case 0x208u:
                m_masterEnable = (value & 1u) != 0u;

                break;
            default:
                break;
        }
    }
}
