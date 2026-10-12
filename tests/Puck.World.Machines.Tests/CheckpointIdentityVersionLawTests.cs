using System.Globalization;
using Puck.AdvancedGamingBrick;
using Puck.HumbleGamingBrick;
using Puck.Machines;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>An emulator core's checkpoint identity names its snapshot layout's version, so a checkpoint of another layout
/// is a different identity and a host refuses its restore by identity.</summary>
public sealed class CheckpointIdentityVersionLawTests {
    [Fact]
    public void AnAdvancedCoresIdentityNamesTheSnapshotVersionAndNoOtherVersionIsTheSameIdentity() {
        var bios = new byte[(16 * 1024)];
        var rom = new byte[0x200];

        using var core = new AdvancedGamingBrickCore(
            bios,
            rom
        );

        Assert.Equal(
            core.CheckpointIdentity,
            MachineCheckpointIdentity.Compute(
                FormattableString.Invariant(formattable: $"puck.agb.core.v1/{AgbMachineIdentity.CurrentVersion}/False/False"),
                bios,
                rom
            )
        );
        Assert.NotEqual(
            core.CheckpointIdentity,
            MachineCheckpointIdentity.Compute(
                FormattableString.Invariant(formattable: $"puck.agb.core.v1/{(AgbMachineIdentity.CurrentVersion + 1)}/False/False"),
                bios,
                rom
            )
        );
    }
    [Fact]
    public void AHumbleCoresIdentityNamesTheSnapshotVersionAndNoOtherVersionIsTheSameIdentity() {
        var rom = new byte[0x8000];
        var configuration = HgbFirmware.CreateConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: rom,
            model: ConsoleModel.CgbE
        );

        using var core = new HumbleGamingBrickCore(configuration: configuration);

        string Identity(int version) => MachineCheckpointIdentity.Compute(
            string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"puck.hgb.core.v1/{version}/{((int)configuration.Model)}/{configuration.TickResolution.SubdivisionLog2}"
            ),
            configuration.BootRom,
            configuration.CartridgeRom
        );

        Assert.Equal(
            core.CheckpointIdentity,
            Identity(version: MachineIdentity.CurrentVersion)
        );
        Assert.NotEqual(
            core.CheckpointIdentity,
            Identity(version: (MachineIdentity.CurrentVersion + 1))
        );
    }
}
