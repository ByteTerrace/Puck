using Puck.Commands;

namespace Puck.Launcher;

/// <summary>The console operation shared by iteration and between-step scheduling. It also runs while a capture
/// holds the clock and in hosts with no simulation. Ready immediate commands (quit, recovery and presentation)
/// progress; simulation submissions wait for a tick, and session waits and read-after-write barriers retain FIFO order.</summary>
public static class HostConsoleDrain {
    /// <summary>Collects ready console work and flushes its answers on the host pump thread.</summary>
    public static void Collect(TextCommandSource source, BufferedConsoleOutput output) {
        source.Collect();
        output.Flush();
    }
}
