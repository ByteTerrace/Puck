using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;

namespace Puck.State;

/// <summary>Marks a value type as a section: state a holder owns (<see cref="Versioned{T}"/>,
/// <see cref="VersionedRecord{T}"/>) and hands out through its two sides. The marker is what lets the compiler
/// require it of a holder's argument and the DOOR analyzer rules recognise a section wherever one is named.</summary>
public interface IVersionedSection;
/// <summary>The digest of a section: a static function of the section's value and nothing else.</summary>
/// <typeparam name="TSection">The section type.</typeparam>
/// <typeparam name="TWriter">The scratch the encoding is written into.</typeparam>
public interface ISectionCodec<TSection, in TWriter> {
    /// <summary>Encodes a section and digests the encoding.</summary>
    /// <param name="writer">The scratch to encode into; its previous contents are discarded.</param>
    /// <param name="section">The section.</param>
    /// <param name="bytes">The number of bytes the section encoded to.</param>
    /// <returns>The digest of the encoding.</returns>
    static abstract ulong Digest(TWriter writer, in TSection section, out int bytes);
}
/// <summary>The digest of a table section: a static function of its elements and nothing else.</summary>
/// <typeparam name="TElement">The element type.</typeparam>
/// <typeparam name="TWriter">The scratch the encoding is written into.</typeparam>
public interface ISectionTableCodec<TElement, in TWriter> {
    /// <summary>Encodes a table and digests the encoding.</summary>
    /// <param name="writer">The scratch to encode into; its previous contents are discarded.</param>
    /// <param name="elements">The table's elements.</param>
    /// <param name="bytes">The number of bytes the table encoded to.</param>
    /// <returns>The digest of the encoding.</returns>
    static abstract ulong Digest(TWriter writer, ReadOnlySpan<TElement> elements, out int bytes);
}
/// <summary>What a run of holder digest reads did, for the reader to publish as counters. Every count is
/// deterministic: it follows the writes and the values, never the clock.</summary>
public struct SectionDigestWork {
    /// <summary>The sections encoded.</summary>
    public long Encodes;
    /// <summary>The bytes those sections encoded to.</summary>
    public long EncodedBytes;
    /// <summary>The raw bytes compared with the bytes a digest was taken from.</summary>
    public long ComparedBytes;
    /// <summary>The sections whose version had moved over unchanged content: a write that changed nothing, which the
    /// compare answered without an encode.</summary>
    public long FilteredCompares;
    /// <summary>The sections that encoded to the digest they already had: a section's first move, before it keeps
    /// the bytes a later move is compared with.</summary>
    public long UnchangedEncodes;
}
/// <summary>
/// What every holder shares: the version its write side moves, the binding it notes through, and the digest cache its
/// digest read answers from. A cached digest is handed out only under the version it was taken at, or over content
/// equal to the content it was taken from; the cache is private to the holder types, so no caller can store a digest.
/// </summary>
public abstract class VersionedHolder {
    private readonly SlotBinding m_binding;
    private ulong m_version = 1UL;
    private ulong m_digest;
    // The version the cached digest was taken at or last confirmed under; zero while there is none.
    private ulong m_digestVersion;
    // The restore point the holder last saved its value under (SlotBinding.BeginRestore); zero while it never has.
    private ulong m_savedEpoch;

    private protected VersionedHolder(SlotBinding binding) {
        ArgumentNullException.ThrowIfNull(argument: binding);

        m_binding = binding;
    }

    /// <summary>Gets the holder's version: it moves on every access to the write side, whether or not the access
    /// changes a value.</summary>
    public ulong Version => m_version;
    /// <summary>Gets the binding the holder notes its writes through.</summary>
    public SlotBinding Binding => m_binding;

    // The write side's first act: under an open restore point the holder keeps the value it is about to hand out for
    // writing, then the version moves and the slot is noted, all before the writable reference exists.
    private protected void Touch() {
        if (m_binding.OwesSave(holder: this, savedEpoch: ref m_savedEpoch, bytes: SavedBytes)) { Save(); }
        m_version++;
        m_binding.Note();
    }

    // The bytes one save copies.
    private protected abstract long SavedBytes { get; }
    // Copies the current value aside, over whatever the last save left.
    private protected abstract void Save();
    // Puts the saved value back as a write: the version moves and the slot is noted.
    internal abstract void RestoreSaved();
    // The write a restore makes: it never saves, since what it writes is the saved value.
    private protected void TouchRestored() {
        m_version++;
        m_binding.Note();
    }

