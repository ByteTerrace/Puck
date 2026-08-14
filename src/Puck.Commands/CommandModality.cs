namespace Puck.Commands;

// The router-owned, compiled answer to "which command maps are active for this slot?". Map names stay on the
// registration/configuration side; source resolution reads ActiveCommands by interned id on the hot path.
internal sealed class CommandModality {
    internal CommandModality(bool[] activeMaps, bool[] activeCommands) {
        ActiveCommands = activeCommands;
        ActiveMaps = activeMaps;
    }

    internal bool[] ActiveCommands { get; }
    internal bool[] ActiveMaps { get; }
}
