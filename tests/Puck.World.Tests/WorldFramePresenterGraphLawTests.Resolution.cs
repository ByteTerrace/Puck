using Microsoft.Extensions.DependencyInjection;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldFramePresenterGraphLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraResolutionUsesItsAuthoredWorldSettingInsteadOfTheViewerLever(bool authored) {
        using var host = WorldBootHarness.Compose(edit: definition => WithScreen(definition) with {
            RenderRaw = definition.Render with { DynamicResolution = authored },
        }, presentation: WorldHostPresentation.Offscreen, stateDirectory: m_stateDirectory, world: World).Build();
        using var render = WorldRenderRoot.Build(sp: host.Services, overlay: null);
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        settings.DynamicResolution = !authored;
        for (ulong frame = 0; frame < 3; frame++) { Present(presenter: presenter, index: frame); }
        var dressed = presenter.CaptureFrame(deltaSeconds: 0, height: Display, interpolationAlpha: 1, width: Display);
        Assert.True(condition: dressed.Views.Count > 1);
        Assert.Equal(expected: !authored, actual: dressed.Views[0].ResolvedRenderScale > 0);
        Assert.All(collection: dressed.Views.Skip(1), action: view => Assert.Equal(expected: authored, actual: view.ResolvedRenderScale > 0));
    }
}
