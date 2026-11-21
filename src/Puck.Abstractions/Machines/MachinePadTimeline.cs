using System.Runtime.InteropServices;

namespace Puck.Abstractions.Machines;

/// <summary>One change of a controller image inside a segment: the image the controller holds from
/// <paramref name="OffsetTicks"/> on, until its next change or for good.</summary>
/// <param name="OffsetTicks">The engine ticks from the segment's start to the change.</param>
/// <param name="Pad">The complete controller image from then on, including released controls.</param>
public readonly record struct MachinePadChange(ulong OffsetTicks, MachinePadState Pad);
/// <summary>One change of one seat's controller image inside a segment, as a machine that reads several seats (a
/// multi-port machine, a cable-linked group) takes it.</summary>
/// <param name="OffsetTicks">The engine ticks from the segment's start to the change.</param>
/// <param name="Seat">The seat whose image changes, in the machine's seat order.</param>
/// <param name="Pad">The seat's complete controller image from then on.</param>
public readonly record struct MachineSeatChange(ulong OffsetTicks, int Seat, MachinePadState Pad);
/// <summary>
/// A controller image over one segment: the image held at the segment's start and each change after it, in rising
/// offset order. A press therefore reaches a machine at the instant inside the segment it happened, not at the next
/// segment's start. Two contributors to one controller (two players on one cabinet, two screens routed to one port)
/// merge instant by instant through <see cref="MachinePadState.Merge"/>, so the merged image at any offset is the merge
/// of what each contributor held then.
/// <para>
/// An instance is reusable scratch: <see cref="Reset"/> starts it over, and nothing is allocated once its change list
/// has grown to the busiest segment it met.
/// </para>
/// </summary>
public sealed class MachinePadTimeline {
    private readonly List<MachinePadChange> m_changes = [];
    private readonly List<MachinePadChange> m_scratch = [];

    /// <summary>Gets the changes after the segment's start, in rising offset order, each at an offset above zero. The
    /// span is valid until the timeline next changes.</summary>
    public ReadOnlySpan<MachinePadChange> Changes => CollectionsMarshal.AsSpan(list: m_changes);
    /// <summary>Gets the image held when the segment ends: the last change's, or <see cref="Start"/> with none.</summary>
    public MachinePadState End => ((m_changes.Count == 0)
        ? Start
        : m_changes[^1].Pad
    );
    /// <summary>Gets the image held at the segment's start.</summary>
    public MachinePadState Start { get; private set; }

    /// <summary>Appends a change at or after the last one. A change that leaves the image as it is, is dropped, and a
    /// change at the last one's offset replaces it, so two changes never share an instant.</summary>
    /// <param name="offsetTicks">The engine ticks from the segment's start; at least the last change's.</param>
    /// <param name="pad">The image from then on.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetTicks"/> is before the last change.</exception>
    public void Add(ulong offsetTicks, in MachinePadState pad) {
        if (m_changes.Count != 0) {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                other: m_changes[^1].OffsetTicks,
                value: offsetTicks
            );

            if (m_changes[^1].OffsetTicks == offsetTicks) {
                m_changes.RemoveAt(index: (m_changes.Count - 1));
            }
        }
        if (offsetTicks == 0UL) {
            // Nothing precedes offset zero, so a change there is the segment's start.
            Start = pad;

            return;
        }
        if (!End.Equals(other: pad)) {
            m_changes.Add(item: new MachinePadChange(
                OffsetTicks: offsetTicks,
                Pad: pad
            ));
        }
    }
    /// <summary>Merges another contributor to the same controller into this one, instant by instant.</summary>
    /// <param name="start">The image the other contributor holds at the segment's start.</param>
    /// <param name="changes">The other contributor's changes, in rising offset order.</param>
    public void Merge(in MachinePadState start, ReadOnlySpan<MachinePadChange> changes) {
        var mine = Start;
        var theirs = start;
        var left = 0;
        var right = 0;

        m_scratch.Clear();
        m_scratch.AddRange(collection: m_changes);
        m_changes.Clear();
        Start = MachinePadState.Merge(
            first: in mine,
            second: in theirs
        );

        while ((left < m_scratch.Count) || (right < changes.Length)) {
            var leftOffset = ((left < m_scratch.Count) ? m_scratch[left].OffsetTicks : ulong.MaxValue);
            var rightOffset = ((right < changes.Length) ? changes[right].OffsetTicks : ulong.MaxValue);
            var offset = Math.Min(
                val1: leftOffset,
                val2: rightOffset
            );

            if (leftOffset == offset) {
                mine = m_scratch[left++].Pad;
            }
            if (rightOffset == offset) {
                theirs = changes[right++].Pad;
            }

            Add(
                offsetTicks: offset,
                pad: MachinePadState.Merge(
                    first: in mine,
                    second: in theirs
                )
            );
        }
    }
    /// <summary>Starts the timeline over with one image held for the whole segment.</summary>
    /// <param name="start">The image held at the segment's start.</param>
    public void Reset(in MachinePadState start) {
        m_changes.Clear();
        Start = start;
    }
}