    private protected bool HasCurrentDigest => (m_digestVersion == m_version);
    private protected bool HasDigest => (m_digestVersion != 0UL);
    private protected ulong CachedDigest => m_digest;

    // The version moved over unchanged content: the cached digest stands for the current version.
    private protected ulong Filter(ref SectionDigestWork work, long comparedBytes) {
        work.ComparedBytes += comparedBytes;
        work.FilteredCompares++;
        m_digestVersion = m_version;
        return m_digest;
    }
    // A fresh encoding becomes the cache. Answers whether the holder should keep the content it was taken from: from
    // the second encode on, so a section that never moves again holds nothing twice.
    private protected ulong Encoded(ref SectionDigestWork work, ulong digest, int bytes, out bool keep) {
        work.Encodes++;
        work.EncodedBytes += bytes;
        keep = HasDigest;
        if (keep && (digest == m_digest)) { work.UnchangedEncodes++; }
        m_digest = digest;
        m_digestVersion = m_version;
        return digest;
    }
    // The checked read: what is about to be handed out against a fresh encoding. A difference is reported once by
    // name through the notes and healed by adopting the fresh digest.
    private protected bool IsChecked => (m_binding.Notes is { Checked: true });
    private protected ulong Check(ulong handedOut, ulong fresh, string section, out bool stale) {
        stale = (handedOut != fresh);
        if (stale) {
            m_binding.Notes?.ReportStale(fault: $"slot {m_binding.Slot} section {section}: the digest {handedOut:X16} handed out at version {m_version} differs from its fresh encoding's {fresh:X16}");
            m_digest = fresh;
            m_digestVersion = m_version;
        }
        return fresh;
    }
}
/// <summary>
/// The holder of one fixed-size section: the section is private to it and leaves through two sides. <see cref="Read"/>
/// is a reference the compiler lets nobody write through; <see cref="Write"/> moves the version and notes the slot
/// before it returns a writable one. The section type holds no reference, so nothing inside it can be reached and
/// written around the door.
/// </summary>
/// <typeparam name="T">The section type.</typeparam>
/// <remarks>The digest read compares a moved section's raw bytes with the bytes its digest was taken from before it
/// encodes, so a section written back to the value it held costs a compare and no writer has to compare first. The
/// compare is of raw memory: a difference in padding can cause an encode and never suppress one.</remarks>
public sealed class Versioned<T> : VersionedHolder where T : unmanaged, IVersionedSection {
    private T m_value;
    // The value the cached digest was taken from, kept from the second encode on.
    private T[]? m_kept;
    // The value an open restore point saved, kept from the first save on.
    private T[]? m_saved;

    private protected override long SavedBytes => Unsafe.SizeOf<T>();
    private protected override void Save() => (m_saved ??= new T[1])[0] = m_value;
    internal override void RestoreSaved() {
        TouchRestored();
        m_value = m_saved![0];
    }

