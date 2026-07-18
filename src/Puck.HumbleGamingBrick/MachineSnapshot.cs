using Puck.HumbleGamingBrick.Timing;

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
    /// <remarks>3: Mbc5Cartridge gained a latched rumble-motor bool (G1); Mbc7Cartridge's accelerometer latch reads
    /// through the new ITiltSensor seam rather than a fixed constant, but its serialized fields are unchanged (G3).
    /// 4 (L-01 correction): <c>5187341</c> added the CGB infrared port as a new <c>ISnapshotable</c> section
    /// (<see cref="InfraredPort"/>'s RP register byte + cart LED latch, two bytes, inserted between
    /// <c>SerialComponent</c> and <c>ApuComponent</c> in registration order) without bumping this constant, so v3 for a
    /// while silently labeled two different layouts. This bump corrects that retroactively; a v3 snapshot taken before
    /// <c>5187341</c> is now correctly refused rather than misread two bytes short.</remarks>
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
            BootRomHash: StateFingerprint.Compute(data: bootRom),
            RomHash: StateFingerprint.Compute(data: rom),
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
public sealed class MachineSnapshot {
    private readonly SnapshotImage m_image;

    internal MachineSnapshot(MachineIdentity identity, Tick takenAt, SnapshotImage image) {
        Identity = identity;
        TakenAt = takenAt;
        m_image = image;
    }

    /// <summary>Gets the identity the snapshot was stamped with (format version + model + boot/cartridge ROM fingerprint).</summary>
    public MachineIdentity Identity { get; }
    /// <summary>Gets the instant on the master timeline at which this snapshot was taken.</summary>
    public Tick TakenAt { get; }
    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size =>
        m_image.Size;
    /// <summary>Gets the component byte-range table this snapshot was written with (the divergence localizer's map from a
    /// raw offset back to the component that owns it). Covers every byte in <see cref="Data"/>, in save order.</summary>
    public IReadOnlyList<SnapshotSection> Sections =>
        m_image.Sections;
    /// <summary>Gets the raw, flat snapshot bytes — the same bytes a restore reads back. Exposed read-only for
    /// diagnostics (hashing, byte-level diffing); never mutated in place.</summary>
    public ReadOnlySpan<byte> Data =>
        m_image.Data;

    /// <summary>Indicates whether another snapshot captures byte-identical state (identity, instant, and all bytes). Two
    /// machines driven from the same start by any mix of pacing — single ticks or run budgets — produce equal snapshots;
    /// a difference is exactly a divergence.</summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when both snapshots hold identical state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool ContentEquals(MachineSnapshot other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return ((Identity == other.Identity)
            && (TakenAt == other.TakenAt)
            && m_image.BytesEqual(other: other.m_image));
    }
    /// <summary>Returns a copy of this snapshot with a single data byte overwritten — a cycle-cost-free way to inject a
    /// controlled corruption (e.g. into the <c>SystemMemory</c> section's work-RAM range) for testing a
    /// divergence-detection tool against itself. Identity, captured instant, and the section table are unchanged;
    /// restoring the result repositions the machine to this exact instant except for the one poked byte. Never used by
    /// real gameplay or save-state code — a diagnostic-only seam.</summary>
    /// <param name="offset">The absolute byte offset within <see cref="Data"/> to overwrite.</param>
    /// <param name="value">The replacement byte value.</param>
    /// <returns>A new snapshot, identical except for the one byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside <see cref="Data"/>.</exception>
    public MachineSnapshot WithPokedByte(int offset, byte value) =>
        new(identity: Identity, takenAt: TakenAt, image: m_image.WithPokedByte(offset: offset, value: value));

    internal StateReader OpenReader() =>
        m_image.OpenReader();
}
