using Microsoft.Extensions.DependencyInjection;

using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRoutedPresentationLawTests {
    [Fact]
    public void ActualPrimaryAndRoutedViewRegistrationPublishesTheirExistingDomainGuards() {
        using var files = new TemporaryDirectory();
        using var host = WorldBootHarness.Compose(files, WorldHostPresentation.Offscreen,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json").Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var screens = host.Services.GetRequiredService<IWorldScreenPresenter>();
        var probe = host.Services.GetRequiredService<IWorldEngineReadiness>();
        var probeType = probe.GetType();
        var binderType = screens.GetType();
        var pipelines = new GpuPassPipelineCache();
        var catalog = new SdfWorldPipelineCatalog(
            regionCopy: new GpuRegionCopyPass(kernel: new byte[] { 1 }, pipelines: pipelines),
            meshRaster: new SdfMeshRasterPass(fragment: new byte[] { 1 }, pipelines: pipelines, vertex: new byte[] { 1 }));
        using var boot = WorldInspectorDomainLawTests.Residency(catalog: catalog, name: "boot");
        // These are the concrete binder's public render-factory seams; no private fields or GPU are involved.
        binderType.GetMethod(name: "ConfigureViews")!.Invoke(obj: screens, parameters: [catalog, false, 2048, 16, 16, presenter, 64, 36,
            host.Services.GetRequiredService<WorldSeatViewports>()]);
        binderType.GetProperty(name: "ViewHost")!.SetValue(obj: screens, value: boot);
        binderType.GetProperty(name: "Presenter")!.SetValue(obj: screens, value: presenter);
        probeType.GetProperty(name: "Residency")!.SetValue(obj: probe, value: boot);
        var lookup = probeType.GetMethod(name: "DomainsOf")!;

        Assert.Same(presenter.TimelineWork.Domains, lookup.Invoke(obj: probe, parameters: [boot]));
        using var endpoint = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        using var window = presenter.AttachWindow(endpoint: endpoint);

        _ = presenter.CaptureFrame(deltaSeconds: 0, height: 36, interpolationAlpha: 0, width: 64);
        object?[] arguments = [window, null];
        var resolved = binderType.GetMethod(name: "TryResolveWindowView")!.Invoke(obj: screens, parameters: arguments);

        Assert.Equal(actual: resolved, expected: true);
        var view = Assert.IsType<SdfWorldView>(@object: arguments[1]);

        Assert.NotSame(boot, view.Residency);
        Assert.Same(window.Scene.TimelineWork.Domains, lookup.Invoke(obj: probe, parameters: [view.Residency]));
    }
}
