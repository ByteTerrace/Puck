using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The copies a collect makes of authority state for the frame (<see cref="WorldPresentationFeed"/>), each a pure
/// read of what it is handed. Call on the pump inside the boundary window.</summary>
public static class WorldPresentationCopy {
    /// <summary>Copies an in-session history's window as a frame reads it.</summary>
    /// <param name="history">The history.</param>
    /// <returns>The window, or <see langword="null"/> while the history holds none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is <see langword="null"/>.</exception>
    public static WorldHistoryWindow? HistoryWindow(WorldHistory history) {
        ArgumentNullException.ThrowIfNull(argument: history);

        return (((history.OldestTick is { } oldest) && (history.HeadTick is { } head) && (history.CursorTick is { } cursor))
            ? new WorldHistoryWindow(Cursor: cursor, Head: head, Oldest: oldest, WindowTicks: history.WindowTicks)
            : null);
    }
    /// <summary>Captures every running world instance's machine presentation, by instance name. Call on the pump inside
    /// the boundary window.</summary>
    /// <param name="instances">The running world instances.</param>
    /// <param name="held">The table the last capture made, returned again when no world's presentation moved.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldMachineTable Machines(WorldInstanceHost instances, WorldMachineTable held) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: held);

        var names = instances.Names;
        var copied = ((held.Count != names.Count) ? new KeyValuePair<string, WorldMachinePresentation>[names.Count] : null);

        for (var index = 0; (index < names.Count); index++) {
            var name = names[index];
            var presentation = ((instances.TryGet(instance: out var instance, name: name) && (instance is not null))
                ? instance.Server.Machines.CapturePresentation()
                : WorldMachinePresentation.None);

            if ((copied is null) && (!string.Equals(a: held.At(index: index).Key, b: name, comparisonType: StringComparison.Ordinal) || !ReferenceEquals(objA: held.At(index: index).Value, objB: presentation))) {
                copied = new KeyValuePair<string, WorldMachinePresentation>[names.Count];
                for (var kept = 0; (kept < index); kept++) {
                    copied[kept] = held.At(index: kept);
                }
            }
            if (copied is not null) {
                copied[index] = new(key: name, value: presentation);
            }
        }

        return ((copied is null) ? held : new WorldMachineTable(worlds: copied));
    }
}
