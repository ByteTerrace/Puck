using Puck.HumbleGamingBrick.Timing;
using Puck.Maths;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The identity a snapshot is stamped with so it refuses to load into a mismatched machine — the pre-flight guard that
/// prevents a documented top cause of desync (loading a snapshot against the wrong model or cartridge). It fingerprints
/// the format version, the emulated console model, and the immutable ROM images (the cartridge ROM and, when present,
/// the boot ROM); a restore compares this against the live machine's identity and faults on any difference rather than
/// silently corrupting state. The GB/GBC machine has no user BIOS image the way the Advanced machine does — its boot
/// behaviour is either a seeded post-boot handoff or an optional boot ROM — so the boot-ROM fingerprint stands in for
/// the Advanced machine's BIOS fingerprint (the empty-span fingerprint when no boot ROM was configured).
/// </summary>
/// <param name="Version">The snapshot format version.</param>
/// <param name="Model">The emulated console model (its <see cref="ConsoleModel"/> integer).</param>
/// <param name="BootRomHash">A 64-bit fingerprint of the boot ROM image, or the empty-span fingerprint when none.</param>
/// <param name="RomHash">A 64-bit fingerprint of the cartridge ROM.</param>
/// <param name="RomLength">The cartridge ROM length in bytes.</param>
public readonly record struct MachineIdentity(int Version, int Model, ulong BootRomHash, ulong RomHash, int RomLength) {
    /// <summary>The current snapshot format version. Increment it whenever the serialized field layout changes so an
    /// incompatible snapshot is rejected rather than misread.</summary>
    public const int CurrentVersion = 4;

    /// <summary>Computes an identity for a console model and its immutable ROM images using a stable FNV-1a fingerprint.</summary>
    /// <param name="model">The emulated console model.</param>
    /// <param name="bootRom">The boot ROM image bytes, or an empty span when the machine starts at the seeded post-boot
    /// handoff state.</param>
    /// <param name="rom">The cartridge ROM image bytes.</param>
    /// <returns>The identity stamp.</returns>
    public static MachineIdentity Compute(ConsoleModel model, ReadOnlySpan<byte> bootRom, ReadOnlySpan<byte> rom) =>
        new(
            Version: CurrentVersion,
            Model: (int)model,
            BootRomHash: Fnv1aHash.Compute(values: bootRom),
            RomHash: Fnv1aHash.Compute(values: rom),
            RomLength: rom.Length
        );
}

/// <summary>
/// An immutable, self-contained capture of a machine's entire mutable state at one instant. It owns its bytes (through a
/// shared <see cref="SnapshotImage"/>) and aliases nothing in the live machine, so it can be held indefinitely, restored
/// into the same machine to rewind, or loaded into a fresh machine to fork a divergent run. The captured instant and
/// machine identity travel with it: a restore repositions the clock exactly and refuses a machine whose model/ROM
/// identity differs.
/// </summary>
public sealed class MachineSnapshot : Puck.Snapshots.MachineSnapshot<MachineSnapshot, MachineIdentity, Tick> {
    internal MachineSnapshot(MachineIdentity identity, Tick takenAt, SnapshotImage image)
        : base(identity: identity, takenAt: takenAt, image: image) { }

    /// <inheritdoc/>
    protected override MachineSnapshot Create(MachineIdentity identity, Tick takenAt, SnapshotImage image) =>
        new(identity: identity, takenAt: takenAt, image: image);
}
