using Puck.Maths;

namespace Puck.HumbleGamingDeck;

/// <summary>The complete configuration stamp required for restoring a Deck snapshot.</summary>
/// <param name="Version">The component layout version.</param>
/// <param name="Model">The console revision.</param>
/// <param name="PowerOn">The named alignment and RAM-fill profile.</param>
/// <param name="Header">The resolved board and memory configuration.</param>
/// <param name="ImageHash">The fingerprint of the complete source image, including header and trainer.</param>
/// <param name="PrgHash">The PRG-ROM fingerprint.</param>
/// <param name="ChrHash">The CHR-ROM fingerprint.</param>
/// <param name="ImageLength">The complete image length.</param>
public readonly record struct HgdMachineIdentity(int Version, HgdConsoleModel Model, HgdPowerOnProfile PowerOn,
    HgdCartridgeHeader Header, ulong ImageHash, ulong PrgHash, ulong ChrHash, int ImageLength) {
    /// <summary>The complete machine snapshot layout, including separate volatile and nonvolatile board memory.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Computes the stamp from all execution inputs.</summary>
    /// <param name="configuration">The configuration to fingerprint.</param>
    /// <returns>The typed identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static HgdMachineIdentity Compute(HgdMachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        return new(Version: CurrentVersion, Model: configuration.Model, PowerOn: configuration.PowerOn,
            Header: configuration.Cartridge.Header,
            ImageHash: Fnv1aHash.Compute(values: configuration.Cartridge.Image),
            PrgHash: Fnv1aHash.Compute(values: configuration.Cartridge.PrgRom),
            ChrHash: Fnv1aHash.Compute(values: configuration.Cartridge.ChrRom),
            ImageLength: configuration.Cartridge.Image.Length);
    }
}
/// <summary>A typed, immutable capture at any master tick, including a partially executed CPU instruction.</summary>
public sealed class HgdMachineSnapshot : MachineSnapshot<HgdMachineSnapshot, HgdMachineIdentity, ulong> {
    /// <summary>Initializes a new instance of the <see cref="HgdMachineSnapshot"/> class over an owned state image.</summary>
    /// <param name="identity">The machine's configuration stamp.</param>
    /// <param name="takenAt">The completed master-tick count.</param>
    /// <param name="image">The immutable component bytes and section table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    public HgdMachineSnapshot(HgdMachineIdentity identity, ulong takenAt, SnapshotImage image)
        : base(identity: identity, takenAt: takenAt, image: image) { }

    /// <inheritdoc/>
    protected override HgdMachineSnapshot Create(HgdMachineIdentity identity, ulong takenAt, SnapshotImage image) {
        return new(identity: identity, image: image, takenAt: takenAt);
    }
}
