using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>Device-free laws for the clock and recovery boundaries of the real offscreen host.</summary>
public sealed class OffscreenBoundaryLawTests {
    private const uint Rate = 30U;
    private const ulong StepTicks = (EngineTicks.PerSecond / Rate);

    private sealed class ManualClock : TimeProvider {
        private long m_now;

        public bool AdvancesOnRead { get; init; }
        public override long TimestampFrequency => ((long)EngineTicks.PerSecond);

        public void Advance(long ticks) => _ = Interlocked.Add(location1: ref m_now, value: ticks);
        public override long GetTimestamp() => (AdvancesOnRead
            ? Interlocked.Increment(location: ref m_now)
            : Interlocked.Read(location: ref m_now));
    }
    private sealed class ManualWaiter(ManualClock clock) : IPrecisionWaiter {
        public bool TryWait(TimeSpan duration) {
            clock.Advance(ticks: ((long)(duration.TotalSeconds * clock.TimestampFrequency)));

            return true;
        }
    }
    private sealed class Simulation : IFixedStepSimulation {
        public bool AwaitsFrame => false;
        public uint RatePerSecond => Rate;
        public ulong Steps { get; private set; }

        public bool HoldsClock(ulong withheldTicks) => false;
        public void SettleOwedFrames() { }
        public void Step(in FixedStepContext context, in CommandSnapshot commands) => Steps++;
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class ClockedRebuild(ManualClock clock) : IDeviceRebuild {
        public int Calls { get; private set; }

        public void Rebuild() {
            Calls++;
            clock.Advance(ticks: (3L * clock.TimestampFrequency));
        }
    }
    private sealed class LosingRoot(ulong lossTick) : IRenderRoot {
        public FrameContext? Failed { get; private set; }
        public List<FrameContext> Frames { get; } = [];
        public int Losses { get; private set; }
        public TerminalControl? Terminal { get; set; }

        public void Dispose() { }
        public void OnDeviceLost() => Losses++;
        public Surface ProduceFrame(in FrameContext context) {
            if ((Failed is null) && (context.ElapsedTicks == (lossTick * StepTicks))) {
                Failed = context;

                throw new DeviceLostException(message: "the law loses this tick's frame");
            }

            Frames.Add(item: context);
            if (Frames.Count == 4) {
                Terminal!.RequestExit();
            }

            return default;
        }
    }

    [InlineData(1UL)]
    [InlineData(3UL)]
    [Theory]
    public async Task RecoveryProducesTheCompletedTicksFrameBeforeAnotherStep(ulong lossTick) {
        var clock = new ManualClock { AdvancesOnRead = true };
        var simulation = new Simulation();
        var root = new LosingRoot(lossTick: lossTick);
        var rebuild = new ClockedRebuild(clock: clock);
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings { DisableDefaults = true });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(implementationInstance: clock);
        builder.Services.AddSingleton<IPrecisionWaiter>(implementationInstance: new ManualWaiter(clock: clock));
        builder.Services.AddSingleton(implementationInstance: new LauncherOptions { ExitAfter = TimeSpan.FromMinutes(value: 1) });
        builder.Services.AddSingleton(implementationInstance: new OffscreenRenderOptions(height: 32U, width: 32U));
        builder.Services.AddSingleton<IRenderRoot>(implementationInstance: root);
        builder.Services.AddSingleton<IDeviceRebuild>(implementationInstance: rebuild);
        builder.Services.AddSingleton<IPrincipalResolver, ConsolePrincipal>();
        builder.Services.AddSingleton(implementationInstance: simulation);
        builder.Services.AddFixedStepSimulation<Simulation>(bindings: new NoBindings());
        builder.Services.AddLauncherOffscreenTerminal();
        // This law has no stdin script; keep the test process's stdin out of the first-step gate.
        for (var index = (builder.Services.Count - 1); (index >= 0); index--) {
            if ((builder.Services[index].ServiceType == typeof(IHostedService)) &&
                (builder.Services[index].ImplementationFactory is not null)) {
                builder.Services.RemoveAt(index: index);
            }
        }

        using var host = builder.Build();

        root.Terminal = host.Services.GetRequiredService<TerminalControl>();
        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(expected: 1, actual: rebuild.Calls);
        Assert.Equal(expected: 1, actual: root.Losses);
        Assert.Equal(expected: 4UL, actual: simulation.Steps);
        Assert.Equal(expected: 4, actual: root.Frames.Count);
        for (var index = 0; (index < root.Frames.Count); index++) {
            var frame = root.Frames[index];

            Assert.Equal(expected: ((((ulong)index) + 1UL) * StepTicks), actual: frame.ElapsedTicks);
            Assert.Equal(expected: (StepTicks, StepTicks, 0UL), actual: (frame.DeltaTicks, frame.FrameDeltaTicks, frame.AccumulatorTicks));
        }
        Assert.Equal(expected: root.Failed!.Value, actual: root.Frames[(((int)lossTick) - 1)]);
    }
    [InlineData("offscreen")]
    [InlineData("headless")]
    [InlineData("windowed")]
    [Theory]
    public void InputAndHostIntervalsUseTheSameRegisteredProvider(string shape) {
        var time = new ManualClock();
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings { DisableDefaults = true });

        builder.Services.AddSingleton<TimeProvider>(implementationInstance: time);
        switch (shape) {
            case "offscreen":
                builder.Services.AddLauncherOffscreenTerminal();
                break;
            case "headless":
                builder.Services.AddLauncherHeadlessTerminal();
                break;
            case "windowed":
                builder.Services.AddLauncherTerminal();
                break;
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(shape));
        }

        using var host = builder.Build();
        var input = host.Services.GetRequiredService<IInputClock>();
        var interval = TickClock.Start(time: host.Services.GetRequiredService<TimeProvider>());

        time.Advance(ticks: (3L * time.TimestampFrequency));
        Assert.Equal(expected: (3UL * EngineTicks.PerSecond), actual: input.NowTicks);
        Assert.Equal(expected: input.NowTicks, actual: interval.Sample());
        time.Advance(ticks: ((long)StepTicks));
        Assert.Equal(expected: StepTicks, actual: interval.Sample());
        Assert.Equal(expected: ((3UL * EngineTicks.PerSecond) + StepTicks), actual: input.NowTicks);
    }
}
