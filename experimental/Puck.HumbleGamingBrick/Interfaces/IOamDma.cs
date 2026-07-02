namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>How a CPU write that collides with a running OAM DMA transfer resolves on Color hardware.</summary>
public enum OamDmaWriteConflict {
    /// <summary>No conflict — perform the normal write.</summary>
    None,
    /// <summary>Drop the write entirely.</summary>
    Drop,
    /// <summary>Store the value at the conflict target instead of the requested address.</summary>
    Store,
    /// <summary>Store the value at the conflict target AND zero the OAM byte the DMA is currently writing.</summary>
    StoreAndPoisonOam,
}

/// <summary>
/// The object-attribute-memory DMA unit (the DMA register at <c>0xFF46</c>). Writing the register starts a transfer
/// that copies 160 bytes from a page of memory into OAM one byte per machine cycle; while it runs, OAM is unreadable to
/// the CPU and — on Color — the transfer occupies its source's bus, hijacking CPU accesses that collide with it. The
/// bus forwards the register here and asks it whether a transfer is in progress so it can gate OAM and resolve the
/// conflicts.
/// </summary>
public interface IOamDma {
    /// <summary>Gets whether a transfer is currently copying into OAM, during which the CPU sees OAM as
    /// <c>0xFF</c>.</summary>
    bool IsActive { get; }
    /// <summary>Gets whether a transfer is running or its startup delay is counting down; CPU writes into OAM are
    /// dropped through this whole window.</summary>
    bool IsActiveOrWarmingUp { get; }

    /// <summary>Reads the DMA register, which returns the last value written regardless of transfer state.</summary>
    /// <returns>The last value written to the register.</returns>
    byte ReadRegister();
    /// <summary>Writes the DMA register, arming a transfer whose source page is <paramref name="value"/> <c>* 0x100</c>.
    /// A write while a transfer is already running restarts it after the same startup delay.</summary>
    /// <param name="value">The high byte of the source address.</param>
    void WriteRegister(byte value);
    /// <summary>Classifies a CPU read that may collide with the running transfer's bus (Color only).</summary>
    /// <param name="address">The address the CPU is reading.</param>
    /// <param name="forceOpenBus">Set when the read returns open bus (<c>0xFF</c>) rather than a redirect.</param>
    /// <param name="redirect">The address the read is hijacked to when it conflicts and is not open bus.</param>
    /// <returns>Whether the read conflicts with the transfer.</returns>
    bool TryReadConflict(ushort address, out bool forceOpenBus, out ushort redirect);
    /// <summary>Classifies a CPU write that may collide with the running transfer's bus (Color only).</summary>
    /// <param name="address">The address the CPU is writing.</param>
    /// <param name="target">The address the write lands on when the classification stores it elsewhere.</param>
    /// <returns>How the write resolves.</returns>
    OamDmaWriteConflict ClassifyWriteConflict(ushort address, out ushort target);
    /// <summary>Zeroes the OAM byte the transfer is currently writing — the side effect of a colliding CPU write that
    /// lands under the DMA's pointer.</summary>
    void PoisonCurrentOamByte();
}
