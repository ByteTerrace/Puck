namespace Puck.World;

/// <summary>
/// The render-capacity oracle the server consults before it applies a scene/screen mutation — the seam that keeps the
/// "capacity honesty" contract: each active renderer registers the render envelope (program words, instances) it
/// probed at construction, so an applied mutation that would exceed ANY active consumer is REJECTED at apply time
/// with a loud line naming the ceiling (never a crash, never a silent clamp). A shared DI singleton:
/// <c>Puck.World.Client.WorldFramePresenter</c> and session-screen views each register their own
/// floors and candidate measurer; <c>Puck.World.Server.WorldServer</c> reads their conjunction while draining its
/// mutation queue.
/// </summary>
/// <remarks>Single-threaded: configured on the window-pump thread during startup, read on the same thread while the
/// server drains its pre-tick queue, so no lock guards it. Unconfigured (a mutation somehow racing startup) reads as
/// "fits" — the boot definition is what the envelope was probed for, so it always fits.</remarks>
public sealed class WorldRenderEnvelope {
    private readonly Dictionary<long, Constraint> m_constraints = [];

    private long m_nextConstraintId;

    /// <summary>Registers one active render consumer's probed floors and worst-case program measurer.</summary>
    /// <param name="programWordCapacity">The probed program-word ceiling (all avatars + the boot scene/screens/placements).</param>
    /// <param name="instanceCapacity">The probed instance ceiling.</param>
    /// <param name="measure">Measures a candidate definition's render-relevant sections (scene, screens, placements +
    /// their creations) against the same worst-case avatar build, returning its program-word and instance counts.</param>
    /// <returns>The registration lease. Disposing it removes only this consumer's constraint; all other active
    /// registrations continue to govern admission.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="measure"/> is <see langword="null"/>.</exception>
    public IDisposable Configure(int programWordCapacity, int instanceCapacity, Func<WorldDefinition, (int Words, int Instances)> measure) {
        ArgumentNullException.ThrowIfNull(argument: measure);

        var id = m_nextConstraintId++;

        m_constraints.Add(
            key: id,
            value: new Constraint(
                InstanceCapacity: instanceCapacity,
                Measure: measure,
                ProgramWordCapacity: programWordCapacity
            )
        );

        return new Registration(
            id: id,
            owner: this
        );
    }
    /// <summary>Tests whether a candidate definition's render-relevant sections fit the probed render envelope.</summary>
    /// <param name="candidate">The composed candidate definition.</param>
    /// <param name="reason">On a miss, the loud ceiling reason; empty otherwise.</param>
    /// <returns><see langword="true"/> when the candidate fits every active registration (or none exists).</returns>
    public bool TryFit(WorldDefinition candidate, out string reason) {
        foreach (var constraint in m_constraints.Values) {
            var (words, instances) = constraint.Measure(arg: candidate);

            if (words > constraint.ProgramWordCapacity) {
                reason = $"program words {words} exceed the probed render envelope {constraint.ProgramWordCapacity}";

                return false;
            }

            if (instances > constraint.InstanceCapacity) {
                reason = $"instances {instances} exceed the probed render envelope {constraint.InstanceCapacity}";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }

    private readonly record struct Constraint(int ProgramWordCapacity, int InstanceCapacity, Func<WorldDefinition, (int Words, int Instances)> Measure);
    private sealed class Registration(WorldRenderEnvelope owner, long id) : IDisposable {
        private WorldRenderEnvelope? m_owner = owner;

        public void Dispose() {
            if (m_owner is not { } current) {
                return;
            }

            m_owner = null;
            _ = current.m_constraints.Remove(key: id);
        }
    }
}