    /// <summary>Initializes a new instance of the <see cref="Versioned{T}"/> class.</summary>
    /// <param name="binding">The binding the holder notes its writes through.</param>
    /// <param name="value">The section's first value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public Versioned(SlotBinding binding, T value = default) : base(binding: binding) {
        m_value = value;
    }

    /// <summary>Gets the section for reading.</summary>
    public ref readonly T Read => ref m_value;
    /// <summary>Gets the section for writing. The version moves and the slot is noted on every access; the reference
    /// is for one statement or one call and is never kept.</summary>
    public ref T Write {
        get {
            Touch();
            return ref m_value;
        }
    }

    /// <summary>Returns the digest of the section's current value: the cached one when the version it was taken at
    /// still stands or the section's bytes equal the bytes it was taken from, a fresh encoding otherwise. Under the
    /// notes' checked mode the answer is also compared with a fresh encoding.</summary>
    /// <typeparam name="TCodec">The section's codec.</typeparam>
    /// <typeparam name="TWriter">The scratch the codec encodes into.</typeparam>
    /// <param name="writer">The scratch.</param>
    /// <param name="work">The counts of what the read did.</param>
    /// <returns>The digest.</returns>
    public ulong Digest<TCodec, TWriter>(TWriter writer, ref SectionDigestWork work) where TCodec : ISectionCodec<T, TWriter> {
        ulong digest;

        if (HasCurrentDigest) {
            digest = CachedDigest;
        } else if ((m_kept is { } kept) && Bytes(value: in kept[0]).SequenceEqual(other: Bytes(value: in m_value))) {
            digest = Filter(comparedBytes: Unsafe.SizeOf<T>(), work: ref work);
        } else {
            if (m_kept is not null) { work.ComparedBytes += Unsafe.SizeOf<T>(); }
            var fresh = TCodec.Digest(bytes: out var bytes, section: in m_value, writer: writer);

            digest = Encoded(bytes: bytes, digest: fresh, keep: out var keep, work: ref work);
            if (keep) { (m_kept ??= new T[1])[0] = m_value; }
        }
        if (IsChecked) {
            digest = Check(fresh: TCodec.Digest(bytes: out _, section: in m_value, writer: writer), handedOut: digest, section: typeof(T).Name, stale: out var stale);
            if (stale) { m_kept = null; }
        }
        return digest;
    }

    private static ReadOnlySpan<byte> Bytes(in T value) => MemoryMarshal.AsBytes(span: new ReadOnlySpan<T>(reference: in value));
}
/// <summary>
/// The holder of one table section, for state whose length the kit or the step decides. It reads as a span nobody
/// can write through and writes as a span whose every access moves the version and notes the slot.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VersionedArray<T> : VersionedHolder where T : unmanaged {
    private T[] m_items = [];
    private int m_count;
    // The elements the cached digest was taken from, kept from the second encode on.
    private T[]? m_kept;
    private int m_keptCount;
    // The elements an open restore point saved, and how many.
    private T[] m_saved = [];
    private int m_savedCount;

    private protected override long SavedBytes => (((long)m_count) * Unsafe.SizeOf<T>());
    private protected override void Save() {
        ArrayCapacity.EnsureLength(ref m_saved, m_count);
        m_items.AsSpan(length: m_count, start: 0).CopyTo(destination: m_saved);
        m_savedCount = m_count;
    }
    internal override void RestoreSaved() {
        TouchRestored();
        ArrayCapacity.EnsureLength(ref m_items, m_savedCount);
        m_saved.AsSpan(length: m_savedCount, start: 0).CopyTo(destination: m_items);
        m_count = m_savedCount;
    }

    /// <summary>Initializes a new instance of the <see cref="VersionedArray{T}"/> class, empty.</summary>
    /// <param name="binding">The binding the holder notes its writes through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public VersionedArray(SlotBinding binding) : base(binding: binding) {
    }

    /// <summary>Gets the number of elements.</summary>
    public int Count => m_count;
    /// <summary>Gets the elements for reading.</summary>
    public ReadOnlySpan<T> Read => m_items.AsSpan(length: m_count, start: 0);
    /// <summary>Gets the elements for writing. The version moves and the slot is noted on every access; the span is
    /// for one statement or one call and is never kept.</summary>
    public Span<T> Write {
        get {
            Touch();
            return m_items.AsSpan(length: m_count, start: 0);
        }
    }

    /// <summary>Sets the number of elements and returns them for writing. Elements below both counts keep their
    /// values; the rest are the default.</summary>
    /// <param name="count">The number of elements from now on.</param>
    /// <returns>The elements, as <see cref="Write"/> hands them out.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Span<T> Resize(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        Touch();
        ArrayCapacity.EnsureLength(ref m_items, count);
        if (count > m_count) {
            m_items.AsSpan(length: (count - m_count), start: m_count).Clear();
        }
        m_count = count;
        return m_items.AsSpan(length: count, start: 0);
    }
    /// <summary>Returns the digest of the table's current elements, as <see cref="Versioned{T}.Digest"/> answers a
    /// section's: the compare is of the count and of the elements' raw bytes.</summary>
    /// <typeparam name="TCodec">The table's codec.</typeparam>
    /// <typeparam name="TWriter">The scratch the codec encodes into.</typeparam>
    /// <param name="writer">The scratch.</param>
    /// <param name="work">The counts of what the read did.</param>
    /// <returns>The digest.</returns>
    public ulong Digest<TCodec, TWriter>(TWriter writer, ref SectionDigestWork work) where TCodec : ISectionTableCodec<T, TWriter> {
        var elements = Read;
        var size = (((long)elements.Length) * Unsafe.SizeOf<T>());
        ulong digest;

        if (HasCurrentDigest) {
            digest = CachedDigest;
        } else if ((m_kept is { } kept) && (m_keptCount == elements.Length) && MemoryMarshal.AsBytes(span: ((ReadOnlySpan<T>)kept.AsSpan(length: m_keptCount, start: 0))).SequenceEqual(other: MemoryMarshal.AsBytes(span: elements))) {
            digest = Filter(comparedBytes: size, work: ref work);
        } else {
            if ((m_kept is not null) && (m_keptCount == elements.Length)) { work.ComparedBytes += size; }
            var fresh = TCodec.Digest(bytes: out var bytes, elements: elements, writer: writer);

            digest = Encoded(bytes: bytes, digest: fresh, keep: out var keep, work: ref work);
            if (keep) {
                if ((m_kept is null) || (m_kept.Length < elements.Length)) { m_kept = new T[elements.Length]; }
                elements.CopyTo(destination: m_kept);
                m_keptCount = elements.Length;
            }
        }
        if (IsChecked) {
            digest = Check(fresh: TCodec.Digest(bytes: out _, elements: elements, writer: writer), handedOut: digest, section: $"{typeof(T).Name}[]", stale: out var stale);
            if (stale) { m_kept = null; }
        }
        return digest;
    }
}
/// <summary>
/// The holder of one section that carries immutable references (a name, a principal, an immutable array): the same
/// door as <see cref="Versioned{T}"/> without the constraint that the section holds no reference. What that
/// constraint cannot say, that every field is deeply immutable, the DOOR003 analyzer rule holds at the declaration.
/// </summary>
/// <typeparam name="T">The section type.</typeparam>
/// <remarks>The digest read compares a moved section with the value its digest was taken from by the section's own
/// equality, under which an immutable array is equal only to itself.</remarks>
public sealed class VersionedRecord<T> : VersionedHolder where T : struct, IVersionedSection, IEquatable<T> {
    private T m_value;
    // The value the cached digest was taken from, kept from the second encode on.
    private T[]? m_kept;
    // The value an open restore point saved, kept from the first save on.
    private T[]? m_saved;

