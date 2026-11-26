using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Whether the last scale pass had a register to read, so the pass after a document drops bodies.scale still returns
    // every body to one, and a document without one costs nothing per tick.
    private bool m_scaling;

    /// <summary>Resyncs every body's live <see cref="WorldBody.Scale"/> from the <c>state.body</c> Counter register
    /// <c>bodies.scale</c> names: an active body takes the register's value, and every other body, or every body of a
    /// document naming none, is one. The register is the body's own state, so a shrunk body keeps its size across a
    /// crossing, a checkpoint restore and a replay. A body whose scale moves rederives its collider volumes and
    /// broadphase radius.
    /// <para>A tick's growth is held to the room about the body (<see cref="WorldBody.SetScaleWithinClearance"/>): a
    /// body asked to grow where it does not fit takes the largest size that does, and that size is written back into its
    /// register, so the register, the collider and every observer that reads the register agree on the size the body
    /// is. A rule that keeps asking for the larger size (a size field) is answered again each tick, and the body grows
    /// once it has room.</para></summary>
    /// <param name="definition">The live document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void SyncBodyScale(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var slot = WorldBodyStateReads.PlanFor(definition: definition).Scale;
        var scaling = (slot >= 0);

        if (!scaling && !m_scaling) {
            return;
        }

        m_scaling = scaling;

        for (var index = 0; (index < m_entries.Length); index++) {
            SyncBodyScaleAt(holdGrowthByClearance: true, index: index, slot: slot);
        }
    }
    /// <summary>Brings one body to the size a crossing wrote into its register, whole: the pair's counterpart is where
    /// the traveller stands, and its opening is the room it needs, so the growth is not held to the room about it.</summary>
    /// <param name="definition">The live document.</param>
    /// <param name="index">The traveller's entity index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void SyncCrossingScale(WorldDefinition definition, int index) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var slot = WorldBodyStateReads.PlanFor(definition: definition).Scale;

        if ((slot >= 0) && (((uint)index) < ((uint)m_entries.Length))) {
            SyncBodyScaleAt(holdGrowthByClearance: false, index: index, slot: slot);
        }
    }

    // Brings one body's scale to its register: an active body takes the register's value, within its clearance when
    // asked, and every other body, or every body under no register (a negative slot), is one.
    private void SyncBodyScaleAt(int index, int slot, bool holdGrowthByClearance) {
        if (m_entries[index].Body is not { } body) {
            return;
        }

        if ((slot < 0) || !m_entries[index].Active) {
            body.SetScale(value: FixedQ4816.One);

            return;
        }

        var asked = m_actionStateLane.Counter(
            ordinal: index,
            slot: slot
        );

        if (!holdGrowthByClearance) {
            body.SetScale(value: asked);

            return;
        }

        var taken = body.SetScaleWithinClearance(value: asked);

        if (taken != asked) {
            body.ApplyClearanceScale(
                raw: taken.Value,
                slot: slot
            );
        }
    }
}
