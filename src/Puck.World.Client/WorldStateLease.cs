using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// One holder's reads of a <see cref="WorldStateMirror"/>: the slots a body, a stamp registration or a seat reads
/// through, acquired on first read and released together. A reference's <c>$body</c> key names the body the lease is
/// bound to (<see cref="StateBinding.BodyKey"/>), so two bodies reading one authored reference read two
/// slots, and a lease bound to no body reads nothing through such a reference.
/// <para>
/// A slot is found by the authored object that names it — a reference or family string, compared by its text, or a
/// lane instruction's state payload, compared by reference — so a steady-state read costs one table probe and no
/// parse, and a recomposed document spelling the same reference reads the same slot. <see cref="Bind"/> to another
/// mirror or body, <see cref="Release"/>, and the first read after the mirror installs a document hand every slot back,
/// so the mirror retires the ones no other holder reads, and an installed document's own objects acquire afresh. A
/// lease is used on the thread that presents frames, like the mirror it reads.
/// </para>
/// </summary>
public sealed class WorldStateLease {
    private readonly Dictionary<(object Source, bool Target), int> m_slots = new(comparer: SourceComparer.Instance);
    private int m_bodyIndex = -1;

    private int m_installs;
    private WorldStateMirror? m_mirror;

    private sealed class SourceComparer : IEqualityComparer<(object Source, bool Target)> {
        public static readonly SourceComparer Instance = new();

        public bool Equals((object Source, bool Target) x, (object Source, bool Target) y) => (
            (x.Target == y.Target) &&
            (((x.Source is string left) && (y.Source is string right))
                ? string.Equals(
                    a: left,
                    b: right,
                    comparisonType: StringComparison.Ordinal
                )
                : ReferenceEquals(
                    objA: x.Source,
                    objB: y.Source
                ))
        );
        public int GetHashCode((object Source, bool Target) obj) => HashCode.Combine(
            value1: ((obj.Source is string text)
                ? string.GetHashCode(value: text.AsSpan())
                : RuntimeHelpers.GetHashCode(o: obj.Source)),
            value2: obj.Target
        );
    }

    /// <summary>Gets the latest <see cref="WorldStateMirror.Revision"/> at which a slot the lease holds changed its
    /// sample, so a holder that derived something from its reads derives it again only when one of its own rows
    /// moved; zero when it holds none.</summary>
    public int Changed {
        get {
            if (Current() is not { } mirror) {
                return 0;
            }

            var changed = 0;

            foreach (var slot in m_slots.Values) {
                if (slot >= 0) {
                    changed = Math.Max(
                        val1: changed,
                        val2: mirror.Changed(slot: slot)
                    );
                }
            }

            return changed;
        }
    }
    /// <summary>Gets the body a <c>$body</c> key names, or -1 when the lease reads for no body.</summary>
    public int BodyIndex => m_bodyIndex;
    /// <summary>Gets the number of references the lease has resolved, whether or not each resolved to a slot.</summary>
    public int Count => m_slots.Count;
    /// <summary>Gets the mirror the lease reads, or <see langword="null"/> before the first <see cref="Bind"/>.</summary>
    public WorldStateMirror? Mirror => m_mirror;

