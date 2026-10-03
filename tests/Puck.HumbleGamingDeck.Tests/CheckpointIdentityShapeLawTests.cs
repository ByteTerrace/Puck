using System.Globalization;
using Puck.Machines;
using Puck.Testing;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>A Deck core's checkpoint identity names the shape fingerprint the ledger records for the snapshot layout, so a
/// checkpoint of another layout is a different identity and the host refuses its restore by identity.</summary>
public sealed class CheckpointIdentityShapeLawTests {
    private static HgdMachineConfiguration Configuration() {
        var image = new byte[(16 + 16384)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 1;
        image[7] = 8;
        image[11] = 7;

        return new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: image));
    }
    private static string Identity(HgdMachineConfiguration configuration, string shape) {
        var powerOn = configuration.PowerOn;

        return MachineCheckpointIdentity.Compute(
            cartridge: configuration.Cartridge.Image,
            descriptor: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"puck.hgd.core.v1/{shape}/{configuration.Model}/{powerOn.Name}/{powerOn.AlignmentPhase}/{powerOn.WorkRamFill}"
            ),
            firmware: []
        );
    }

    [Fact]
    public void TheCoresIdentityNamesTheLedgersShapeAndNoOtherShapeIsTheSameIdentity() {
        var configuration = Configuration();

        using var core = new HumbleGamingDeckCore(configuration: configuration);

        Assert.Equal(
            actual: core.CheckpointIdentity,
            expected: Identity(
                configuration: configuration,
                shape: FormatLedgerShapes.Of(id: "HgdMachineIdentity.CurrentVersion")
            )
        );
        Assert.NotEqual(
            actual: core.CheckpointIdentity,
            expected: Identity(
                configuration: configuration,
                shape: "0000000000000000"
            )
        );
    }
}
