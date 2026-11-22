namespace Puck.World;

/// <summary>What a candidate definition's render scene declares against the engine's program ceilings, counted on the
/// CPU: its instances and its materials.</summary>
/// <param name="Instances">The instances the composed program declares.</param>
/// <param name="Materials">The materials the composed program registers.</param>
public readonly record struct WorldRenderCost(int Instances, int Materials) {
    /// <summary>Adds two costs.</summary>
    /// <param name="left">The first cost.</param>
    /// <param name="right">The second cost.</param>
    /// <returns>The sum, checked.</returns>
    public static WorldRenderCost operator +(WorldRenderCost left, WorldRenderCost right) => new(
        Instances: checked((left.Instances + right.Instances)),
        Materials: checked((left.Materials + right.Materials))
    );
    /// <summary>Scales a cost.</summary>
    /// <param name="cost">The cost of one.</param>
    /// <param name="count">How many.</param>
    /// <returns>The product, checked.</returns>
    public static WorldRenderCost operator *(WorldRenderCost cost, int count) => new(
        Instances: checked((cost.Instances * count)),
        Materials: checked((cost.Materials * count))
    );

    /// <summary>Names material overflow past <see cref="Puck.SignedDistance.SdfProgramBuilder.ScreenMaterialId"/>
    /// (the first screen sentinel). Instance counts describe the authored scene, not a per-world admission budget.</summary>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Exceeds() {
        return ((Materials > Puck.SignedDistance.SdfProgramBuilder.ScreenMaterialId)
            ? $"materials {Materials} exceed the engine ceiling {Puck.SignedDistance.SdfProgramBuilder.ScreenMaterialId}"
            : null);
    }
}
/// <summary>
/// The render-capacity oracle the server consults before it applies a render-relevant mutation. Each consumer that
/// renders the world registers a measurer that counts a candidate definition's render scene on the CPU
/// (<see cref="WorldRenderCost"/>); every renderer grows its program and instance buffers to what is delivered, so only
/// the engine's own ceilings bound a candidate. A shared DI singleton: the composition root registers the world scene's
/// own measurer on every boot, headless or windowed (<c>Puck.World.Client.WorldSceneEnvelope</c>), and session-screen
/// views register theirs on the instance they show; <c>Puck.World.Server.WorldServer</c> reads their conjunction while
/// draining its mutation queue.
/// </summary>
/// <remarks>The registered measurers are one immutable array, replaced whole under a lock by each registration and
/// removal, so a view may configure the envelope from the frame while a tick on another thread fits a candidate against
/// the array it read. Unconfigured, a candidate reads as fitting.</remarks>
public sealed class WorldRenderEnvelope {
    private readonly Lock m_gate = new();
    private volatile KeyValuePair<long, Func<WorldDefinition, WorldRenderCost>>[] m_measurers = [];

    private long m_nextId;
    private long m_revision;

    /// <summary>Gets a count that moves whenever a measurer is registered. What a fit read is recorded against it,
    /// because a fit made before a measurer registered says nothing about the parts that measurer reads. Removing a
    /// measurer leaves it, since the parts the rest read are among those recorded.</summary>
    public long Revision => Interlocked.Read(location: ref m_revision);

    /// <summary>Registers one render consumer's measurer.</summary>
    /// <param name="measure">Counts a candidate definition's render scene as this consumer would draw it. It runs once
    /// per render-relevant mutation, so it counts rather than emits; a candidate it cannot measure throws
    /// <see cref="WorldRenderMeasureRefusedException"/>.</param>
    /// <returns>The registration lease. Disposing it removes only this consumer's measurer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="measure"/> is <see langword="null"/>.</exception>
    public IDisposable Configure(Func<WorldDefinition, WorldRenderCost> measure) {
        ArgumentNullException.ThrowIfNull(argument: measure);

        long id;

        lock (m_gate) {
            id = m_nextId++;
            m_measurers = [.. m_measurers, new(key: id, value: measure)];
            _ = Interlocked.Increment(location: ref m_revision);
        }

        return new Registration(
            id: id,
            owner: this
        );
    }
    /// <summary>Tests whether a candidate definition's render scene fits every registered consumer.</summary>
    /// <param name="candidate">The composed candidate definition.</param>
    /// <param name="reason">On a miss, the ceiling the candidate exceeds; empty otherwise.</param>
    /// <returns><see langword="true"/> when the candidate fits every registration (or none exists).</returns>
    public bool TryFit(WorldDefinition candidate, out string reason) {
        foreach (var (_, measure) in m_measurers) {
            WorldRenderCost cost;

            try {
                cost = measure(arg: candidate);
            } catch (WorldRenderMeasureRefusedException error) {
                reason = error.Message;

                return false;
            }

            if (cost.Exceeds() is { } exceeded) {
                reason = exceeded;

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }

    private sealed class Registration(WorldRenderEnvelope owner, long id) : IDisposable {
        private WorldRenderEnvelope? m_owner = owner;

        public void Dispose() {
            if (m_owner is not { } current) {
                return;
            }

            m_owner = null;
            lock (current.m_gate) {
                current.m_measurers = Array.FindAll(array: current.m_measurers, match: entry => (entry.Key != id));
            }
        }
    }
}
