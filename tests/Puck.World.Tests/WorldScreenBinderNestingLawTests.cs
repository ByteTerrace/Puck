using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Hosting;
using Puck.Platform;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws over the composed binder's nested feed lifetime, without configuring a renderer or opening a device.</summary>
public sealed class WorldScreenBinderNestingLawTests {
    private const int Screen = WorldPrototypeFacets.DerivedFaceBase;
    private const string World = "tests/Puck.World.Canaries/portal-nested/fixture.world.json";

    private static readonly ulong Step = EngineTicks.PerRate(ratePerSecond: 30u);

    private static IHost Boot(TemporaryDirectory state) {
        var builder = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.None,
            stateDirectory: state,
            world: World
        );

        builder.Services.AddSingleton<ICameraCaptureService, NullCameraCaptureService>();

        return state.Own(owner: builder.Build());
    }
    private static IWorldScreenPresenter BindScreens(IHost host) {
        var binder = host.Services.GetRequiredService<IWorldScreenPresenter>();
        var definition = host.Services.GetRequiredService<WorldInstanceHost>().Boot!.Server.Definition;

        // The constructor reserves face slots; a presentation delivery supplies their actual rows.
        binder.ReconcileScreens(screens: [.. definition.Screens, .. WorldPrototypeFacets.Seated(definition: definition)]);

        return binder;
    }
    private static void Publish(IWorldScreenPresenter binder) {
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = new FakeGpuDevice(),
            }),
            StepTicks: Step,
            TargetHeight: 64U,
            TargetWidth: 64U
        );

        binder.Publish(context: in context);
    }
    private static string Nesting(IHost host) {
        var result = host.Services.GetRequiredService<CommandRegistry>().Submit(line: "world.nesting");

        Assert.False(condition: result.IsError, userMessage: result.Output);

        return result.Output;
    }
    private static void SetDepth(WorldInstanceHost instances, int depth) {
        var boot = instances.Boot!;

        boot.Server.EnqueueMutation(mutation: new WorldMutation.SetViewDefaults(
            Principal: Principal.Console,
            Views: (boot.Server.Definition.Views with { NestingDepthRaw = depth })
        ));
        boot.Server.Advance(stepTicks: Step);
        instances.SettleBootScreenSessions(stepped: true);
        instances.StepInstances(masterDeltaTicks: Step);
        Assert.Equal(expected: depth, actual: boot.Server.Definition.Views.NestingDepth);
    }

    // THE LAW: lowering the limit to zero releases the boot world's feeds as well as their descendants; raising it
    // opens them again. The fallback remains an ordinary colour source while there is no session view to schedule.
    [Fact]
    public void DepthZeroReleasesTheBootFeedsAndRaisingItReopensThem() {
        using var state = new TemporaryDirectory(prefix: "puck-binder-depth-");
        using var host = Boot(state: state);
        var binder = BindScreens(host: host);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        Publish(binder: binder);
        Assert.Contains(expectedSubstring: $"session${Screen} depth 1", actualString: Nesting(host: host));
        Assert.Contains(expectedSubstring: $"session${Screen}${Screen} depth 2", actualString: Nesting(host: host));

        SetDepth(depth: 0, instances: instances);
        Publish(binder: binder);

        Assert.Equal(expected: "[world.nesting: depth 0]", actual: Nesting(host: host));
        Assert.Null(@object: instances.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: Screen));
        Assert.StartsWith(
            expectedStartString: "source$color$",
            actualString: ((ISdfScreenSources)binder).ReadOf(screen: Screen, view: 0)
        );

        SetDepth(depth: 3, instances: instances);
        Publish(binder: binder);

        Assert.Contains(expectedSubstring: $"session${Screen} depth 1", actualString: Nesting(host: host));
        Assert.Contains(expectedSubstring: $"session${Screen}${Screen} depth 2", actualString: Nesting(host: host));
    }
    // THE LAW: removing a root portal releases its nested owner, including the closures that retain its session.
    // Keep the binder alive across collection: dropping the entire host would conceal an owner-table leak.
    [Fact]
    public void RemovingARootPortalDoesNotRetainItsNestedOwner() {
        using var state = new TemporaryDirectory(prefix: "puck-binder-remove-");
        using var host = Boot(state: state);
        var retired = RemovePortal(host: host);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(condition: retired.TryGetTarget(target: out _));
        GC.KeepAlive(obj: host.Services.GetRequiredService<IWorldScreenPresenter>());
    }
    // THE LAW: an ended parent observation cannot keep disclosing live descendants through its retained document.
    // Presentation releases the child even before the authority settles; the authority then closes the child's
    // session because an ended observation is no edge in its depth walk.
    [Fact]
    public void AnEndedObservationNeitherShowsNorHoldsItsDescendants() {
        using var state = new TemporaryDirectory(prefix: "puck-binder-ended-");
        using var host = Boot(state: state);
        var binder = BindScreens(host: host);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        Publish(binder: binder);
        Assert.Contains(expectedSubstring: $"session${Screen}${Screen} depth 2", actualString: Nesting(host: host));

        var parent = instances.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: Screen)!;
        var child = instances.ScreenSession(instanceName: parent.InstanceName!, screenIndex: Screen)!;

        Assert.NotNull(@object: child.Observation);
        parent.Observation!.Dispose();
        Publish(binder: binder);

        Assert.DoesNotContain(expectedSubstring: $"session${Screen}${Screen}", actualString: Nesting(host: host));
        instances.StepInstances(masterDeltaTicks: Step);
        Assert.Null(@object: instances.ScreenSession(instanceName: parent.InstanceName!, screenIndex: Screen));
        Assert.Null(@object: child.Observation);
    }
    // THE LAW: a nested level whose own observation has ended stops showing at the next publish, while the level above
    // it, whose observation is live, keeps its feed.
    [Fact]
    public void AnEndedNestedObservationReleasesOnlyItsOwnLevel() {
        using var state = new TemporaryDirectory(prefix: "puck-binder-ended-nested-");
        using var host = Boot(state: state);
        var binder = BindScreens(host: host);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        Publish(binder: binder);
        Assert.Contains(expectedSubstring: $"session${Screen}${Screen} depth 2", actualString: Nesting(host: host));

        var parent = instances.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: Screen)!;
        var child = instances.ScreenSession(instanceName: parent.InstanceName!, screenIndex: Screen)!;

        child.Observation!.Dispose();
        Publish(binder: binder);

        var nesting = Nesting(host: host);

        Assert.Contains(actualString: nesting, expectedSubstring: $"session${Screen} depth 1");
        Assert.DoesNotContain(actualString: nesting, expectedSubstring: $"session${Screen}${Screen}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<WorldScreenSession> RemovePortal(IHost host) {
        var binder = BindScreens(host: host);
        var instances = host.Services.GetRequiredService<WorldInstanceHost>();

        instances.StepInstances(masterDeltaTicks: Step);
        Publish(binder: binder);
        Assert.Contains(expectedSubstring: $"session${Screen}${Screen} depth 2", actualString: Nesting(host: host));

        var retired = new WeakReference<WorldScreenSession>(target: instances.ScreenSession(
            instanceName: WorldInstanceHost.BootInstanceName,
            screenIndex: Screen
        )!);

        instances.Boot!.Server.EnqueueMutation(mutation: new WorldMutation.RemovePlacement(
            Id: "door",
            Principal: Principal.Console
        ));
        instances.Boot.Server.Advance(stepTicks: Step);
        instances.SettleBootScreenSessions(stepped: true);
        instances.StepInstances(masterDeltaTicks: Step);
        binder.ReconcileScreens(screens: []);
        Publish(binder: binder);

        Assert.Null(@object: instances.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: Screen));
        Assert.DoesNotContain(expectedSubstring: $"session${Screen}", actualString: Nesting(host: host));

        return retired;
    }
}
