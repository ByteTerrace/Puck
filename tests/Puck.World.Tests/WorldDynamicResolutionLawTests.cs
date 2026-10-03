using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the dynamic-resolution controller (<see cref="WorldDynamicResolution"/>) chooses each frame's render
/// grid between the floor and the ceiling from one load signal, through one response. A fresh sample within
/// <see cref="WorldDynamicResolution.Deadband"/> of its budget leaves the grid; outside it the grid moves toward the scale
/// whose area meets the budget by at most <see cref="WorldDynamicResolution.MaximumFall"/> of itself down or
/// <see cref="WorldDynamicResolution.MaximumRise"/> up, clamped to the floor and the ceiling; a frame with no fresh sample
/// moves nothing. A sample is taken only at the grid the views render now: a reading of renders at another grid moves
/// nothing, and a present interval is a sample only while the views' completed renders were at the current grid
/// throughout it. When the budget falls between two adjacent grids the controller settles on the cheaper one, until a
/// sample predicts the dearer one within the budget. Against a known display rate the sample is the GPU's frame time for
/// newly timed renders against the display period, so under a present-paced display, where every kept present reads
/// exactly its period, the grid rises again when the GPU's time drops; where the device times nothing it is a new
/// present's interval against the period; where neither is available, newly completed renders' counted march steps
/// against the budget the committed counters ceilings give per output pixel, and no budget holds the ceiling. Every
/// signal answers the same trace of load against budget with the same grids. A pin overrides them all, bounded
/// by the floor and ceiling. The grids it chooses are extents the render graph quantizes, so the views' extents follow them.
/// </summary>
public sealed class WorldDynamicResolutionLawTests {
    private const float Ceiling = 0.875f;
    private const float Fall = ((float)(1d - WorldDynamicResolution.MaximumFall));
    private const float Floor = 0.5f;
    private const int Hertz = 60;
    private const long OutputPixels = (256L * 144L);
    private const float Rise = ((float)(1d + WorldDynamicResolution.MaximumRise));

    [InlineData(1f)]
    [InlineData(0.99f)]
    [InlineData(0.9376f)]
    [Theory]
    public void EveryCeilingThatQuantizesToNativeReconstructsWhileDynamicResolutionIsOn(float scale) {
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) { RenderScale = scale };

        Assert.Equal(expected: 1d, actual: new SdfViewSnapshot { RenderScale = settings.RenderCeiling }.RenderCeiling);
        settings.DynamicResolution = true;
        var view = new SdfViewSnapshot { RenderScale = settings.RenderCeiling, ResolvedRenderScale = 0.5f };