    /// <summary>Points the lease at a mirror and a body. Binding to the mirror and body it already reads changes
    /// nothing; any other binding first releases every slot held.</summary>
    /// <param name="mirror">The mirror to read.</param>
    /// <param name="bodyIndex">The 0-based body a <c>$body</c> key names, or -1 for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public void Bind(WorldStateMirror mirror, int bodyIndex) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        if (
            ReferenceEquals(
            objA: mirror,
            objB: m_mirror
        ) &&
            (bodyIndex == m_bodyIndex)
        ) {
            return;
        }

        Release();
        m_mirror = mirror;
        m_bodyIndex = bodyIndex;
        m_installs = mirror.Installs;
    }
    /// <summary>Releases every slot the lease holds, keeping its binding; the next read acquires afresh.</summary>
    public void Release() {
        if (m_mirror is { } mirror) {
            foreach (var slot in m_slots.Values) {
                if (slot >= 0) {
                    mirror.Release(slot: slot);
                }
            }
        }

        m_slots.Clear();
    }
    /// <summary>Finds the slot the lease already resolved for an authored object, without acquiring one.</summary>
    /// <param name="source">The authored object naming the read: a string compared by its text, any other object by
    /// reference.</param>
    /// <param name="target">Whether the read is of the stored truth rather than the eased follower.</param>
    /// <param name="slot">The resolved slot, which is -1 when the object resolved to no slot; -1 when not found.</param>
    /// <returns><see langword="true"/> when the lease has resolved the object since the mirror's last install.</returns>
    public bool TryFind(object source, bool target, out int slot) {
        ArgumentNullException.ThrowIfNull(argument: source);

        _ = Current();

        if (m_slots.TryGetValue(
            key: (source, target),
            value: out slot
        )) {
            return true;
        }

        slot = -1;

        return false;
    }
    /// <summary>Returns the slot an already parsed row and key read through, acquiring it on first sight.</summary>
    /// <param name="source">The authored object naming the read: a string compared by its text, any other object by
    /// reference.</param>
    /// <param name="row">The row's name.</param>
    /// <param name="key">The cell key, which may be <see cref="StateBinding.BodyKey"/>, or
    /// <see langword="null"/> for the slot cell.</param>
    /// <param name="target">Whether the read is of the stored truth rather than the eased follower.</param>
    /// <returns>The slot, or -1 when the lease is unbound or the key names a body the lease has none of.</returns>
    public int Slot(object source, string row, string? key, bool target) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: row);

        _ = Current();

        if (m_slots.TryGetValue(
            key: (source, target),
            value: out var slot
        )) {
            return slot;
        }

        slot = Acquire(
            key: key,
            row: row,
            target: target
        );
        m_slots[(source, target)] = slot;

        return slot;
    }
    /// <summary>Returns the slot an authored <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> reference reads
    /// through, parsing it once and acquiring it on first sight. A reference spelling <c>.$target</c> reads the truth
    /// whatever <paramref name="truth"/> says.</summary>
    /// <param name="reference">The authored reference.</param>
    /// <param name="truth">Whether the read is of the stored truth rather than the eased follower.</param>
    /// <returns>The slot, or -1 when the lease is unbound, the reference is no state binding, or its key names a body
    /// the lease has none of.</returns>
    public int Slot(string reference, bool truth) {
        ArgumentNullException.ThrowIfNull(argument: reference);

        _ = Current();

        if (m_slots.TryGetValue(
            key: (reference, truth),
            value: out var slot
        )) {
            return slot;
        }

        slot = (StateBinding.TryParse(
            binding: out var binding,
            token: reference
        )
            ? Acquire(
                key: binding.Key,
                row: binding.Row,
                target: (truth || binding.Target)
            )
            : -1
        );
        m_slots[(reference, truth)] = slot;

        return slot;
    }
    /// <summary>Reads an authored reference's presented number.</summary>
    /// <param name="reference">The authored reference.</param>
    /// <param name="truth">Whether to read the stored truth rather than the eased follower.</param>
    /// <param name="value">The presented number; zero when the cell is absent or holds no number.</param>
    /// <returns><see langword="true"/> when the cell holds a number.</returns>
    public bool TryNumber(string reference, bool truth, out float value) => TryNumber(
        slot: Slot(
            reference: reference,
            truth: truth
        ),
        value: out value
    );
    /// <summary>Reads a slot of this lease's mirror as a presented number.</summary>
    /// <param name="slot">The slot, from <see cref="Slot(object, string, string?, bool)"/> or
    /// <see cref="Slot(string, bool)"/>; -1 reads nothing.</param>
    /// <param name="value">The presented number; zero when the slot is -1 or its cell holds no number.</param>
    /// <returns><see langword="true"/> when the cell holds a number.</returns>
    public bool TryNumber(int slot, out float value) {
        if (
            (slot < 0) ||
            (m_mirror is not { } mirror)
        ) {
            value = 0f;

            return false;
        }

        return mirror.TryNumber(
            slot: slot,
            value: out value
        );
    }
    /// <summary>Returns a slot's current sample through this lease's mirror.</summary>
    /// <param name="slot">The slot, from one of the <c>Slot</c> overloads; -1 reads nothing.</param>
    /// <returns>The sample; its value holds no case when the slot is -1 or the cell does not resolve.</returns>
    public WorldStateSample Sample(int slot) => (((slot >= 0) && (m_mirror is { } mirror))
        ? mirror.Sample(slot: slot)
        : default
    );
    /// <summary>Reads an authored reference's current text.</summary>
    /// <param name="reference">The authored reference.</param>
    /// <param name="value">The text, or <see langword="null"/> when the cell holds none.</param>
    /// <returns><see langword="true"/> when the cell holds text.</returns>
    public bool TryText(string reference, [NotNullWhen(returnValue: true)] out string? value) {
        var slot = Slot(
            reference: reference,
            truth: false
        );

        if (
            (slot < 0) ||
            (m_mirror is not { } mirror)
        ) {
            value = null;

            return false;
        }

        return mirror.TryText(
            slot: slot,
            value: out value
        );
    }

    // The bound mirror, first releasing every slot when it has installed a document since the lease last looked.
    private WorldStateMirror? Current() {
        if (
            (m_mirror is { } mirror) &&
            (mirror.Installs != m_installs)
        ) {
            Release();
            m_installs = mirror.Installs;
        }

        return m_mirror;
    }
    private int Acquire(string row, string? key, bool target) {
        if (
            (m_mirror is not { } mirror) ||
            !WorldGaitDrivers.TryResolveBodyKey(
            bodyIndex: m_bodyIndex,
            key: key,
            resolved: out var resolved
        )
        ) {
            return -1;
        }

        return mirror.Acquire(
            binding: new StateBinding(
                Key: resolved,
                Row: row,
                Target: target
            ),
            conversion: WorldStateConversion.Number
        );
    }
}
