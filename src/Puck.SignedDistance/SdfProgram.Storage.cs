using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;

namespace Puck.SignedDistance;

// A composed world's program runs to millions of words and hundreds of thousands of instructions, rebuilt whenever its
// field moves; held in managed arrays, each build left tens of megabytes on the large object heap that only a gen2
// collection takes back, between long-lived arrays that kept its regions open. A program built with a storage
// (SdfProgramStorage) holds them in native blocks instead, owned by claims.
//
// Invariant: a program that owns native storage is read (its field words, its appearance section, its instructions and
// every analysis that walks them) only while a claim on it stands, and its last release frees that storage. The program's
// maker holds its first claim. Every holder that keeps the program past the call that handed it over and reads its storage
// later, or on another thread, takes its claim at one door, Exchange, which claims the program it is handed before it
// releases the one it held. A reference kept only to compare identity (ReferenceEquals, SharesField) or to read what stays
// managed (the instances, the appearance, the counts, the bounds, the sampled fields) takes no claim: none of those read
// the freed storage. The holders are named in docs/plans/open-items.md's residency packet and in each holder's own field.
//
// Enforced: the storage is reached only through PackedWords, InstructionRows and AppearanceSection, which read it through
// its OwnedBuffer, whose guard throws ObjectDisposedException once the last claim freed it; a holder that skipped its claim
// fails loudly on the thread that reads after the release. Where it stops: a read on one thread that races the last
// release on another is past that guard, which is why every holder that reads across threads claims; no type makes a
// holder claim.
public sealed partial class SdfProgram {
    // The claims on this program's own storage (its appearance section) and its claim on the field's (m_storage). A
    // managed program's claims are never counted: it holds nothing a release could free.
    private ClaimCount m_claims = new(claims: 1);
    // The appearance section, when the field's storage is native.
    private OwnedBuffer<uint>? m_ownedAppearance;

    /// <summary>Gets whether the program holds its words, appearance section and instructions in native storage its claims
    /// own (<see cref="Acquire"/>, <see cref="Release"/>), which its last release frees; a program built without a storage
    /// holds managed arrays and needs no release.</summary>
    public bool OwnsNativeStorage => m_storage.Native;

    /// <summary>Takes a claim on the program's storage, so it stays readable until the claim is released. A managed program
    /// takes none.</summary>
    /// <returns>This program.</returns>
    /// <exception cref="ObjectDisposedException">The program owns native storage and its last claim was released.</exception>
    public SdfProgram Acquire() {
        if (m_storage.Native) { m_claims.Acquire(owner: this); }
        return this;
    }
    /// <summary>Takes a claim unless the program's last one was already released: a holder that names the program without
    /// a claim of its own (a reference kept for identity) takes one this way before it reads the storage.</summary>
    /// <returns><see langword="true"/> when the claim was taken, or the program is managed; <see langword="false"/> when
    /// its storage is gone.</returns>
    public bool TryAcquire() => (!m_storage.Native || m_claims.TryAcquire());
    /// <summary>Releases one claim; the last frees the appearance section and the program's claim on its field's storage,
    /// which the last program sharing the field frees. A managed program releases nothing.</summary>
    /// <exception cref="InvalidOperationException">More claims were released than taken.</exception>
    public void Release() {
        if (!m_storage.Native || !m_claims.Release()) { return; }
        m_ownedAppearance?.Dispose();
        m_storage.Release();
    }
    /// <summary>Swaps a holder's claim: claims <paramref name="next"/>, then releases the program
    /// <paramref name="held"/> named, so a program both name keeps its storage throughout. The one door through which a
    /// holder that reads a program's storage after the call that handed it over keeps that program.</summary>
    /// <param name="held">The holder's field, which names <paramref name="next"/> afterward.</param>
    /// <param name="next">The program the holder keeps now, or <see langword="null"/> to keep none.</param>
    /// <exception cref="ObjectDisposedException"><paramref name="next"/> owns native storage and its last claim was
    /// released; <paramref name="held"/> is left as it was.</exception>
    public static void Exchange(ref SdfProgram? held, SdfProgram? next) {
        if (ReferenceEquals(objA: held, objB: next)) { return; }
        _ = next?.Acquire();

        var previous = held;

        held = next;
        previous?.Release();
    }

    // The field's words, written by the constructor alone; readable only under a claim on a native program.
    private Span<uint> PackedWords => m_storage.Words.Span;
    // The instructions, patched by the constructor alone (scope scales, table offsets); readable only under a claim on a
    // native program.
    private Span<SdfInstruction> InstructionRows => m_storage.Instructions.Span;

    // Allocates a native appearance section of the field's storage, or returns a managed one, sized for this program.
    private Span<uint> NewAppearanceSection() {
        if (m_storage.NewBlock<uint>(length: m_appearanceWordCount) is not { } block) {
            m_appearanceWords = new uint[m_appearanceWordCount];
            return m_appearanceWords;
        }

        m_ownedAppearance = block;
        return block.GetSpan();
    }

    // The field derivation's storage: the instructions the constructor snapshotted and patched and the words it packed from
    // them, shared by every program that takes the derivation with another appearance (WithAppearance), each holding one
    // claim on it, so the last of them frees it. Managed arrays when the program was built without a storage.
    private sealed class FieldStorage {
        private readonly SdfProgramStorage? m_native;
        private readonly OwnedBuffer<SdfInstruction>? m_ownedInstructions;
        private OwnedBuffer<uint>? m_ownedWords;
        private ClaimCount m_claims = new(claims: 1);

        // Snapshots the instructions, natively when a storage is named.
        public FieldStorage(IReadOnlyList<SdfInstruction> instructions, SdfProgramStorage? storage) {
            m_native = storage;
            if (storage is null) {
                Instructions = (SdfInstruction[])[.. instructions];
                return;
            }

            m_ownedInstructions = OwnedBuffer<SdfInstruction>.OfLength(allocator: storage.Allocator, clear: false, length: instructions.Count, observer: storage.Observer);
            var destination = m_ownedInstructions.GetSpan();

            switch (instructions) {
                case List<SdfInstruction> list: CollectionsMarshal.AsSpan(list: list).CopyTo(destination: destination); break;
                case SdfInstruction[] array: array.CopyTo(destination: destination); break;
                default:
                    for (var index = 0; (index < destination.Length); index++) { destination[index] = instructions[index]; }
                    break;
            }
            Instructions = m_ownedInstructions.Memory;
        }

        public Memory<SdfInstruction> Instructions { get; }
        public Memory<uint> Words { get; private set; }
        public bool Native => (m_native is not null);

        // Allocates the field's words, zeroed, once the derivation knows their count.
        public void AllocateWords(int count) {
            if (m_native is not { } storage) {
                Words = new uint[count];
                return;
            }

            m_ownedWords = OwnedBuffer<uint>.OfLength(allocator: storage.Allocator, clear: true, length: count, observer: storage.Observer);
            Words = m_ownedWords.Memory;
        }
        // A zeroed block of the same allocator and observer, owned by the caller, or null for a managed storage.
        public OwnedBuffer<T>? NewBlock<T>(int length) where T : unmanaged =>
            ((m_native is { } storage) ? OwnedBuffer<T>.OfLength(allocator: storage.Allocator, clear: true, length: length, observer: storage.Observer) : null);
        public void Acquire() => m_claims.Acquire(owner: this);
        public void Release() {
            if (!m_claims.Release()) { return; }
            m_ownedWords?.Dispose();
            m_ownedInstructions?.Dispose();
        }
    }
}
