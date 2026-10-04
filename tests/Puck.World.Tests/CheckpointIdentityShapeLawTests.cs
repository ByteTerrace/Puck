using System.Globalization;
using Puck.AdvancedGamingBrick;
using Puck.HumbleGamingBrick;
using Puck.Machines;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An emulator core's checkpoint identity names the shape fingerprint the ledger records for its snapshot layout,
/// so a checkpoint of another layout is a different identity and a host refuses its restore by identity.</summary>
public sealed class CheckpointIdentityShapeLawTests {
    [Fact]
    public void AnAdvancedCoresIdentityNamesTheLedgersShapeAndNoOtherShapeIsTheSameIdentity() {
        var bios = new byte[(16 * 1024)];
        var rom = new byte[0x200];

        using var core = new AdvancedGamingBrickCore(
            bios,
            rom
        );

        Assert.Equal(
            core.CheckpointIdentity,
            MachineCheckpointIdentity.Compute(
                FormattableString.Invariant(formattable: $"puck.agb.core.v1/{FormatLedgerShapes.Of(id: "AgbMachineIdentity.CurrentVersion")}/False/False"),
                bios,
                rom
            )
        );
        Assert.NotEqual(
            core.CheckpointIdentity,
            MachineCheckpointIdentity.Compute(
                FormattableString.Invariant(formattable: $"puck.agb.core.v1/0000000000000000/False/False"),
                bios,
                rom
            )
        );
    }
    [Fact]
    public void AHumbleCoresIdentityNamesTheLedgersShapeAndNoOtherShapeIsTheSameIdentity() {
        var rom = new byte[0x8000];
        var configuration = HgbFirmware.CreateConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: rom,
            model: ConsoleModel.CgbE
        );

        using var core = new HumbleGamingBrickCore(configuration: configuration);

        string Identity(string shape) => MachineCheckpointIdentity.Compute(
            string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"puck.hgb.core.v1/{shape}/{((int)configuration.Model)}/{configuration.TickResolution.SubdivisionLog2}/False"
            ),
            configuration.BootRom,
            configuration.CartridgeRom
        );

        Assert.Equal(
            core.CheckpointIdentity,
            Identity(shape: FormatLedgerShapes.Of(id: "MachineIdentity.CurrentVersion"))
        );
        Assert.NotEqual(
            core.CheckpointIdentity,
            Identity(shape: "0000000000000000")
        );
    }
}
