using System.Diagnostics;
using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the dynamic-resolution controller (<see cref="WorldDynamicResolution"/>) chooses each frame's render
/// grid between the floor and the ceiling from one load signal, through one response. A fresh sample within
/// <see cref="WorldDynamicResolution.Deadband"/> of its budget leaves the grid; outside it the grid moves toward the scale
/// whose area meets the budget by at most <see cref="WorldDynamicResolution.MaximumFall"/> of itself down or
/// <see cref="WorldDynamicResolution.MaximumRise"/> up, clamped to the floor and the ceiling; a frame with no fresh sample
/// moves nothing. Against a known display rate the sample is the GPU's frame time for a newly timed frame against the
/// display period, so under a present-paced display, where every kept present reads exactly its period, the grid rises
/// again when the GPU's time drops; where the device times nothing it is a new present's interval against the period;
/// where neither is available, a completed frame's counted march steps against the budget the committed counters
/// ceilings give per output pixel, and no budget holds the ceiling. Every signal answers the same trace of load against
/// budget with the same grids. A forced grid overrides them all, bounded by the ceiling. The grids it chooses are extents
/// the render graph quantizes, so the views' extents follow them.
/// </summary>
public sealed class WorldDynamicResolutionLawTests {
    private const float Ceiling = 0.875f;
    private const float Fall = ((float)(1d - WorldDynamicResolution.MaximumFall));
    private const float Floor = 0.5f;
    private const int Hertz = 60;
    private const long OutputPixels = (256L * 144L);
    private const float Rise = ((float)(1d + WorldDynamicResolution.MaximumRise));

