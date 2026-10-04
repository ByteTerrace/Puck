using Microsoft.Extensions.DependencyInjection;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a layout transition's render-scale dip reaches each view the presenter dresses as the render grid
/// inside its ceiling (<see cref="SdfViewSnapshot.ResolvedRenderScale"/>) and never as the ceiling itself
/// (<see cref="SdfViewSnapshot.RenderScale"/>), which sizes the view's scratch and chooses its graph, so a transition moves
/// no allocation and rebuilds nothing (SdfWorldPassesLawTests holds the graph to that). A view at a native ceiling
/// reconstructs nothing, so its grid stays its output through the dip.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldLayoutTransitionScaleLawTests : IDisposable {
    private const uint Display = 64;
    private const float Step = 0.1f;
    private const string World = "tests/Puck.Counters/counters.world.json";

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-transition-scale-");

    public void Dispose() => m_stateDirectory.Dispose();
    [InlineData(0.5f)]
    [InlineData(1f)]
    [Theory]
    public void ATransitionDipsTheGridInsideTheCeilingAndNeverTheCeiling(float ceiling) {
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World,
            edit: definition => {
                var slot = definition.Views.Layouts[0].Slots[0];

                return definition with {
                    ViewsRaw = definition.Views with {
                        Layouts = [
                            new WorldViewLayout(Name: "settled", Slots: [slot]),
                            new WorldViewLayout(Name: "eased", Slots: [slot], TransitionRenderScale: 0.5f, TransitionSeconds: 0.6f),
                        ],
                    },
                };
            }).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var composition = host.Services.GetRequiredService<WorldCompositionState>();

        host.Services.GetRequiredService<WorldRenderSettings>().RenderScale = ceiling;
        composition.ActiveLayout = "settled";
        _ = Dress();
        composition.ActiveLayout = "eased";

        // The frame that selects the eased layout starts its ease, which frames six steps later settle. Every frame of the
        // ease carries half the ceiling's grid, and every settled frame the ceiling's own; the frame on the boundary is
        // left to the float clock.
        for (var frame = 0; (frame < 10); frame++) {
            var view = Dress().Views[0];
            var easing = (frame < 6);

            if (frame == 6) {
                continue;
            }

            Assert.Equal(expected: ceiling, actual: view.RenderScale);
            Assert.Equal(expected: (easing ? (ceiling * 0.5f) : ceiling), actual: view.ResolvedRenderScale);
            Assert.Equal(expected: (ceiling < 1f), actual: view.Reconstructs);
            Assert.Equal(expected: ((ceiling < 1f) ? (easing ? 0.25d : 0.5d) : 1d), actual: view.RenderGrid);
        }

        SdfFrame Dress() => presenter.CaptureFrame(deltaSeconds: Step, height: Display, interpolationAlpha: 1f, width: Display);
    }
}
