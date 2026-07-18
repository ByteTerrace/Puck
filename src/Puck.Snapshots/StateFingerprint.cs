namespace Puck.Snapshots;

/// <summary>
/// The repo's one snapshot fingerprint — 64-bit FNV-1a over a byte span. Deterministic, allocation-free, and identical
/// across runs and machines, so both the snapshot-identity stamp (model/BIOS/ROM images) and the hash-divergence
/// localizer name the same instant the same way. Only ever compared for equality, never used as a security digest.
/// </summary>
public static class StateFingerprint {
    private const ulong OffsetBasis = 0xCBF29CE484222325ul;
    private const ulong Prime = 0x100000001B3ul;

    /// <summary>Computes the FNV-1a fingerprint of a byte span.</summary>
    /// <param name="data">The bytes to fingerprint.</param>
    /// <returns>The 64-bit fingerprint.</returns>
    public static ulong Compute(ReadOnlySpan<byte> data) {
        var hash = OffsetBasis;

        foreach (var value in data) {
            hash = ((hash ^ value) * Prime);
        }

        return hash;
    }
}
