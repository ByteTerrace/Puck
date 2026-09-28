using System.Diagnostics;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldDynamicResolutionLawTests {
    [Fact]
    public void ConfirmedTimingHoldsTheExactDemandAndHystereticExtentSequence() {
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();
        float Step(double load) {
            timing.Advance(load: load);
            return Update(controller: controller, timing: timing);
        }
        Assert.Equal(expected: 1f, actual: Step(load: 1));
        Assert.Equal(expected: 1f, actual: Step(load: 1.05));
        Assert.Equal(expected: 1f, actual: Step(load: 0.95));
        float[] down = [1f, 0.875f, 0.875f, 0.75f, 0.75f, 0.625f, 0.625f, 0.5f, 0.5f];
        for (var index = 0; (index < down.Length); index++) {
            Assert.Equal(expected: down[index], actual: Step(load: 4));
            Assert.Equal(expected: Math.Max(val1: 0.5f, val2: (1f - ((index + 1) / 16f))), actual: controller.Demand);
            Assert.Equal(expected: WorldResolutionSignal.PresentTiming, actual: controller.Signal);
        }
        float[] up = [0.5625f, 0.5625f, 0.625f, 0.625f, 0.6875f, 0.6875f, 0.75f, 0.75f,
            0.8125f, 0.8125f, 0.875f, 0.875f, 0.9375f, 0.9375f, 1f, 1f, 1f];
        for (var index = 0; (index < up.Length); index++) {
            Assert.Equal(expected: up[index], actual: Step(load: 0.25));
            Assert.Equal(expected: Math.Min(val1: 1f, val2: (0.5f + ((index + 1) / 32f))), actual: controller.Demand);
        }
    }
    [Fact]
    public void RepeatedRestartedAndReplacedPresentSourcesNeverReapplyAnInterval() {
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();
        timing.Advance(load: 4);
        Update(controller: controller, timing: timing);
        timing.Advance(load: 4);
        Update(controller: controller, timing: timing);
        Assert.Equal(expected: 0.9375f, actual: controller.Demand);
        for (var index = 0; (index < 8); index++) {
            Update(controller: controller, timing: timing);
        }
        Assert.Equal(expected: 0.9375f, actual: controller.Demand);
        Assert.Equal(expected: WorldResolutionSignal.None, actual: controller.Signal);
        timing.LastPresentTiming = new(PresentCount: 1, PresentTimestampTicks: 1);
        Update(controller: controller, timing: timing);
        var replacement = new Timing();
        replacement.Advance(load: 4);
        Update(controller: controller, timing: replacement);
        Assert.Equal(expected: 0.9375f, actual: controller.Demand);
        replacement.Advance(load: 4);
        Assert.Equal(expected: 0.875f, actual: Update(controller: controller, timing: replacement));
    }
    [Theory]
    [InlineData("vulkan")]
    [InlineData("directx")]
    public void UnavailableTimingUsesEachCompletedMarchOnceOnEitherBackend(string backend) {
        using var source = File.OpenRead(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.Counters/counters.ceilings.json"));
        var ceilings = JsonSerializer.Deserialize(utf8Json: source, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)!;
        var run = ceilings.Runs.Single(predicate: run => run.Backend == backend);
        var recordedSteps = run.Ceilings.Where(predicate: static row => row.Kind == GpuWork.MarchSteps.Name)
            .Sum(selector: static row => row.Ceiling);
        var budget = WorldDynamicResolutionBudget.Recorded.For(width: ((uint)run.Width), height: ((uint)run.Height), ceiling: 1);
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();
        var work = new Work(name: backend);
        float Step() => controller.Update(timing: timing, work: work.Ledger, displayHertz: 60, stepBudget: budget, floor: 0.5f, ceiling: 1);
        Assert.Equal(expected: 1f, actual: Step());
        work.Complete(steps: (recordedSteps * 4));
        Assert.Equal(expected: 1f, actual: Step());
        Assert.Equal(expected: ((recordedSteps * 4) / budget), actual: controller.Load);
        Assert.Equal(expected: 0.9375f, actual: controller.Demand);
        Step();
        Assert.Equal(expected: 0.9375f, actual: controller.Demand);
        Assert.Equal(expected: WorldResolutionSignal.None, actual: controller.Signal);
        work.Complete(steps: (recordedSteps * 4));
        Assert.Equal(expected: 0.875f, actual: Step());
        Assert.Equal(expected: WorldResolutionSignal.MarchSteps, actual: controller.Signal);
        work.Complete(steps: (recordedSteps / 4));
        Assert.Equal(expected: 0.9375f, actual: Step());
        Assert.Equal(expected: ((recordedSteps / 4) / budget), actual: controller.Load);
        Assert.Equal(expected: 0.90625f, actual: controller.Demand);
    }
    [Fact]
    public void ACompositeWithoutAPrimaryMarchIsNotAZeroLoadObservation() {
        var controller = new WorldDynamicResolutionController();
        var work = new Work(name: "test");
        work.Complete(steps: 400);
        Update(controller: controller, work: work.Ledger);
        work.Complete(steps: 400);
        Update(controller: controller, work: work.Ledger);
        work.Complete(steps: 0, march: false);
        Assert.Equal(expected: 0.875f, actual: Update(controller: controller, work: work.Ledger));
        Assert.Equal(expected: WorldResolutionSignal.None, actual: controller.Signal);
        work.Complete(steps: 0);
        Assert.Equal(expected: 0.9375f, actual: Update(controller: controller, work: work.Ledger));
    }
    [Fact]
    public void TimingWinsAndItsLossCannotReplayWorkObservedWhileItWasAvailable() {
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();
        var work = new Work(name: "test");
        timing.Advance(load: 1);
        Update(controller: controller, timing: timing, work: work.Ledger);
        work.Complete(steps: 400);
        timing.Advance(load: 1);
        Update(controller: controller, timing: timing, work: work.Ledger);
        Assert.Equal(expected: 1f, actual: controller.Demand);
        timing.LastPresentTiming = PresentTimingSample.Unavailable;
        Update(controller: controller, timing: timing, work: work.Ledger);
        Assert.Equal(expected: WorldResolutionSignal.None, actual: controller.Signal);
        work.Complete(steps: 400);
        Update(controller: controller, timing: timing, work: work.Ledger);
        Assert.Equal(expected: 0.9375f, actual: controller.Demand);
        Assert.Equal(expected: WorldResolutionSignal.MarchSteps, actual: controller.Signal);
    }
    [Fact]
    public void NamedBoundsUseTheExistingQuantizerAndDisabledFramesHoldTheirCeiling() {
        var controller = new WorldDynamicResolutionController();
        var work = new Work(name: "test");
        var floor = WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter);
        var ceiling = WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Half);
        for (var index = 0; (index < 16); index++) {
            work.Complete(steps: 400);
            controller.Update(timing: null, work: work.Ledger, displayHertz: 0, stepBudget: 100, floor: floor, ceiling: ceiling);
        }
        Assert.Equal(expected: floor, actual: controller.Demand);
        Assert.Equal(expected: 0.5625f, actual: controller.Scale);
        Assert.Equal(expected: 0.75f, actual: controller.Update(timing: null, work: work.Ledger,
            displayHertz: 0, stepBudget: 100, floor: floor, ceiling: ceiling, enabled: false));
        Assert.Equal(expected: ceiling, actual: controller.Demand);
        Assert.Equal(expected: WorldResolutionSignal.None, actual: controller.Signal);
    }
    [Fact]
    public void FreshScaleChangesAllocateNothingAfterInitialization() {
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();
        var changes = 0;
        void Frames() {
            for (var index = 0; (index < 64); index++) {
                var before = controller.Scale;
                timing.Advance(load: (((index % 32) < 16) ? 4 : 0.25));
                Update(controller: controller, timing: timing);
                changes += ((controller.Scale != before) ? 1 : 0);
            }
        }
        Frames();
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Frames));
        Assert.True(condition: (changes > 20));
    }
    private static float Update(WorldDynamicResolutionController controller, Timing? timing = null, IGpuWorkSource? work = null) =>
        controller.Update(timing: timing, work: work, displayHertz: 60, stepBudget: 100, floor: 0.5f, ceiling: 1);
    private sealed class Timing : IPresentTimingFeedback {
        public PresentTimingSample LastPresentTiming { get; set; }
        public void Advance(double load) => LastPresentTiming = new(PresentCount: (LastPresentTiming.PresentCount + 1),
            PresentTimestampTicks: (LastPresentTiming.PresentTimestampTicks + ((long)(Stopwatch.Frequency * load / 60))));
    }
    private sealed class Work : IGpuWorkReadback {
        private readonly GpuDeviceServices m_services;
        private long m_steps;
        public Work(string name) {
            Ledger = new GpuWorkLedger(name: ("gpu." + name), framesInFlight: 1);
            Ledger.Configure(revision: 1, passLabels: ["sdf.world$primary", "place"]);
            m_services = GpuWorkCounting.Wrap(services: new FakeGpuDevice().Services, ledger: Ledger);
        }
        public GpuWorkLedger Ledger { get; }
        public void Complete(long steps, bool march = true) {
            m_steps = steps;
            if (march) {
                Ledger.EnterPass(pass: 0);
                Ledger.LeavePass();
            } else {
                Ledger.SkipPass(pass: 0);
            }
            Ledger.EnterPass(pass: 1);
            Ledger.LeavePass();
            Ledger.ReadOnCompletion(readback: this, slot: 0);
            m_services.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);
        }
        public void AddTo(int slot, Span<long> counts, int passCount) =>
            counts[GpuWork.SubmissionKinds.Length + GpuWork.SubmissionKinds.IndexOf(value: GpuWork.MarchSteps)] += m_steps;
    }
}
