namespace Puck.World.Client;

/// <summary>How much of its destination a viewer's session may see right now, from least to most.</summary>
public enum WorldSessionAccess : byte {
    /// <summary>Nothing: the session has no observation, its observation ended, or its world withholds observation.</summary>
    None = 0,
    /// <summary>What its own admission discloses, rendered from its own disclosed session.</summary>
    Disclosed = 1,
    /// <summary>Everything its destination holds, so it may be rendered from that world's whole replica.</summary>
    Everything = 2,
}
/// <summary>
/// One viewer's access to its destination, settled once a frame on the frame thread before any view of it is bound. Its
/// <see cref="Epoch"/> names the pictures the viewer may still be shown: it moves whenever the access narrows (to less
/// than before) or the session behind it is replaced, and never otherwise.
/// <para>Invariant: a picture made under one epoch is never shown under a later one. The epoch is the one signal of a
/// narrowing; the view graph host withdraws every published output of a view whose epoch moved
/// (<see cref="WorldViewGraphHost.BeginFrame"/>, through <see cref="Puck.Shaders.IRenderGraphInstances.Withdraw"/>), and
/// the screen binder releases the private residency and frame source made under the old epoch, so no job either
/// submitted can install into what the viewer now reads.</para>
/// <para>Every epoch any tracker takes is new to the process, so a view whose tracker is replaced (a session closed and
/// opened again under the same name) reads a different epoch too, and the epochs of a chain of trackers combine by their
/// maximum (<see cref="WorldViewAccess.Join"/>): any narrowing anywhere in the chain raises it.</para>
/// </summary>
public sealed class WorldViewerAccess {
    // The last epoch any tracker took; presentation state, never simulation state.
    private static long Epochs;

    private object? m_admission;

    /// <summary>Gets the access the latest settle found.</summary>
    public WorldSessionAccess Level { get; private set; }
    /// <summary>Gets the pictures the viewer may be shown: it moves only when the access narrows or its session is
    /// replaced.</summary>
    public long Epoch { get; private set; } = Interlocked.Increment(location: ref Epochs);

    /// <summary>Settles the access for the frame being prepared.</summary>
    /// <param name="level">The access the viewer's session has now.</param>
    /// <param name="admission">The session object that access was admitted through (its observation), compared by
    /// reference: another one is another session, even at the same level.</param>
    /// <returns><see langword="true"/> when the access narrowed, which moved <see cref="Epoch"/>.</returns>
    public bool Settle(WorldSessionAccess level, object? admission) {
        var narrowed = (
            (level < Level) ||
            ((m_admission is not null) && !ReferenceEquals(objA: m_admission, objB: admission))
        );

        Level = level;
        m_admission = admission;
        if (narrowed) {
            Epoch = Interlocked.Increment(location: ref Epochs);
        }

        return narrowed;
    }
}
/// <summary>Whether a view's content may be rendered now, and the epoch of the pictures it may still show
/// (<see cref="WorldViewerAccess.Epoch"/>).</summary>
/// <param name="Available">Whether the view may render current content.</param>
/// <param name="Epoch">The pictures the view may show: a change of it withdraws every picture published before.</param>
public readonly record struct WorldViewAccess(bool Available, long Epoch) {
    /// <summary>A view no session gates: always available, its epoch never moving.</summary>
    public static WorldViewAccess Open => new(Available: true, Epoch: 0L);
    /// <summary>A view whose session is gone: unavailable, at an epoch no live session takes.</summary>
    public static WorldViewAccess Retired => new(Available: false, Epoch: long.MaxValue);

    /// <summary>Combines this access with another the view also depends on: available only when both are, at the later
    /// epoch.</summary>
    /// <param name="other">The other access.</param>
    /// <returns>The combined access.</returns>
    public WorldViewAccess Join(WorldViewAccess other) => new(
        Available: (Available && other.Available),
        Epoch: Math.Max(val1: Epoch, val2: other.Epoch)
    );
}
