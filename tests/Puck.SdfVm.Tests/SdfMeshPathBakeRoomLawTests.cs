using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Xunit;
using static Puck.SdfVm.Tests.SdfMeshPathBakeLawTests;

namespace Puck.SdfVm.Tests;

// A bake on an account whose free memory leaves authoring no room past its margin, only the gap between the margin and
// the headroom that the forced job and the progress floors may spend: every admission and refinement goes through the
// forced allowance, one step at a time, and every readback through a progress floor beside them. The account half of the
// ordering (a forced head leaves a readback window, passed reservations stay under one forced job) is
// MemoryAdmissionLawTests'. The refinement queue is one for the process, so the law runs alone.
[Collection(name: ProcessWorkCollection.Name)]
public sealed class SdfMeshPathBakeRoomLawTests {
    [Fact]
    public void ABakeLeftOnlyTheForcedGapMeshesEveryGroupAndGivesEveryReservationBack() {
        var account = new MemoryAccount(availablePhysicalBytes: static () => (2L << 30), clock: static () => 1L, installedPhysicalBytes: (16L << 30), reclaim: static () => { });
        var (program, materials) = Program(shapes: Shapes);
        using var run = new MeshBakeTestRun(new SdfMeshPathBake { HostAccount = account, PlannedTier = MeshBakeTestRun.PlannedTier, Residency = "capped" });
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        Assert.Equal(expected: (2L << 30), actual: account.AuthoringMargin);
        _ = run.Dresser.Dress(frame: frame);
        Drive(bake: run.Bake, dresser: run.Dresser, frame: frame, materials: materials, shapes: Shapes, afterStep: () => {
            var host = account.ReadAdmission(pool: PhysicalMemory.Host);

            // Authoring has no room of its own, and what the bake holds and has promised, the forced job and the
            // reservations it passed on included, stays under the machine's floor.
            Assert.Equal(expected: 0L, actual: host.AuthoringRoom);
            Assert.True(condition: ((host.AuthoringHeld + host.AuthoringPromised) <= host.MachineLimit!.Value), userMessage: $"held {host.AuthoringHeld} promised {host.AuthoringPromised} against {host.MachineLimit}");
        });

        var description = run.Bake.Describe();
        var after = account.ReadAdmission(pool: PhysicalMemory.Host);

        // Every group meshed, through the forced allowance more than once.
        Assert.Contains(expectedSubstring: "groups=2 meshes=2", actualString: description);
        Assert.True(condition: (after.ForcedJobs > 1L), userMessage: $"forced jobs {after.ForcedJobs}: {description}");
        // Done gives every reservation back, the forced allowance and the passed total with it.
        Assert.Equal(expected: (0L, 0L, 0L), actual: (after.Forced, after.Passed, after.AuthoringPromised));
    }
}
