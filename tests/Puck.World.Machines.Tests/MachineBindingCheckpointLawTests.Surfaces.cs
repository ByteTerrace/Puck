using Puck.World.Server;
using Puck.Abstractions.Machines;
using Puck.Testing;
using Xunit;

namespace Puck.World.Machines.Tests;

public sealed partial class MachineBindingCheckpointLawTests {
    [Fact]
    public void ASeekOverASteppedMachineSaysItsCoresAreOutsideTheProof() {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-verdict-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        using var harness = new WorldHistoryHarness(
            definition: Document(rom, Binding(address: SendAddress, name: "send", row: "send", update: "onChange")),
            seats: 0
        );

        harness.Steps(count: 3);

        Assert.True(condition: harness.Fixture.Server.AnyMachineEverPumped);
        Assert.True(condition: harness.SeekAndProve(target: harness.History.OldestTick!.Value).MachineCoresOutsideProof);
    }
    [Fact]
    public void ASeekWithNoSteppedMachineClaimsNoMachineCoreOutsideItsProof() {
        using var harness = new WorldHistoryHarness(seats: 0);

        harness.Steps(count: 3);

        Assert.False(condition: harness.Fixture.Server.AnyMachineEverPumped);
        Assert.False(condition: harness.SeekAndProve(target: harness.History.OldestTick!.Value).MachineCoresOutsideProof);
    }
    [Fact]
    public void ACheckpointCarriesTheMemoInBindingOrderAndRefusesAnyOther() {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-order-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        using var fixture = Fixtures.FreshServer(
            definition: Document(
                rom,
                Binding(address: (SendAddress + 4UL), name: "zeta", row: "zeta", update: "onChange"),
                Binding(address: (SendAddress + 8UL), name: "alpha", row: "alpha", update: "onChange")
            ),
            machineCatalog: TestMachines.Catalog()
        );

        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var captured,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: ["alpha", "zeta"],
            actual: captured!.Server.MachineBindings.Select(selector: static entry => entry.Binding)
        );
        Assert.All(
            collection: captured.Server.MachineBindings,
            action: static entry => Assert.Equal(expected: (MachineAccessStatus.Available, ((long?)FirstValue)), actual: (entry.State.Status, entry.State.LastValue))
        );

        var reversed = captured with {
            Server = captured.Server with { MachineBindings = [.. captured.Server.MachineBindings.Reverse()] },
        };

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: reversed),
            checkpoint: out _,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "binding order"
        );
    }
}
