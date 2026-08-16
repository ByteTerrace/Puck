using Puck.Maths;

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
        new(
        Version: CurrentVersion,
        BiosHash: Fnv1aHash.Compute(values: bios),
        RomHash: Fnv1aHash.Compute(values: rom),
        RomLength: rom.Length
    );
}
/// <summary>
/// A self-contained, deterministic byte image of an Advanced GamingBrick's entire mutable state at one instant. It owns
/// its bytes (through a shared <see cref="SnapshotImage"/>) and aliases nothing in the live machine, so it can be held
/// indefinitely, restored into the machine it came from to rewind, or loaded into a forked sibling of the same identity
/// to diverge a run. The captured instant and machine identity travel with it: a restore repositions the master clock
/// exactly and refuses a machine whose BIOS/ROM identity differs.
/// </summary>
public sealed class AgbMachineSnapshot : MachineSnapshot<AgbMachineSnapshot, AgbMachineIdentity, long> {
    internal AgbMachineSnapshot(AgbMachineIdentity identity, long takenAt, SnapshotImage image)
        : base(
        identity: identity,
        takenAt: takenAt,
        image: image
    ) { }

    /// <inheritdoc/>
    protected override AgbMachineSnapshot Create(AgbMachineIdentity identity, long takenAt, SnapshotImage image) =>
        new(
        identity: identity,
        image: image,
        takenAt: takenAt
    );
}