        Assert.Equal(expected: 0.875d, actual: view.RenderCeiling);
        Assert.Equal(expected: 0.5d, actual: view.RenderGrid);
        settings.DynamicResolution = false;
        Assert.Equal(expected: scale, actual: settings.RenderCeiling);
    }
    [Fact]
    public void PresentTimingHoldsKeptPeriodsAndStepsTheGridByBoundedShares() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller);

        float Present(double periods) => PresentSample(controller: controller, load: load, periods: periods);

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
    public void APresentIsASampleOnlyWhileTheViewsCompletedAtTheCurrentGrid() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller);
        var elsewhere = 0.5d;

        // Renders completing at another grid than the views render now void every interval they fall in.
        load.CompletionGrid = () => elsewhere;

        for (var miss = 0; (miss < 8); miss++) {
            load.Present(periods: 3.0);
            Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));
        }

        // Once they complete at the current grid, the next present starts the clock and the one after is a sample.
        load.CompletionGrid = null;
        load.Present(periods: 3.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));
        load.Present(periods: 3.0);
        Assert.Equal(expected: (Ceiling * Fall), actual: Advance(controller: controller, load: load), tolerance: 1e-6f);

        // A render at another grid completing inside an interval voids it, though the present ending it follows a render
        // at the current grid.
        load.CompletionGrid = () => elsewhere;
        load.CompleteFrame();
        Assert.Equal(expected: (Ceiling * Fall), actual: Advance(controller: controller, load: load), tolerance: 1e-6f);
        load.CompletionGrid = null;
        load.Present(periods: 3.0);
        Assert.Equal(expected: (Ceiling * Fall), actual: Advance(controller: controller, load: load), tolerance: 1e-6f);
        load.Present(periods: 3.0);
        Assert.Equal(expected: ((Ceiling * Fall) * Fall), actual: Advance(controller: controller, load: load), tolerance: 1e-6f);
    }
    [Fact]
    public void APresentIntervalOverRendersAtTwoGridsIsNoSample() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller);
        var current = WorldDynamicResolution.GridOf(ceiling: Ceiling, scale: Ceiling);

        // The clock starts at the current grid.
        load.Present(periods: 1.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));

        // A render at 0.5 and then one at the current grid complete between two reads; the newer alone is at the current
        // grid, but the interval spans both, so a missed period in it moves nothing and only restarts the clock.
        load.CompleteFrame(grid: 0.5d);
        load.CompleteFrame(grid: current);
        load.Present(periods: 3.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));

        // Once renders complete at the current grid alone, the next present starts the clock and the one after is a
        // sample.
        load.Present(periods: 3.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));
        load.Present(periods: 3.0);
        Assert.Equal(expected: (Ceiling * Fall), actual: Advance(controller: controller, load: load), tolerance: 1e-6f);
    }
    [Fact]
    public void UnderAPresentPacedDisplayTheGpuTimeRaisesTheGridAgain() {
        // Every kept present reads exactly the display period, as a present-paced (FIFO) swapchain reports them, so the
        // present timing alone lowers the grid on a miss and never raises it again.
        var presentOnly = new WorldDynamicResolution();
        var paced = new ScriptedLoad(controller: presentOnly);

        PresentSample(controller: presentOnly, load: paced, periods: 1.0);
        PresentSample(controller: presentOnly, load: paced, periods: 2.0);

        for (var frame = 0; (frame < 120); frame++) {
            PresentSample(controller: presentOnly, load: paced, periods: 1.0);
        }

        Assert.Equal(expected: (Ceiling * Fall), actual: presentOnly.Scale, tolerance: 1e-6f);

        // The GPU's own time shows the headroom: two periods of GPU work lower the grid, half a period raises it back to
        // the ceiling while the presents still read exactly their period.
        var controller = new WorldDynamicResolution();
        var timed = new ScriptedLoad(controller: controller) { Timed = true };

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
        // A frame with no newly timed render moves nothing.
        timed.TimeFrame(fresh: false, periods: 2.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: timed));
    }
    [Fact]
    public void TheGpuTimeLeadsThePresentTimingWhichLeadsTheSteps() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller) { BudgetPerPixel = 20d, StepsPerPixelAtFull = 60d, Timed = true };

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
        controller.Advance(ceiling: Ceiling, displayHertz: 0, floor: Floor, load: load, outputPixels: OutputPixels, pin: 0f);
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
    [InlineData(90d)]
    [InlineData(100d)]
    [InlineData(110d)]
    [Theory]
    public void TheDeadbandIncludesBothBoundaries(double load) =>
        Assert.Equal(expected: 0.8f, actual: WorldDynamicResolution.Respond(budget: 100d, ceiling: 1f, floor: 0.25f, load: load, scale: 0.8f));
    [Fact]
    public void AReadingOfRendersAtAnotherGridMovesNothing() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller) { BudgetPerPixel = 20d, StepsPerPixelAtFull = 60d, Timed = true };
        var current = WorldDynamicResolution.GridOf(ceiling: Ceiling, scale: Ceiling);

        // Four periods of GPU time read back from renders at another grid, then from renders whose grids differ.
        load.TimeFrame(grid: 0.5d, periods: 4.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));
        load.TimeFrame(grid: 0d, periods: 4.0);
        Assert.Equal(expected: Ceiling, actual: Advance(controller: controller, load: load));
        // The same time at the grid the views render now is a sample.
        load.TimeFrame(grid: current, periods: 4.0);
        Assert.Equal(expected: (Ceiling * Fall), actual: Advance(controller: controller, load: load), tolerance: 1e-6f);

        // The steps answer alike.
        var steps = new WorldDynamicResolution();
        var counted = new ScriptedLoad(controller: steps) { BudgetPerPixel = 20d };

        counted.CompleteFrame(grid: 0.5d, steps: ((long)(80d * OutputPixels)));
        Assert.Equal(expected: Ceiling, actual: Advance(controller: steps, load: counted));
        counted.CompleteFrame(grid: current, steps: ((long)(80d * OutputPixels)));
        Assert.Equal(expected: (Ceiling * Fall), actual: Advance(controller: steps, load: counted), tolerance: 1e-6f);
    }
    [InlineData(0.625f, 0.5625d)]
    [InlineData(0.8125f, 0.75d)]
    [InlineData(0.875f, 0.8125d)]
    [Theory]
    public void AMarkedGridPredictedExactlyAtBudgetClearsAndResolutionRecovers(float marked, double cheaper) {
        var controller = new WorldDynamicResolution();
        var units = (marked * 16d);
        // An integer budget proportional to the marked grid's area keeps every cheaper grid's step count integral, so
        // the counted steps meet the budget exactly at the mark.
        var load = new ScriptedLoad(controller: controller) { BudgetPerPixel = (units * units) };

        controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, load: load, outputPixels: OutputPixels, pin: marked);
        void Sample(double share) {
            load.TimeFrame(periods: share);
            load.CompleteFrame(steps: ((long)Math.Round(a: ((share * load.BudgetPerPixel) * OutputPixels))));
            Advance(controller: controller, load: load);
        }

        // Two over-budget samples reach the cheaper grid and mark the dearer one. The lighter scene meets the budget
        // exactly at the mark: 81% at 0.5625 predicts 100% at 0.625, for example, which a rounded ratio reads above 100%.
        Sample(share: 1.2d);
        Sample(share: 1.2d);
        Assert.Equal(expected: cheaper, actual: controller.Grid);
        Assert.Equal(expected: ((double)marked), actual: controller.OverGrid);
        Sample(share: ((cheaper * cheaper) / (marked * ((double)marked))));
        Assert.Equal(expected: 0d, actual: controller.OverGrid);

        for (var frame = 0; (frame < 32); frame++) {
            var ratio = (controller.Grid / marked);

            Sample(share: (ratio * ratio));
        }
        Assert.Equal(expected: ((double)marked), actual: controller.Grid);
        Assert.Equal(expected: 0d, actual: controller.OverGrid);
    }
    [InlineData(0.625f, 0.5625d)]
    [InlineData(0.8125f, 0.75d)]
    [InlineData(0.875f, 0.8125d)]
    [Theory]
    public void TheMarkClearsExactlyAtTheBudgetAndNotOneTimeAbove(float marked, double cheaper) {
        // A GPU time is no exact multiple of a 60 Hz period, so the tie is the largest time whose product with the marked
        // grid's area does not exceed the period's with the cheaper grid's, in exact rationals; the next time above it
        // keeps the mark.
        var budget = (1d / Hertz);
        var area = (((double)marked) * marked);
        var cheaperArea = (cheaper * cheaper);
        var tie = ((budget * cheaperArea) / area);

        while (!ExactlyAtMost(a: tie, b: area, c: budget, d: cheaperArea)) {
            tie = Math.BitDecrement(x: tie);
        }
        while (ExactlyAtMost(a: Math.BitIncrement(x: tie), b: area, c: budget, d: cheaperArea)) {
            tie = Math.BitIncrement(x: tie);
        }

        foreach (var (seconds, clears) in (((double Seconds, bool Clears)[])[(Math.BitIncrement(x: tie), false), (tie, true)])) {
            var controller = new WorldDynamicResolution();
            var load = new ScriptedLoad(controller: controller) { Timed = true };

            controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, load: load, outputPixels: OutputPixels, pin: marked);
            load.TimeFrame(periods: 1.2d);
            Advance(controller: controller, load: load);
            load.TimeFrame(periods: 1.2d);
            Advance(controller: controller, load: load);
            Assert.Equal(expected: cheaper, actual: controller.Grid);
            Assert.Equal(expected: ((double)marked), actual: controller.OverGrid);
            load.TimeFrame(periods: (seconds * Hertz), seconds: seconds);
            Advance(controller: controller, load: load);
            Assert.Equal(expected: (clears ? 0d : marked), actual: controller.OverGrid);
            // A held mark holds the rise below it.
            Assert.True(condition: (clears || (controller.Grid == cheaper)), userMessage: $"grid {controller.Grid}");
        }
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Theory]
    public void ABudgetBetweenTwoGridsSettlesOnTheCheaperOneAndStaysPut(int latency) {
        // The GPU's time scales with the grid's area and meets the display period at a scale of 0.594, between the grids
        // 0.5625 and 0.625, where it reads 14.946 and 18.452 milliseconds: under and over the deadband. Each render is
        // read back `latency` frames after it was rendered, at the grid it rendered.
        var meets = 0.594d;
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller) { Timed = true };
        var rendered = new Queue<double>();
        var grids = new List<double>();

        double Periods(double grid) => ((grid / meets) * (grid / meets));
        void Run(int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                rendered.Enqueue(item: load.RenderGrid());

                if (rendered.Count > latency) {
                    var grid = rendered.Dequeue();

                    load.TimeFrame(grid: grid, periods: Periods(grid: grid));
                }

                Advance(controller: controller, load: load);
                grids.Add(item: controller.Grid);
            }
        }
        int SettledAt() {
            var last = (grids.Count - 1);

            while ((last > 0) && (grids[(last - 1)] == grids[last])) {
                last--;
            }

            return last;
        }

        Assert.Equal(expected: 14.946d, actual: ((Periods(grid: 0.5625d) * 1000d) / Hertz), precision: 3);
        Assert.Equal(expected: 18.452d, actual: ((Periods(grid: 0.625d) * 1000d) / Hertz), precision: 3);
        Run(frames: 600);

        // Settled on the cheaper grid within a bounded run, measured over budget on the dearer one, and held there.
        var settled = SettledAt();

        Assert.Equal(expected: 0.5625d, actual: grids[^1]);
        Assert.Equal(expected: 0.625d, actual: controller.OverGrid);
        Assert.True(condition: (settled < (16 * (latency + 1))), userMessage: $"settled at frame {settled}");

        // A load that falls until the dearer grid is predicted within the budget clears the mark and rises onto it.
        meets = 0.66d;
        grids.Clear();
        Run(frames: 600);
        settled = SettledAt();
        Assert.Equal(expected: 0.6875d, actual: grids[^1]);
        Assert.Equal(expected: 0d, actual: controller.OverGrid);
        Assert.True(condition: (settled < (16 * (latency + 1))), userMessage: $"settled at frame {settled}");
    }
    [Fact]
    public void WithoutPresentTimingTheStepBudgetHoldsTheFrameAndNoBudgetHoldsTheCeiling() {
        var controller = new WorldDynamicResolution();
        // The views march 60 steps a pixel at the full output, a quarter of that at half its extent on each axis; the
        // budget is 20 a pixel, so the grid that meets it is the full output over the square root of three.
        var load = new ScriptedLoad(controller: controller) { BudgetPerPixel = 20d, StepsPerPixelAtFull = 60d };

        for (var frame = 0; (frame < 48); frame++) {
            load.CompleteFrame();
            Advance(controller: controller, load: load);
            Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: controller.Signal);
        }

        // Converged inside the deadband, above the floor and below the ceiling, at a budget the output's pixels scale.
        var budget = (load.BudgetPerPixel * OutputPixels);

        Assert.Equal(expected: budget, actual: controller.StepBudget);
        Assert.InRange(actual: controller.Scale, low: Floor, high: Ceiling);
        Assert.InRange(actual: ((double)load.StepsAt(grid: controller.Grid)), low: (budget * (1d - WorldDynamicResolution.Deadband)), high: (budget * (1d + WorldDynamicResolution.Deadband)));

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
        Assert.Equal(expected: Ceiling, actual: controller.Advance(ceiling: Ceiling, displayHertz: 0, floor: Floor, load: load, outputPixels: OutputPixels, pin: 0f));
        Assert.Equal(expected: WorldDynamicResolutionSignal.Steps, actual: controller.Signal);
    }
    [Fact]
    public void EverySignalAnswersOneTraceWithTheSameGrids() {
        double[] trace = [1.0, 2.0, 2.0, 1.3, 1.05, 0.95, 0.5, 0.5, 4.0, 0.0, 0.85, 1.12, 0.2, 0.2, 0.2, 3.0];
        var timed = new WorldDynamicResolution();
        var counted = new WorldDynamicResolution();
        var gpu = new WorldDynamicResolution();
        var presents = new ScriptedLoad(controller: timed);
        var steps = new ScriptedLoad(controller: counted) { BudgetPerPixel = 10d };
        var frames = new ScriptedLoad(controller: gpu) { Timed = true };

        // The first present only starts the clock, as the first after each grid move does, so the timed controller takes
        // those presents beside the trace. A present's interval is whole clock ticks, so its share of the period agrees
        // with the steps' to within a tick.
        presents.Present(periods: 1.0);
        Advance(controller: timed, load: presents);

        foreach (var ratio in trace) {
            steps.CompleteFrame(steps: ((long)Math.Round(a: ((ratio * steps.BudgetPerPixel) * OutputPixels))));
            frames.TimeFrame(periods: ratio);

            var grid = Advance(controller: counted, load: steps);

            Assert.Equal(expected: grid, actual: PresentSample(controller: timed, load: presents, periods: ratio), tolerance: 1e-6f);
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
    public void APinOverridesTheLoadAndBothBoundsHoldIt() {
        var controller = new WorldDynamicResolution();
        var load = new ScriptedLoad(controller: controller);

        load.Present(periods: 3.0);
        Assert.Equal(expected: 0.6f, actual: controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, load: load, outputPixels: OutputPixels, pin: 0.6f));
        Assert.Equal(expected: WorldDynamicResolutionSignal.Pin, actual: controller.Signal);
        Assert.Equal(expected: 0.625d, actual: controller.Grid);
        Assert.Equal(expected: Ceiling, actual: controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, load: load, outputPixels: OutputPixels, pin: 1f));
        // A pin remains inside its floor as well as its ceiling.
        Assert.Equal(expected: Floor, actual: controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, load: load, outputPixels: OutputPixels, pin: 0.25f));
    }

    private static float Advance(WorldDynamicResolution controller, IWorldFrameLoadSource load) =>
        controller.Advance(ceiling: Ceiling, displayHertz: Hertz, floor: Floor, load: load, outputPixels: OutputPixels, pin: 0f);
    // One present sample: a present after a grid move only restarts the clock, so a move is followed by that present,
    // which moves nothing.
    private static float PresentSample(WorldDynamicResolution controller, ScriptedLoad load, double periods) {
        var grid = controller.Grid;

        load.Present(periods: periods);
        var scale = Advance(controller: controller, load: load);

        if (controller.Grid != grid) {
            load.Present(periods: 1.0);
            Assert.Equal(expected: scale, actual: Advance(controller: controller, load: load));
        }

        return scale;
    }
    // a * b <= c * d over the doubles' exact rational values, an oracle independent of the controller's arithmetic.
    private static bool ExactlyAtMost(double a, double b, double c, double d) {
        static (BigInteger Significand, int Exponent) Exact(double value) {
            var bits = BitConverter.DoubleToInt64Bits(value: value);
            var exponent = ((int)((bits >> 52) & 0x7FFL));
            var fraction = bits & 0xFFFFFFFFFFFFFL;

            return ((exponent == 0) ? (fraction, -1074) : (fraction | (1L << 52), (exponent - 1075)));
        }

        var (sa, ea) = Exact(value: a);
        var (sb, eb) = Exact(value: b);
        var (sc, ec) = Exact(value: c);
        var (sd, ed) = Exact(value: d);
        var left = (sa * sb);
        var right = (sc * sd);
        var shift = ((ea + eb) - (ec + ed));

        return ((shift >= 0) ? ((left << shift) <= right) : (left <= (right << -shift)));
    }
    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(path: AppContext.BaseDirectory); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(message: "No checkout holds the test assembly.");
    }

    // A load whose timed renders, presents and completed renders the law scripts, each read once: a timed render takes
    // the given number of display periods of GPU time, each Present advances the present count by one at the given
    // number of display periods after the last and follows a completed render, and each completed render counts either
    // the given steps or those the views march at its grid, proportional to the grid's area. A render is at the grid
    // the controller last chose unless the law names another. An untimed load reads no GPU time.
    private sealed class ScriptedLoad(WorldDynamicResolution controller) : IWorldFrameLoadSource {
        private WorldFrameLoadReading m_completed;
        private ShaderPipelineCompletions m_completions;
        private bool m_counts;
        private WorldFrameLoadReading m_timed;
        private bool m_times;
        private uint m_presents;
        private long m_ticks;

        public double BudgetPerPixel { get; set; }
        public Func<double>? CompletionGrid { get; set; }
        public PresentTimingSample LastPresentTiming { get; private set; }
        public double MarchStepBudgetPerPixel => BudgetPerPixel;
        public double StepsPerPixelAtFull { get; init; }
        public bool Timed { get; set; }

        public double RenderGrid() => ((controller.Grid > 0d)
            ? controller.Grid
            : WorldDynamicResolution.GridOf(ceiling: Ceiling, scale: Ceiling));
        public void RequireGpuTiming(bool required) { }
        public void RequireCompletions(bool required) { }
        public bool TryReadGpuFrame(out WorldFrameLoadReading reading) {
            reading = m_timed;
            m_timed = (m_timed with { Renders = 0 });

            return (Timed && m_times);
        }
        public void TimeFrame(double periods, bool fresh = true, double? grid = null, double? seconds = null) {
            if (fresh) {
                m_times = true;
                m_timed = new WorldFrameLoadReading(Grid: (grid ?? RenderGrid()), Load: (seconds ?? (periods / Hertz)), Renders: 1);
            }
        }
        public void Unpresent() => LastPresentTiming = PresentTimingSample.Unavailable;
        public void CompleteFrame(long? steps = null, double? grid = null) {
            var at = (grid ?? (CompletionGrid?.Invoke() ?? RenderGrid()));

            m_counts = true;
            m_completed = new WorldFrameLoadReading(Grid: at, Load: (steps ?? StepsAt(grid: at)), Renders: 1);
            m_completions = m_completions.Then(later: new ShaderPipelineCompletions(Grid: at, Renders: 1));
        }
        public void Present(double periods) {
            CompleteFrame();
            m_ticks += ((m_ticks == 0L)
                ? Stopwatch.Frequency
                : ((long)Math.Round(a: ((periods * Stopwatch.Frequency) / Hertz))));
            m_presents++;
            LastPresentTiming = new PresentTimingSample(PresentCount: m_presents, PresentTimestampTicks: m_ticks);
        }
        public long StepsAt(double grid) => ((long)((StepsPerPixelAtFull * OutputPixels) * (grid * grid)));
        public bool TryReadMarchSteps(out WorldFrameLoadReading reading) {
            reading = m_completed;
            m_completed = (m_completed with { Renders = 0 });

            return m_counts;
        }
        public ShaderPipelineCompletions TakeCompletions() {
            var completions = m_completions;

            m_completions = default;

            return completions;
        }
    }
}
