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
