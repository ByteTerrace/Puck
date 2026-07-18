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
    /// <remarks>6: AgbCartridge gained the rumble motor latch, the solar-sensor counter/edge/threshold (G1/G2), and
    /// the address-mapped tilt sensor's latched X/Y bytes (G3).</remarks>
    public const int CurrentVersion = 6;

    /// <summary>Computes an identity for a BIOS image and cartridge ROM using a stable FNV-1a fingerprint.</summary>
    /// <param name="bios">The BIOS image bytes.</param>
    /// <param name="rom">The cartridge ROM bytes.</param>
    /// <returns>The identity stamp.</returns>
    public static AgbMachineIdentity Compute(ReadOnlySpan<byte> bios, ReadOnlySpan<byte> rom) =>
        new(Version: CurrentVersion, BiosHash: StateFingerprint.Compute(data: bios), RomHash: StateFingerprint.Compute(data: rom), RomLength: rom.Length);
}

/// <summary>
/// A self-contained, deterministic byte image of an Advanced GamingBrick's entire mutable state at one instant. It owns
/// its bytes (through a shared <see cref="SnapshotImage"/>) and aliases nothing in the live machine, so it can be held
/// indefinitely, restored into the machine it came from to rewind, or loaded into a forked sibling of the same identity
/// to diverge a run. The captured instant and machine identity travel with it: a restore repositions the master clock
/// exactly and refuses a machine whose BIOS/ROM identity differs.
/// </summary>
public sealed class AgbMachineSnapshot {
    private readonly SnapshotImage m_image;

    internal AgbMachineSnapshot(AgbMachineIdentity identity, long takenAt, SnapshotImage image) {
        Identity = identity;
        TakenAt = takenAt;
        m_image = image;
    }

    /// <summary>Gets the identity the snapshot was stamped with (format version + BIOS/ROM fingerprint).</summary>
    public AgbMachineIdentity Identity { get; }

    /// <summary>Gets the master-clock cycle at which this snapshot was taken.</summary>
    public long TakenAt { get; }

    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size => m_image.Size;

    /// <summary>Gets the component byte-range table this snapshot was written with (the divergence localizer's map
    /// from a raw offset back to the component that owns it). Covers every byte in <see cref="Data"/>, in save order.</summary>
    public IReadOnlyList<SnapshotSection> Sections => m_image.Sections;

    /// <summary>Gets the raw, flat snapshot bytes — the same bytes a restore reads back. Exposed read-only for
    /// diagnostics (hashing, byte-level diffing); never mutated in place.</summary>
    public ReadOnlySpan<byte> Data => m_image.Data;

    /// <summary>Indicates whether another snapshot captures byte-identical state (identity, instant, and all bytes).
    /// Two machines driven from the same start by any mix of pacing produce equal snapshots; a difference is exactly a
    /// divergence.</summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when both snapshots hold identical state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool ContentEquals(AgbMachineSnapshot other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return ((Identity == other.Identity)
            && (TakenAt == other.TakenAt)
            && m_image.BytesEqual(other: other.m_image));
    }

    /// <summary>Returns a copy of this snapshot with a single data byte overwritten — a cycle-cost-free way to inject
    /// a controlled corruption (e.g. into the <c>bus</c> section's EWRAM range) for testing a divergence-detection
    /// tool against itself. Identity, captured instant, and the section table are unchanged; restoring the result
    /// repositions the machine to this exact instant except for the one poked byte. Never used by real gameplay or
    /// save-state code — a diagnostic-only seam.</summary>
    /// <param name="offset">The absolute byte offset within <see cref="Data"/> to overwrite.</param>
    /// <param name="value">The replacement byte value.</param>
    /// <returns>A new snapshot, identical except for the one byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside <see cref="Data"/>.</exception>
    public AgbMachineSnapshot WithPokedByte(int offset, byte value) =>
        new(identity: Identity, takenAt: TakenAt, image: m_image.WithPokedByte(offset: offset, value: value));

    internal StateReader OpenReader() => m_image.OpenReader();
}
