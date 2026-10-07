using Puck.SdfVm;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldIndirectSubmissionLogLawTests {
    [Fact]
    public void RegisteringAResidencyAttachesTheEnabledDiagnosticSinkAndReportsItsAdmittedCost() {
        using var residency = new SdfWorldResidency(pipelines: SdfTestPipelines.Cache(), frameSource: new UncapturedSource(),
            kernels: SdfTestPipelines.Kernels(), name: "heavy-world", width: 1, height: 1, brickPoolVoxelCapacity: 0);
        var lines = new List<string>();
        var enabled = new WorldRenderProbe { IndirectSubmissionLog = lines.Add };

        enabled.RegisterIndirectResidency(residency, active: true);
        residency.BeginFrame();
        residency.LogIndirectSubmission("shade", queries: 1200, instructionCount: 700, frame: 42, fixedCost: 100);
        Assert.Equal("[sdf-indirect] residency=heavy-world frame=1 cache-frame=42 kind=shade queries=1200 instructions=700 estimated-cost=840100 frame-cost=0 frame-budget=134217728",
            Assert.Single(collection: lines));

        var disabled = new WorldRenderProbe();

        disabled.RegisterIndirectResidency(residency, active: true);
        residency.LogIndirectSubmission("trace", queries: 1200, instructionCount: 700, frame: 43);
        Assert.Single(collection: lines);
    }

    private sealed class UncapturedSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            throw new InvalidOperationException(message: "Submission diagnostics need no frame capture or GPU.");
    }
}
