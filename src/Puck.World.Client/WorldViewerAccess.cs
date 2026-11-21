using Puck.Abstractions.Presentation;

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
/// <see cref="Epoch"/> and <see cref="Span"/> name the pictures the viewer may still be shown: they move whenever the
/// access narrows (to less than before) or the session behind it is replaced, and never otherwise.
/// <para>Invariant: a picture made under one epoch is never shown under a later one. Two guards hold it side by side. The
/// view graph host withdraws every published output of a view whose epoch moved
/// (<see cref="WorldViewGraphHost.BeginFrame"/>, through <see cref="Puck.Shaders.IRenderGraphInstances.Withdraw"/>), and
/// the screen binder releases the private residency and frame source made under the old epoch, so no job either
/// submitted can install into what the viewer now reads. And every picture carries the span it was made under in its
/// label (<see cref="Puck.Shaders.IRenderGraphInstances.SetRoute"/>), which no reader is given once the span has
/// ended.</para>
/// <para>Every epoch any tracker takes is new to the process, and every tracker has spans of its own, so a view whose
/// tracker is replaced (a session closed and opened again under the same name) reads a different epoch and span too. A
/// view seen through a chain of trackers is available only while every one is, at the latest epoch among them, under a
/// route of every one's span, so any narrowing anywhere in the chain raises its epoch and ends its route.</para>
/// </summary>
public sealed class WorldViewerAccess {
    // The last epoch any tracker took; presentation state, never simulation state.
    private static long Epochs;

    private readonly DeliverySpanSource m_spans = new();
    private object? m_admission;

    /// <summary>Gets the access the latest settle found.</summary>
    public WorldSessionAccess Level { get; private set; }
    /// <summary>Gets the pictures the viewer may be shown: it moves only when the access narrows or its session is
    /// replaced.</summary>
    public long Epoch { get; private set; } = Interlocked.Increment(location: ref Epochs);
    /// <summary>Gets the viewer's current delivery span, ended whenever <see cref="Epoch"/> moves or <see cref="Close"/>
    /// releases the viewer.</summary>
    public DeliverySpan Span => m_spans.Span;

    /// <summary>Settles the access for the frame being prepared.</summary>
    /// <param name="level">The access the viewer's session has now.</param>
    /// <param name="admission">The session object that access was admitted through (its observation), compared by
    /// reference: another one is another session, even at the same level.</param>
    /// <returns><see langword="true"/> when the access narrowed, which moved <see cref="Epoch"/> and replaced
    /// <see cref="Span"/>.</returns>
    public bool Settle(WorldSessionAccess level, object? admission) {
        var narrowed = (
            (level < Level) ||
            ((m_admission is not null) && !ReferenceEquals(objA: m_admission, objB: admission))
        );

        Level = level;
        m_admission = admission;
        if (narrowed) {
            Epoch = Interlocked.Increment(location: ref Epochs);
            _ = m_spans.Advance();
        }

        return narrowed;
    }
    /// <summary>Ends the current delivery span once the viewer's session or layer is released.</summary>
    public void Close() => m_spans.Close();
}
/// <summary>Whether a view's content may be rendered now, the epoch of the pictures it may still show
/// (<see cref="WorldViewerAccess.Epoch"/>), and the <see cref="DeliveryLabel"/> route its instance renders under.</summary>
/// <param name="Available">Whether the view may render current content.</param>
/// <param name="Epoch">The pictures the view may show: a change of it withdraws every picture published before.</param>
/// <param name="Route">The provenance label of every picture the view produces.</param>
public readonly record struct WorldViewAccess(bool Available, long Epoch, DeliveryLabel Route) {
    /// <summary>A view no session gates: always available, its epoch never moving.</summary>
    public static WorldViewAccess Open => new(
        Available: true,
        Epoch: 0L,
        Route: DeliveryLabel.ForSource(source: DeliveryPublicSource.Host)
    );
    /// <summary>A view whose session is gone: unavailable, at an epoch no live session takes and an ended span.</summary>
    public static WorldViewAccess Retired => new(
        Available: false,
        Epoch: long.MaxValue,
        Route: DeliveryLabel.Of(span: DeliverySpan.Ended)
    );
}