    private protected override long SavedBytes => Unsafe.SizeOf<T>();
    private protected override void Save() => (m_saved ??= new T[1])[0] = m_value;
    internal override void RestoreSaved() {
        TouchRestored();
        m_value = m_saved![0];
    }

    /// <summary>Initializes a new instance of the <see cref="VersionedRecord{T}"/> class.</summary>
    /// <param name="binding">The binding the holder notes its writes through.</param>
    /// <param name="value">The section's first value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public VersionedRecord(SlotBinding binding, T value = default) : base(binding: binding) {
        m_value = value;
    }

    /// <summary>Gets the section for reading.</summary>
    public ref readonly T Read => ref m_value;
    /// <summary>Gets the section for writing. The version moves and the slot is noted on every access; the reference
    /// is for one statement or one call and is never kept.</summary>
    public ref T Write {
        get {
            Touch();
            return ref m_value;
        }
    }

    /// <summary>Returns the digest of the section's current value, as <see cref="Versioned{T}.Digest"/> answers a
    /// fixed-size section's, comparing by the section's equality.</summary>
    /// <typeparam name="TCodec">The section's codec.</typeparam>
    /// <typeparam name="TWriter">The scratch the codec encodes into.</typeparam>
    /// <param name="writer">The scratch.</param>
    /// <param name="work">The counts of what the read did.</param>
    /// <returns>The digest.</returns>
    public ulong Digest<TCodec, TWriter>(TWriter writer, ref SectionDigestWork work) where TCodec : ISectionCodec<T, TWriter> {
        ulong digest;

        if (HasCurrentDigest) {
            digest = CachedDigest;
        } else if ((m_kept is { } kept) && kept[0].Equals(other: m_value)) {
            digest = Filter(comparedBytes: 0L, work: ref work);
        } else {
            var fresh = TCodec.Digest(bytes: out var bytes, section: in m_value, writer: writer);

            digest = Encoded(bytes: bytes, digest: fresh, keep: out var keep, work: ref work);
            if (keep) { (m_kept ??= new T[1])[0] = m_value; }
        }
        if (IsChecked) {
            digest = Check(fresh: TCodec.Digest(bytes: out _, section: in m_value, writer: writer), handedOut: digest, section: typeof(T).Name, stale: out var stale);
            if (stale) { m_kept = null; }
        }
        return digest;
    }
}
