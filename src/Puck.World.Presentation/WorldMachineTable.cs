using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// What a frame reads of the machines of every world this process runs, captured at a host step's collect and published
/// whole (<see cref="WorldPresentationFeed.Machines"/>): each world instance's <see cref="WorldMachinePresentation"/>, by
/// instance name. A screen resolves its output and its clock here, never through a machine host.
/// </summary>
public sealed class WorldMachineTable {
    private readonly KeyValuePair<string, WorldMachinePresentation>[] m_worlds;

    /// <summary>Initializes a new instance of the <see cref="WorldMachineTable"/> class.</summary>
    /// <param name="worlds">Each running world instance's machine presentation, by instance name; the table keeps the
    /// array.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worlds"/> is <see langword="null"/>.</exception>
    public WorldMachineTable(KeyValuePair<string, WorldMachinePresentation>[] worlds) {
        ArgumentNullException.ThrowIfNull(argument: worlds);
        m_worlds = worlds;
    }

    /// <summary>Gets the table of a process running no world.</summary>
    public static WorldMachineTable Empty { get; } = new(worlds: []);

    /// <summary>Gets the number of worlds the table holds.</summary>
    public int Count => m_worlds.Length;

    /// <summary>Reads the world held at a position, in the order the table was built.</summary>
    /// <param name="index">The 0-based position.</param>
    /// <returns>The world's instance name and machine presentation.</returns>
    public KeyValuePair<string, WorldMachinePresentation> At(int index) => m_worlds[index];
    /// <summary>Reads a world's machine presentation.</summary>
    /// <param name="world">The world instance's name.</param>
    /// <returns>The presentation, or <see langword="null"/> for a world this process does not run, whose machines run on
    /// its own authority and reach no screen here.</returns>
    public WorldMachinePresentation? Of(string world) {
        foreach (var (name, presentation) in m_worlds) {
            if (string.Equals(a: name, b: world, comparisonType: StringComparison.Ordinal)) {
                return presentation;
            }
        }

        return null;
    }
}
