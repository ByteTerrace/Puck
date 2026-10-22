using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Whether the last scale pass had a register to read, so the pass after a document drops bodies.scale still returns
    // every body to one, and a document without one costs nothing per tick.
    private bool m_scaling;

    /// <summary>Resyncs every body's live <see cref="WorldBody.Scale"/> from the <c>state.body</c> Counter register
    /// <c>bodies.scale</c> names: an active body takes the register's value, and every other body, or every body of a
    /// document naming none, is one. The register is the body's own state, so a shrunk body keeps its size across a
    /// crossing, a checkpoint restore and a replay. Called once a tick after the world rules, from the same choke points
    /// <see cref="SyncBodyCells"/> is, and by a crossing that wrote the register. A body whose scale moves rederives its
    /// collider volumes and broadphase radius.</summary>
    /// <param name="definition">The live document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void SyncBodyScale(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var slot = -1;
        var scaling = (
            (definition.Population.Scale is { } name) &&
            WorldBodyStateReads.TryResolveSlot(
            definition: definition,
            kind: out var kind,
            name: name,
            slot: out slot
        ) &&
            (kind == ActionStateKind.Counter)
        );

        if (!scaling && !m_scaling) {
            return;
        }

        m_scaling = scaling;

        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index].Body is not { } body) {
                continue;
            }

            body.SetScale(value: ((scaling && m_entries[index].Active)
                ? m_actionStateLane.Counter(
                    ordinal: index,
                    slot: slot
                )
                : FixedQ4816.One
            ));
        }
    }
}