    [Fact]
    public void PresentTimingHoldsKeptPeriodsAndStepsTheGridByBoundedShares() {
        var load = new ScriptedLoad();
        var controller = new WorldDynamicResolution();

        float Present(double periods) {
            load.Present(periods: periods);

            return Advance(controller: controller, load: load);
        }

        // Kept periods, and any within the deadband, hold the ceiling; the first present only starts the clock.
        Assert.Equal(expected: Ceiling, actual: Present(periods: 3.0));

        foreach (var periods in ((double[])[1.0, 1.05, 0.95, 1.1, 0.9])) {
            Assert.Equal(expected: Ceiling, actual: Present(periods: periods));
        }

        Assert.Equal(expected: WorldDynamicResolutionSignal.Present, actual: controller.Signal);

        // A missed period lowers the grid by the bounded share, never by the whole miss, down to the floor.
        var expected = Ceiling;

        for (var miss = 0; (miss < 12); miss++) {
            expected = Math.Max(val1: Floor, val2: ((float)(expected * (1d - WorldDynamicResolution.MaximumFall))));
            Assert.Equal(expected: expected, actual: Present(periods: 2.0), tolerance: 1e-6f);
        }

        Assert.Equal(expected: Floor, actual: controller.Scale);
        // A frame with no new present moves nothing.
        Assert.Equal(expected: Floor, actual: Advance(controller: controller, load: load));

        // Short intervals raise it by the bounded share, up to the ceiling and no further.
        expected = Floor;

        for (var fast = 0; (fast < 24); fast++) {
            expected = Math.Min(val1: Ceiling, val2: ((float)(expected * (1d + WorldDynamicResolution.MaximumRise))));
            Assert.Equal(expected: expected, actual: Present(periods: 0.5), tolerance: 1e-6f);
        }

        Assert.Equal(expected: Ceiling, actual: controller.Scale);
        // The extents follow: the render graph quantizes each chosen grid, and the rise moved it up its steps.
        Assert.True(condition: (RenderGraphExtent.Quantize(fraction: controller.Scale) > RenderGraphExtent.Quantize(fraction: Floor)));
    }
    [Fact]
    public void UnderAPresentPacedDisplayTheGpuTimeRaisesTheGridAgain() {
        // Every kept present reads exactly the display period, as a present-paced (FIFO) swapchain reports them, so the
        // present timing alone lowers the grid on a miss and never raises it again.
        var paced = new ScriptedLoad();
        var presentOnly = new WorldDynamicResolution();

        paced.Present(periods: 1.0);
        Advance(controller: presentOnly, load: paced);
        paced.Present(periods: 2.0);
        Advance(controller: presentOnly, load: paced);

        for (var frame = 0; (frame < 120); frame++) {
            paced.Present(periods: 1.0);
            Advance(controller: presentOnly, load: paced);
        }

        Assert.Equal(expected: (Ceiling * Fall), actual: presentOnly.Scale, tolerance: 1e-6f);

        // The GPU's own time shows the headroom: two periods of GPU work lower the grid, half a period raises it back to
        // the ceiling while the presents still read exactly their period.
        var timed = new ScriptedLoad { Timed = true };
        var controller = new WorldDynamicResolution();

        for (var frame = 0; (frame < 8); frame++) {
            timed.Present(periods: 1.0);
            timed.TimeFrame(periods: 2.0);
            Advance(controller: controller, load: timed);
        }

        var lowered = controller.Scale;

        Assert.Equal(expected: WorldDynamicResolutionSignal.Gpu, actual: controller.Signal);
        Assert.True(condition: (lowered < Ceiling));

        for (var frame = 0; (frame < 64); frame++) {
            timed.Present(periods: 1.0);
            timed.TimeFrame(periods: 0.5);
            Advance(controller: controller, load: timed);
        }

        Assert.Equal(expected: Ceiling, actual: controller.Scale);
        // A frame with no newly timed frame moves nothing.
        timed.TimeFrame(fresh: false, periods: 2.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: timed));
    }
    [Fact]
    public void TheGpuTimeLeadsThePresentTimingWhichLeadsTheSteps() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad { BudgetPerPixel = 20d, StepsPerPixelAtFull = 60d, Timed = true };

        load.Present(periods: 1.0);
        load.TimeFrame(periods: 1.0);
        load.CompleteFrame();
        Advance(controller: controller, load: load);
        Assert.Equal(expected: WorldDynamicResolutionSignal.Gpu, actual: controller.Signal);

        // A device that times nothing falls to the presents.
        load.Timed = false;
        Advance(controller: controller, load: load);
        Assert.Equal(expected: WorldDynamicResolutionSignal.Present, actual: controller.Signal);

        // With neither, the steps.
        load.Unpresent();
        Advance(controller: controller, load: load);
        Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: controller.Signal);

        // A timed GPU without a known display rate has no period to be held to, so it falls to the steps too.
        load.Timed = true;
        load.Present(periods: 1.0);
        controller.Advance(ceiling: Ceiling, displayHertz: 0, floor: Floor, forced: 0f, load: load, outputPixels: OutputPixels);
        Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: controller.Signal);
    }
    [Fact]
    public void ASampleOutsideTheDeadbandMovesOnlyAsFarAsItsShareAsks() {
        // Steps that scale with the area at 1.1236 times the budget ask for the grid over 1.06, which is inside the fall bound.
        Assert.Equal(expected: (0.8f / 1.06f), actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.25f, load: 112.36d, scale: 0.8f), tolerance: 1e-6f);
        // At 0.81 of the budget, the grid over 0.9 is past the rise bound, which holds it to its share.
        Assert.Equal(expected: (0.8f * Rise), actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.25f, load: 81d, scale: 0.8f), tolerance: 1e-6f);
        // A frame that marches nothing raises by the most; inside the deadband nothing moves.
        Assert.Equal(expected: (0.8f * Rise), actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.25f, load: 0d, scale: 0.8f), tolerance: 1e-6f);
        Assert.Equal(expected: 0.8f, actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.25f, load: 109d, scale: 0.8f));
        // The floor and the ceiling clamp what the bounds allow.
        Assert.Equal(expected: 0.78f, actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.78f, load: 400d, scale: 0.8f));
        Assert.Equal(expected: 0.81f, actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 0.81f, floor: 0.25f, load: 1d, scale: 0.8f));
        Assert.Equal(expected: (0.8f * Fall), actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.25f, load: 400d, scale: 0.8f), tolerance: 1e-6f);
    }
    [Fact]
    public void WithoutPresentTimingTheStepBudgetHoldsTheFrameAndNoBudgetHoldsTheCeiling() {
        var controller = new WorldDynamicResolution();
        // The views march 60 steps a pixel at the full output, a quarter of that at half its extent on each axis; the
        // budget is 20 a pixel, so the grid that meets it is the full output over the square root of three.
        var load = new ScriptedLoad { BudgetPerPixel = 20d, StepsPerPixelAtFull = 60d };

        load.Grid = () => controller.Scale;

        for (var frame = 0; (frame < 48); frame++) {
            load.CompleteFrame();
            Advance(controller: controller, load: load);
            Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: controller.Signal);
        }

        // Converged inside the deadband, above the floor and below the ceiling, at a budget the output's pixels scale.
        var budget = (load.BudgetPerPixel * OutputPixels);

        Assert.Equal(expected: budget, actual: controller.StepBudget);
        Assert.InRange(actual: controller.Scale, low: Floor, high: Ceiling);
        Assert.InRange(actual: ((double)load.StepsAt(grid: controller.Scale)), low: (budget * (1d - WorldDynamicResolution.Deadband)), high: (budget * (1d + WorldDynamicResolution.Deadband)));

        // A frame no new completion follows moves nothing.
        var held = controller.Scale;

        Assert.Equal(expected: held, actual: Advance(controller: controller, load: load));

        // A budget the floor cannot meet stops at the floor.
        load.BudgetPerPixel = 1d;

        for (var frame = 0; (frame < 48); frame++) {
            load.CompleteFrame();
            Advance(controller: controller, load: load);
        }

        Assert.Equal(expected: Floor, actual: controller.Scale);

        // No budget holds the ceiling, and an unknown display rate reads the steps rather than the presents.
        load.BudgetPerPixel = 0d;
        load.Present(periods: 2.0);
        Assert.Equal(expected: Ceiling, actual: controller.Advance(ceiling: Ceiling, displayHertz: 0, floor: Floor, forced: 0f, load: load, outputPixels: OutputPixels));
        Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: controller.Signal);
    }
    [Fact]
    public void EverySignalAnswersOneTraceWithTheSameGrids() {
        double[] trace = [1.0, 2.0, 2.0, 1.3, 1.05, 0.95, 0.5, 0.5, 4.0, 0.0, 0.85, 1.12, 0.2, 0.2, 0.2, 3.0];
        var timed = new WorldDynamicResolution();
        var counted = new WorldDynamicResolution();
        var gpu = new WorldDynamicResolution();
        var presents = new ScriptedLoad();
        var steps = new ScriptedLoad { BudgetPerPixel = 10d };
        var frames = new ScriptedLoad { Timed = true };

        // The first present only starts the clock, so the timed controller takes one present ahead of the trace. A present's
        // interval is whole clock ticks, so its share of the period agrees with the steps' to within a tick.
        presents.Present(periods: 1.0);
        Advance(controller: timed, load: presents);

        foreach (var ratio in trace) {
            presents.Present(periods: ratio);
            steps.CompleteFrame(steps: ((long)Math.Round(a: ((ratio * steps.BudgetPerPixel) * OutputPixels))));
            frames.TimeFrame(periods: ratio);

            var grid = Advance(controller: counted, load: steps);

            Assert.Equal(expected: grid, actual: Advance(controller: timed, load: presents), tolerance: 1e-6f);
            Assert.Equal(expected: grid, actual: Advance(controller: gpu, load: frames), tolerance: 1e-6f);
        }

        Assert.Equal(expected: WorldDynamicResolutionSignal.Gpu, actual: gpu.Signal);
        Assert.Equal(expected: WorldDynamicResolutionSignal.Present, actual: timed.Signal);
        Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: counted.Signal);
    }
    [Fact]
    public void TheStepBudgetIsTheCommittedFloorCeilingsPerOutputPixel() {
        var path = Path.Combine(path1: RepositoryRoot(), path2: "tests/Puck.Counters/counters.ceilings.json");
        var ceilings = JsonSerializer.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings, utf8Json: File.OpenRead(path: path))!;

        foreach (var run in ceilings.Runs) {
            var steps = run.Ceilings
                .Where(predicate: static ceiling => ((ceiling.Node == WorldDynamicResolution.BudgetNode) && (ceiling.Kind == "gpu.march.steps")))
                .Sum(selector: static ceiling => ceiling.Ceiling);

            Assert.True(condition: (steps > 0L), userMessage: $"the {run.Backend} run records no march steps for the world node");
            Assert.Equal(expected: (((double)steps) / (((long)run.Width) * run.Height)), actual: WorldDynamicResolution.StepBudgetPerPixel(backend: run.Backend, ceilings: ceilings));
        }

        // A backend no run was recorded on reads the first run; a document with no run gives no budget.
        Assert.Equal(expected: WorldDynamicResolution.StepBudgetPerPixel(backend: ceilings.Runs[0].Backend, ceilings: ceilings), actual: WorldDynamicResolution.StepBudgetPerPixel(backend: "metal", ceilings: ceilings));
        Assert.Equal(expected: 0d, actual: WorldDynamicResolution.StepBudgetPerPixel(backend: "vulkan", ceilings: (ceilings with { Runs = [] })));
    }
    [Fact]
    public void AForcedGridOverridesTheLoadAndTheCeilingBoundsIt() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad();

        load.Present(periods: 3.0);
        Assert.Equal(expected: 0.6f, actual: controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, forced: 0.6f, load: load, outputPixels: OutputPixels));
        Assert.Equal(expected: WorldDynamicResolutionSignal.Forced, actual: controller.Signal);
        Assert.Equal(expected: Ceiling, actual: controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, forced: 1f, load: load, outputPixels: OutputPixels));
        // Below the floor too: a sweep forces any extent the ceiling allows.
        Assert.Equal(expected: 0.25f, actual: controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, forced: 0.25f, load: load, outputPixels: OutputPixels));
    }

    private static float Advance(WorldDynamicResolution controller, ScriptedLoad load) =>
        controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, forced: 0f, load: load, outputPixels: OutputPixels);
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(path: AppContext.BaseDirectory); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(message: "No checkout holds the test assembly.");
    }

    // A load whose timed frames, presents and march steps the law scripts: each timed frame takes the given number of
    // display periods of GPU time, each Present advances the present count by one at the given number of display periods
    // after the last, and each completed frame counts either the given steps or those the views would at the grid the
    // controller last chose, proportional to the grid's area. An untimed load reads no GPU time.
    private sealed class ScriptedLoad : IWorldFrameLoadSource {
        private long m_frame;
        private long m_gpuFrame;
        private double m_gpuSeconds;
        private uint m_presents;
        private long? m_steps;
        private long m_ticks;

        public double BudgetPerPixel { get; set; }
        public Func<float> Grid { get; set; } = static () => Ceiling;
        public PresentTimingSample LastPresentTiming { get; private set; }
        public double MarchStepBudgetPerPixel => BudgetPerPixel;
        public double StepsPerPixelAtFull { get; init; }
        public bool Timed { get; set; }

        public void RequireGpuTiming(bool required) { }
        public bool TryReadGpuFrame(out long frame, out double seconds) {
            frame = m_gpuFrame;
            seconds = m_gpuSeconds;

            return (Timed && (m_gpuFrame != 0L));
        }
        public void TimeFrame(double periods, bool fresh = true) {
            if (fresh) {
                m_gpuFrame++;
            }

            m_gpuSeconds = (periods / Hertz);
        }
        public void Unpresent() => LastPresentTiming = PresentTimingSample.Unavailable;
        public void CompleteFrame(long? steps = null) {
            m_frame++;
            m_steps = steps;
        }
        public void Present(double periods) {
            m_ticks += ((m_ticks == 0L)
                ? Stopwatch.Frequency
                : ((long)Math.Round(a: ((periods * Stopwatch.Frequency) / Hertz))));
            m_presents++;
            LastPresentTiming = new PresentTimingSample(PresentCount: m_presents, PresentTimestampTicks: m_ticks);
        }
        public long StepsAt(float grid) {
            var scale = ((grid > 0f) ? grid : Ceiling);

            return ((long)((StepsPerPixelAtFull * OutputPixels) * (((double)scale) * scale)));
        }
        public bool TryReadMarchSteps(out long frame, out long steps) {
            frame = m_frame;
            steps = (m_steps ?? StepsAt(grid: Grid()));

            return (m_frame != 0L);
        }
    }
}
