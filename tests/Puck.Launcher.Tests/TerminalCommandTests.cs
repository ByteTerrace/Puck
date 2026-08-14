using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class TerminalCommandTests {
    [Fact]
    public void AdministrativeConsoleInvocationRequiresAndUsesAnExplicitPlayer() {
        var sessions = new TestSessions(count: 4);
        var services = new ServiceCollection();

        services.AddSingleton<IConsoleSessions>(implementationInstance: sessions);
        services.AddLauncherTerminal();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<CommandRegistry>();

        var implicitTarget = registry.Submit(line: "console on");
        var explicitTarget = registry.Submit(line: "console on 2");

        Assert.True(condition: implicitTarget.IsError);
        Assert.Contains(expectedSubstring: "explicit player", actualString: implicitTarget.Output);
        Assert.False(condition: explicitTarget.IsError);
        Assert.Equal(expected: 1, actual: sessions.LastSlot);
        Assert.True(condition: sessions.LastVisible);
    }

    [Fact]
    public void LauncherTextSourceMirrorsAdministrativeResultsAndHonorsContributedHoldGates() {
        var gate = new TestHoldGate { Holding = true, };
        var invocations = 0;
        var services = new ServiceCollection();

        services.AddSingleton<ITextCommandHoldGate>(implementationInstance: gate);
        services.AddSingleton<ICommandModule>(implementationInstance: new ProbeModule(onInvoke: () => invocations++));
        services.AddLauncherTerminal();

        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<TextCommandSource>();
        var terminalSessions = provider.GetRequiredService<TerminalConsoleSessions>();

        source.Enqueue(line: "probe");
        source.Collect();

        Assert.Equal(expected: 0, actual: invocations);

        gate.Holding = false;
        source.Collect();

        Assert.Equal(expected: 1, actual: invocations);
        Assert.True(condition: terminalSessions.OperatorStore.TrySnapshot(frame: out var frame));
        Assert.Contains(collection: frame.Lines, filter: static line => line.Text == "> probe");
        Assert.Contains(collection: frame.Lines, filter: static line => line.Text == "[probe: ok]");
    }

    private sealed class TestHoldGate : ITextCommandHoldGate {
        public bool Holding { get; set; }

        public bool IsHolding() => Holding;
    }

    private sealed class ProbeModule(Action onInvoke) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Unbindable,
                name: "probe",
                description: "Reports a probe result.",
                valueKind: CommandValueKind.Digital,
                handler: _ => {
                    onInvoke();

                    return new CommandResult(Output: "[probe: ok]");
                }
            );
        }
    }

    private sealed class TestSessions(int count) : IConsoleSessions {
        public int Count { get; } = count;
        public int LastSlot { get; private set; } = -1;
        public bool LastVisible { get; private set; }

        public bool TryGetVisible(int slot, out bool visible) {
            visible = false;
            return ((uint)slot < (uint)Count);
        }

        public bool TrySetVisible(int slot, bool? visible, out bool resolved) {
            if ((uint)slot >= (uint)Count) {
                resolved = false;
                return false;
            }

            LastSlot = slot;
            LastVisible = (visible ?? !LastVisible);
            resolved = LastVisible;
            return true;
        }
    }
}
