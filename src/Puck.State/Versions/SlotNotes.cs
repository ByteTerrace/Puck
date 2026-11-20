using Puck.Maths;

namespace Puck.State;

/// <summary>
/// Where the holders of one slot's state note that they were written: the slot's place in its population's
/// <see cref="SlotNotes"/>. Every holder an owner constructs takes the owner's one binding, so a holder never touches
/// the shared versions itself, and an owner that belongs to no population (a binding never bound) notes nothing.
/// </summary>
public sealed class SlotBinding {
    private SlotNotes? m_notes;
    // Set by the first write since the notes were last published; later writes see it and append nothing.
    private bool m_noted;

    /// <summary>Gets the slot the owner occupies; meaningful only while bound.</summary>
    public int Slot { get; private set; }
    /// <summary>Gets the notes the binding appends to, or <see langword="null"/> while unbound.</summary>
    public SlotNotes? Notes => m_notes;

    /// <summary>Points the binding at a slot of a population's notes, or at nothing. A write noted and not yet
    /// published under the previous binding is dropped: the slot's own change of occupant is the population's to
    /// note.</summary>
    /// <param name="notes">The notes to append to, or <see langword="null"/> to unbind.</param>
    /// <param name="slot">The slot the owner occupies.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside <paramref name="notes"/>.</exception>
    public void Bind(SlotNotes? notes, int slot) {
        if (notes is not null) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: notes.Count, value: slot);
        }

        m_notes = notes;
        m_noted = false;
        Slot = slot;
    }

    // Notes the slot once per publish. Called by a holder's write side, on the thread that owns the slot's partition.
    internal void Note() {
        if (m_noted || (m_notes is not { } notes)) {
            return;
        }

        m_noted = true;
        notes.Append(binding: this, slot: Slot);
    }
    // Called by the publish for a binding it drained; a binding re-bound since it noted belongs to other notes.
    internal void Published(SlotNotes notes) {
        if (ReferenceEquals(objA: m_notes, objB: notes)) { m_noted = false; }
    }
}
/// <summary>
/// A population's record of which slots were written since it last published: one list per partition of
/// consecutive slots, appended to by the holders of that partition's slots and drained, in partition order, into the
/// shared <see cref="Versions"/> by <see cref="Publish"/>, which is their one writer.
/// </summary>
/// <remarks>
/// <para>A write appends to the list of its own slot's partition and touches nothing shared, so a step that hands
/// each partition to one worker has no two workers writing one list. A reader publishes before it reads, under
/// whatever gate serialises the population's readers, so the shared versions keep one writer however the step
/// forks and no note is lost between a write and a read.</para>
/// <para>The checked mode and the stale-digest report serve the holders' digest reads
/// (<see cref="Versioned{T}"/>): while <see cref="Checked"/> holds, every digest a holder hands out is compared with
/// a fresh encoding, and the first difference is kept here by name.</para>
/// </remarks>
public sealed class SlotNotes {
    private readonly int m_partitionSlots;
    private List<(SlotBinding? Binding, int Slot)>[] m_partitions;

    /// <summary>Initializes a new instance of the <see cref="SlotNotes"/> class with nothing noted.</summary>
    /// <param name="count">The number of slots.</param>
    /// <param name="partitionSlots">The number of consecutive slots one partition holds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or
    /// <paramref name="partitionSlots"/> is not positive.</exception>
    public SlotNotes(int count, int partitionSlots) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: partitionSlots);

        m_partitionSlots = partitionSlots;
        m_partitions = NewPartitions(count: count, partitionSlots: partitionSlots);
        Versions = new IndexVersions(count: count);
    }

    /// <summary>Gets the number of slots.</summary>
    public int Count => Versions.Count;
    /// <summary>Gets the number of partitions the slots divide into.</summary>
    public int PartitionCount => m_partitions.Length;
    /// <summary>Gets the slots' versions and the order they last moved in. A reader calls <see cref="Publish"/> and
    /// then follows the moves from its own place (<see cref="IndexVersions.TryReadMoves"/>).</summary>
    public IndexVersions Versions { get; }
    /// <summary>Gets or sets a value indicating whether every holder's digest read also encodes afresh and compares
    /// with what it hands out.</summary>
    public bool Checked { get; set; }
    /// <summary>Gets the first digest a checked read found stale, by slot, section, version and both digests, or
    /// <see langword="null"/> while every checked read agreed.</summary>
    public string? StaleDigest { get; private set; }

    private static List<(SlotBinding? Binding, int Slot)>[] NewPartitions(int count, int partitionSlots) {
        var partitions = new List<(SlotBinding? Binding, int Slot)>[count.CeilingDivide(divisor: partitionSlots)];

        for (var partition = 0; (partition < partitions.Length); partition++) { partitions[partition] = []; }
        return partitions;
    }

    /// <summary>Returns the partition a slot belongs to.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The partition's ordinal.</returns>
    public int PartitionOf(int slot) => (slot / m_partitionSlots);
    /// <summary>Notes a slot the population itself changed (a slot that took or lost its occupant), as a holder's
    /// write notes its own. Called on the thread that owns the slot's partition.</summary>
    /// <param name="slot">The slot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside the notes.</exception>
    public void Note(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: Count, value: slot);
        m_partitions[PartitionOf(slot: slot)].Add(item: (null, slot));
    }
    /// <summary>Moves the version of every slot noted since the last publish, partition by partition in ascending
    /// order and within a partition in the order the slots were first written, and clears the notes. A slot its
    /// holders wrote any number of times is listed once and moves once; a reader is handed a moved slot once however
    /// often it moved. This is the one writer of <see cref="Versions"/> beside <see cref="Reset"/>, so its callers
    /// are serialised with one another and with every reader.</summary>
    /// <returns>The number of notes published.</returns>
    public int Publish() {
        var published = 0;

        foreach (var partition in m_partitions) {
            foreach (var (binding, slot) in partition) {
                binding?.Published(notes: this);
                Versions.Bump(index: slot);
                published++;
            }
            partition.Clear();
        }

        return published;
    }
    /// <summary>Restarts the notes for a population whose slots were rebuilt: nothing is noted, every version takes
    /// zero, and every reader takes every slot as moved.</summary>
    /// <param name="count">The number of slots from now on.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void Reset(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        foreach (var partition in m_partitions) {
            foreach (var (binding, _) in partition) { binding?.Published(notes: this); }
        }
        m_partitions = NewPartitions(count: count, partitionSlots: m_partitionSlots);
        Versions.ResetAll(count: count, version: 0UL);
    }
    /// <summary>Clears the stale-digest report, for a reader that has narrated it.</summary>
    public void ClearStaleDigest() => StaleDigest = null;

    internal void Append(SlotBinding binding, int slot) => m_partitions[PartitionOf(slot: slot)].Add(item: (binding, slot));
    internal void ReportStale(string fault) => StaleDigest ??= fault;
}
