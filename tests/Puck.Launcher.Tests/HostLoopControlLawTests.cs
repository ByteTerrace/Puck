using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>Control ingress remains live without an advancing simulation clock.</summary>
public sealed class HostLoopControlLawTests {
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class HeldSimulation : IFixedStepSimulation {
        public bool AwaitsFrame => (Steps > 0);
        public uint RatePerSecond => 30;
        public int Steps { get; private set; }

        public bool HoldsClock(ulong withheldTicks) => AwaitsFrame;
        public void SettleOwedFrames() { }
        public void Step(in FixedStepContext context, in CommandSnapshot commands) => Steps++;
    }
    private sealed class ControlModule : ICommandModule {
        public int Calls { get; private set; }

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                audience: CommandAudience.Anyone,
                name: "probe.control", description: "A presentation/control command that needs no tick.",
                bindability: CommandBindability.Unbindable,
                handler: (_, _) => { Calls++; return CommandResult.None; });
        }
    }
    private sealed class HeldRoot(ControlModule control, HeldSimulation simulation, bool hasSimulation) : IRenderRoot {
        public int Frames { get; private set; }
        public TextCommandSource Source { get; set; } = null!;

        public void Dispose() { }
        public RootFrame ProduceFrame(in FrameContext context) {
            Frames++;
            Assert.InRange(actual: Frames, low: 1, high: 2);
            Assert.Equal(expected: (hasSimulation ? 1 : 0), actual: simulation.Steps);
            if (Frames == 1) {
                // Arrives after the step drain: it must run during the held/no-pump iteration.
                Source.Enqueue(line: "probe.control");
            } else {
                Assert.Equal(expected: 1, actual: control.Calls);
                Source.Enqueue(line: "quit");
            }
            // An unfinished frame holds the completed tick in the real offscreen adapter.
            return new RootFrame(Surface: default, Render: FrameRender.Waiting(reason: "the capture's pipeline is still building"));
        }
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ConsoleProgressesWhileFrameIsHeldOrNoSimulationExists(bool hasSimulation) {
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings { DisableDefaults = true });

        builder.Logging.ClearProviders();
        var control = new ControlModule();
        var simulation = new HeldSimulation();
        var root = new HeldRoot(control: control, hasSimulation: hasSimulation, simulation: simulation);

        builder.Services.AddSingleton<ICommandModule>(implementationInstance: control);
        builder.Services.AddSingleton<IRenderRoot>(implementationInstance: root);
        builder.Services.AddSingleton(implementationInstance: new OffscreenRenderOptions(height: 32, width: 32));
        if (hasSimulation) {
            builder.Services.AddSingleton<IPrincipalResolver, ConsolePrincipal>();
            builder.Services.AddSingleton(implementationInstance: simulation);
            builder.Services.AddFixedStepSimulation<HeldSimulation>(bindings: new NoBindings());
        }
        builder.Services.AddLauncherOffscreenTerminal();
        // The law owns ingress; do not open the test runner's stdin.
        for (var index = (builder.Services.Count - 1); (index >= 0); index--) {
            if ((builder.Services[index].ServiceType == typeof(IHostedService)) &&
                (builder.Services[index].ImplementationFactory is not null)) {
                builder.Services.RemoveAt(index: index);
            }
        }
        using var host = builder.Build();

        root.Source = host.Services.GetRequiredService<TextCommandSource>();
        await WindowedHostFixture.RunAsync(host: host);
        Assert.Equal(expected: 2, actual: root.Frames);
        Assert.Equal(expected: 1, actual: control.Calls);
    }
    [Fact]
    public void RuntimeLockstepRetargetsTheRegisteredReplayPolicy() {
        var services = new ServiceCollection();

        services.AddLauncherTerminal();
        using var provider = services.BuildServiceProvider();
        var pacing = provider.GetRequiredService<HostPacing>();
        var presentation = provider.GetRequiredService<PresentPacingControl>();

        Assert.Same(expected: pacing, actual: presentation.HostPacing);
        Assert.False(condition: pacing.StepsOneTickPerFrame);
        presentation.SetTickLocked(locked: true);
        Assert.True(condition: pacing.StepsOneTickPerFrame);
        Assert.True(condition: presentation.ConvergesLighting);
        presentation.SetLightingConvergence(enabled: false);
        Assert.True(condition: pacing.StepsOneTickPerFrame);
        Assert.False(condition: presentation.ConvergesLighting);
        presentation.SetLightingConvergence(enabled: true);
        Assert.True(condition: presentation.ConvergesLighting);
        presentation.SetTickLocked(locked: false);
        Assert.False(condition: pacing.StepsOneTickPerFrame);
    }
}
