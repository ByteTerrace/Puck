using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>The named clock dependencies of a cached presentation value. A held preview ignores later state deliveries.</summary>
public sealed class WorldClockReads {
    private readonly List<Reading> m_readings = [];

    private readonly record struct Reading(string Name, bool HasPhase, double Phase, PresentedTick? IntegratedTick);

    /// <summary>Clears the dependencies before resolving a value again.</summary>
    public void Clear() => m_readings.Clear();
    /// <summary>Records a key's clock, including its unwrapped tick when the key is an integrated rate.</summary>
    /// <param name="mirror">The presentation source.</param>
    /// <param name="name">The clock name.</param>
    /// <param name="integrated">Whether complete turns affect the result.</param>
    public void Note(WorldStateMirror mirror, string name, bool integrated = false) => m_readings.Add(item: Read(integrated: integrated, mirror: mirror, name: name));
    /// <summary>Returns whether any recorded clock presents a different value.</summary>
    /// <param name="mirror">The presentation source.</param>
    /// <returns>Whether the cached value needs resolving.</returns>
    public bool Moved(WorldStateMirror mirror) {
        foreach (var reading in m_readings) {
            if (Read(mirror, reading.Name, reading.IntegratedTick.HasValue) != reading) { return true; }
        }
        return false;
    }

    private static Reading Read(WorldStateMirror mirror, string name, bool integrated) {
        var available = mirror.TryReadPhase(clock: out _, name: name, phase: out var phase);

        return new Reading(name, available, phase, (integrated ? mirror.ClockTick(name: name) : null));
    }
}
