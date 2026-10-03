using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a load reading sums the views' renders read back since the previous reading
/// (<see cref="WorldFrameLoadAggregate"/>). Each render counts once; a view whose latest render was already counted, a
/// standing view, adds no load, no freshness and no grid, so a reading is fresh whenever any view rendered anew, and the
/// controller reads a rising load on one view while another stands. A reading's grid is its renders' common grid, zero
/// when they differ. A new set of views keeps what each surviving view counted.
/// </summary>
public sealed class WorldFrameLoadAggregateLawTests {
    private const int Hertz = 60;

    [Fact]
    public void AStandingViewAddsNothingAndAnotherViewsNewRendersAreRead() {
        var aggregate = new WorldFrameLoadAggregate();

        aggregate.Reset(survivors: [-1, -1]);
        Assert.Equal(expected: new WorldFrameLoadReading(Grid: 0.75d, Load: 8d, Renders: 2), actual: Read(aggregate: aggregate, (0, 1L, 4d, 0.75d), (1, 1L, 4d, 0.75d)));

        // View 0 stands on its counted render while view 1 renders on: only view 1's renders are read, as they rise.
        for (var render = 2L; (render < 6L); render++) {
            Assert.Equal(expected: new WorldFrameLoadReading(Grid: 0.75d, Load: (2d * render), Renders: 1), actual: Read(aggregate: aggregate, (0, 1L, 4d, 0.75d), (1, render, (2d * render), 0.75d)));
        }

        // Neither rendered anew: nothing is fresh, and a stale grid names nothing.
        var stale = Read(aggregate: aggregate, (0, 1L, 4d, 0.5d), (1, 5L, 10d, 0.5d));

        Assert.False(condition: stale.IsFresh);
        Assert.Equal(expected: 0d, actual: stale.Grid);

        // Renders at different grids name no grid.
        Assert.Equal(expected: new WorldFrameLoadReading(Grid: 0d, Load: 7d, Renders: 2), actual: Read(aggregate: aggregate, (0, 2L, 3d, 0.75d), (1, 6L, 4d, 0.625d)));

        // A new set keeps what a surviving view counted: the old view 1 is the new view 0, and the new view 1 is new.
        aggregate.Reset(survivors: [1, -1]);
        Assert.Equal(expected: new WorldFrameLoadReading(Grid: 0.75d, Load: 1d, Renders: 1), actual: Read(aggregate: aggregate, (0, 6L, 4d, 0.75d), (1, 1L, 1d, 0.75d)));
    }
    [Fact]
    public void ARisingLoadOnOneViewLowersTheGridWhileAnotherStands() {
        // View 0 renders once and stands; view 1 renders every frame, its GPU time rising from a quarter of the display
        // period to a whole period and a half.
        var controller = new WorldDynamicResolution();
        var load = new TwoViews(controller: controller);

        for (var frame = 0; (frame < 48); frame++) {
            var standing = (frame > 0);

            load.Render(first: !standing, periods: (0.25d + (frame / 32d)));
            controller.Advance(ceiling: 0.875f, displayHertz: Hertz, floor: 0.5f, load: load, outputPixels: (256L * 144L), pin: 0f);
        }

        Assert.Equal(expected: WorldDynamicResolutionSignal.Gpu, actual: controller.Signal);
        Assert.True(condition: (controller.Grid < 0.875d), userMessage: $"the grid held at {controller.Grid}");
    }

    private static WorldFrameLoadReading Read(WorldFrameLoadAggregate aggregate, params (int View, long Render, double Load, double Grid)[] views) {
        aggregate.Begin();

        foreach (var (view, render, load, grid) in views) {
            aggregate.Add(grid: grid, load: load, render: render, view: view);
        }

        return aggregate.End();
    }

    // Two views timed through one aggregate, each render at the grid the controller last chose: view 0 renders only when
    // asked, view 1 every frame.
    private sealed class TwoViews(WorldDynamicResolution controller) : IWorldFrameLoadSource {
        private readonly WorldFrameLoadAggregate m_aggregate = CreateAggregate();

        private long m_first;
        private long m_second;
        private double m_secondSeconds;

        public PresentTimingSample LastPresentTiming => PresentTimingSample.Unavailable;
        public double MarchStepBudgetPerPixel => 0d;

        public void Render(bool first, double periods) {
            if (first) {
                m_first++;
            }

            m_second++;
            m_secondSeconds = (periods / Hertz);
        }
        public void RequireGpuTiming(bool required) { }
        public void RequireCompletions(bool required) { }
        public bool TryReadGpuFrame(out WorldFrameLoadReading reading) {
            var grid = ((controller.Grid > 0d) ? controller.Grid : WorldDynamicResolution.GridOf(ceiling: 0.875f, scale: 0.875f));

            m_aggregate.Begin();
            m_aggregate.Add(grid: grid, load: (0.25d / Hertz), render: m_first, view: 0);
            m_aggregate.Add(grid: grid, load: m_secondSeconds, render: m_second, view: 1);
            reading = m_aggregate.End();

            return true;
        }
        public bool TryReadMarchSteps(out WorldFrameLoadReading reading) {
            reading = default;

            return false;
        }
        public ShaderPipelineCompletions TakeCompletions() => default;

        private static WorldFrameLoadAggregate CreateAggregate() {
            var aggregate = new WorldFrameLoadAggregate();

            aggregate.Reset(survivors: [-1, -1]);

            return aggregate;
        }
    }
}
