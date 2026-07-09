namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The identity a snapshot is stamped with so it refuses to load into a mismatched machine — the pre-flight guard the
/// survey names as preventing a documented top cause of desync (loading a snapshot against the wrong ROM/BIOS pair).
/// It fingerprints the format version, the BIOS image, and the cartridge ROM; a restore compares this against the
/// live machine's identity and faults on any difference rather than silently corrupting state.
/// </summary>
/// <param name="Version">The snapshot format version.</param>
/// <param name="BiosHash">A 64-bit fingerprint of the BIOS image.</param>
/// <param name="RomHash">A 64-bit fingerprint of the cartridge ROM.</param>
/// <param name="RomLength">The cartridge ROM length in bytes.</param>
public readonly record struct AgbMachineIdentity(int Version, ulong BiosHash, ulong RomHash, int RomLength) {
    /// <summary>The current snapshot format version. Bump when the serialized field layout changes so an old
    /// snapshot is rejected rather than misread.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Computes an identity for a BIOS image and cartridge ROM using a stable FNV-1a fingerprint.</summary>
    /// <param name="bios">The BIOS image bytes.</param>
    /// <param name="rom">The cartridge ROM bytes.</param>
    /// <returns>The identity stamp.</returns>
    public static AgbMachineIdentity Compute(ReadOnlySpan<byte> bios, ReadOnlySpan<byte> rom) =>
        new(Version: CurrentVersion, BiosHash: Fingerprint(data: bios), RomHash: Fingerprint(data: rom), RomLength: rom.Length);

    // FNV-1a 64-bit — deterministic, no allocation, and identical across runs (the same fingerprint the render-hash
    // probe uses). Only ever compared for equality, never used as a security digest.
    private static ulong Fingerprint(ReadOnlySpan<byte> data) {
        const ulong offsetBasis = 0xCBF29CE484222325ul;
        const ulong prime = 0x100000001B3ul;

        var hash = offsetBasis;

        foreach (var value in data) {
            hash = ((hash ^ value) * prime);
        }

        return hash;
    }
}

/// <summary>
/// A self-contained, deterministic byte image of an Advanced GamingBrick's entire mutable state at one instant. It
/// owns its bytes and aliases nothing in the live machine, so it can be held indefinitely, restored into the machine
/// it came from to rewind, or (once forking lands) loaded into a fresh machine of the same identity to diverge a run.
/// The captured instant and machine identity travel with it: a restore repositions the master clock exactly and
/// refuses a machine whose BIOS/ROM identity differs.
/// </summary>
public sealed class AgbMachineSnapshot {
    private readonly byte[] m_data;

    internal AgbMachineSnapshot(AgbMachineIdentity identity, long takenAt, byte[] data) {
        Identity = identity;
        TakenAt = takenAt;
        m_data = data;
    }

    /// <summary>Gets the identity the snapshot was stamped with (format version + BIOS/ROM fingerprint).</summary>
    public AgbMachineIdentity Identity { get; }

    /// <summary>Gets the master-clock cycle at which this snapshot was taken.</summary>
    public long TakenAt { get; }

    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size => m_data.Length;

    /// <summary>Indicates whether another snapshot captures byte-identical state (identity, instant, and all bytes).
    /// Two machines driven from the same start by any mix of pacing produce equal snapshots; a difference is exactly a
    /// divergence.</summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when both snapshots hold identical state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool ContentEquals(AgbMachineSnapshot other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return (Identity == other.Identity)
            && (TakenAt == other.TakenAt)
            && m_data.AsSpan().SequenceEqual(other: other.m_data);
    }

    internal AgbStateReader OpenReader() => new(buffer: m_data);
}
