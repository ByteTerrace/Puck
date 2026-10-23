using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Testing;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldCaptureBudgetLawTests {
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [Theory]
    public async Task EachAcceptedCaptureGetsItsOwnBudgetAndTheWorkCounterKeepsTheRunTotal(bool scheduled, bool building, bool firstServed) {
        using var directory = new TemporaryDirectory();
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument() with {
            Captures = new WorldCapturesSection(Directory: directory.RootPath, Rows: (scheduled
                ? [new WorldCaptureRow(Station: CellName.Parse(candidate: "sample"), Ticks: [1, 2],
                    Palette: [new WorldCapturePaletteEntry(Color: "#000000", Material: 0)])] : [])),
        });
        var target = new Target();
        var scheduler = new WorldCaptureScheduler(server: row.Server, directory: directory.RootPath,
            backend: "vulkan", worldFile: "fixture.world.json", captureTarget: _ => target,
            readiness: new Readiness(Building: building));
        var budget = (building ? WorldCaptureScheduler.BuildHoldBudgetTicks : WorldCaptureScheduler.HoldBudgetTicks);

        for (var capture = 1UL; (capture <= 2UL); capture++) {
            scheduler.PublishTick(tick: capture);
            if (!scheduled) {
                scheduler.ArmUnscheduled(target: target,
                    request: new FrameCaptureRequest(path: Path.Join(path1: directory.RootPath, path2: $"capture-{capture}.png")));
            }
            Assert.True(condition: scheduler.HoldsClock(withheldTicks: (budget - 1)));
            Assert.True(condition: scheduler.HoldsClock(withheldTicks: 0));
            Assert.False(condition: target.Request!.Completion.IsCompleted);
            Assert.True(condition: scheduler.HoldsClock(withheldTicks: 1));
            if ((capture == 1) && firstServed) {
                target.Request.Write(tick: capture, writer: path => PngEncoder.Write(height: 1,
                    path: path, rgba: [0, 0, 0, 255], width: 1));
            } else {
                Assert.False(condition: scheduler.HoldsClock(withheldTicks: 0));
                Assert.IsType<OperationCanceledException>(@object: (await target.Request.Completion).Error);
            }
        }
        Assert.True(condition: scheduler.Work.TryRead(kind: WorldCaptureScheduler.HeldTicks, value: out var held));
        Assert.Equal(actual: held, expected: checked((long)(2 * budget)));
        Assert.False(condition: scheduler.AwaitsFrame);
        if (scheduled) {
            Assert.Equal(expected: 2, actual: scheduler.Entries.Count);
            Assert.Equal(expected: (firstServed ? null : WorldCaptureRefusal.Unserved), actual: scheduler.Entries[0].Refusal);
            Assert.Equal(expected: WorldCaptureRefusal.Unserved, actual: scheduler.Entries[1].Refusal);
        }
    }

    /// <summary>Under lockstep an ordinary settled still converges over the samples the host names, and is stamped with
    /// the tick it is held at; a still that already converges keeps its own, and a presented capture or a still already
    /// claimed takes no convergence.</summary>
    [Fact]
    public void ALockstepStillConvergesOverAFreshSequence() {
        using var directory = new TemporaryDirectory();
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var samples = 8;
        var scheduler = new WorldCaptureScheduler(server: row.Server, directory: directory.RootPath, backend: "vulkan",
            worldFile: "fixture.world.json", captureTarget: null, stillSamples: () => samples);
        var still = new FrameCaptureRequest(path: Path.Join(path1: directory.RootPath, path2: "still.png"));

        scheduler.PublishTick(tick: 3);
        scheduler.ArmUnscheduled(request: still, target: new Target());
        Assert.Equal(expected: 8, actual: still.Converge);
        Assert.Equal(expected: 3UL, actual: still.StateTick);
        Assert.StartsWith(expectedStartString: "tick=3 hash=", actualString: still.StateStamp, comparisonType: StringComparison.Ordinal);

        var converging = new FrameCaptureRequest(converge: 2, path: Path.Join(path1: directory.RootPath, path2: "converging.png"));

        scheduler.ArmUnscheduled(request: converging, target: new Target());
        Assert.Equal(expected: 2, actual: converging.Converge);

        var presented = FrameCaptureRequest.Presented(path: Path.Join(path1: directory.RootPath, path2: "presented.png"));

        samples = 0;
        var ordinary = new FrameCaptureRequest(path: Path.Join(path1: directory.RootPath, path2: "ordinary.png"));

        scheduler.ArmUnscheduled(request: ordinary, target: new Target());
        Assert.Equal(expected: 0, actual: ordinary.Converge);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ordinary.ConvergeOver(samples: 0));
        Assert.Throws<InvalidOperationException>(testCode: () => presented.ConvergeOver(samples: 1));
        _ = ordinary.TryFail(error: new InvalidOperationException());
        Assert.Throws<InvalidOperationException>(testCode: () => ordinary.ConvergeOver(samples: 1));
    }

    private sealed class Target : ICaptureRequestTarget {
        private readonly CaptureRequestSlot m_slot = new();

        public string? PendingCapturePath => m_slot.PendingPath;
        public FrameCaptureRequest? Request { get; private set; }

        public void RequestCapture(FrameCaptureRequest request) {
            m_slot.Arm(request: request, pendingPath: m_slot.PendingPath);
            Request = request;
        }
    }
    private sealed record Readiness(bool Building) : IWorldEngineReadiness {
        public bool CapturesSettled => true;
        public bool IsReady => !Building;
        public string? NotReadyReason => (Building ? "the engine's pipeline set is building" : null);
    }
}
