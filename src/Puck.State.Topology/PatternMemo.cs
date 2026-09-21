namespace Puck.State;

/// <summary>One pattern walk's result over a word.</summary>
/// <param name="State">The machine state after the last symbol.</param>
/// <param name="Match">1 when the whole word is in the language, 0 when it is not.</param>
/// <param name="LongestAcceptedPrefix">The length of the longest accepted prefix, 0 when only the empty word is
/// accepted, or -1 when no prefix is.</param>
/// <param name="Steps">How many symbols this walk read — the whole word on a restart, the appended tail on a
/// resume.</param>
/// <param name="Resumed">Whether the walk continued a memoized prefix instead of restarting.</param>
public readonly record struct PatternWalk(int State, long Match, long LongestAcceptedPrefix, int Steps, bool Resumed);
/// <summary>
/// The memo an incremental pattern match resumes from: the derivative state a walk left off in, kept per arena
/// read and pattern, and reused exactly while the row proves that read's prefix unchanged.
/// </summary>
/// <remarks>
/// <para>A memoized walk is identified by the arena and its layout, the <see cref="WordSource"/> the word was read
/// from — the row, the attribute row its letters are read through, the start, and the direction — the pattern, the
/// row's mutation counter, and the length already consumed. A walk resumes only when every one of those still
/// holds and the word is not shorter than what was consumed; anything else restarts from the first symbol, so the
/// answer never depends on what the memo remembered. Two reads over one row are two sources and never share an
/// entry, so a later start or another ray off an unchanged board answers on its own letters.</para>
/// <para>Every one of those is what the word carries from its own read, never what the arena holds when the word
/// is walked. A word kept across a mutation therefore memoizes against the counter it was read under, which no
/// later read shares, and a current word restarts rather than resume from letters the row no longer holds.</para>
/// <para>The counter is <see cref="StateArena.AppendGeneration"/> for an ordered row, which moves on every mutation
/// but a push at the tail, and <see cref="StateArena.RowGeneration"/> for every other shape and for the attribute
/// row, which moves on every mutation at all. A prefix replacement, a removal, a reorder, a clear, a relayout, and
/// a speculative rewind therefore all invalidate the memo, and a tail append preserves it.</para>
/// <para>A relayout replaces the arena's layout, and the memo drops everything it holds when it sees an arena or a
/// layout it did not memoize against.</para>
/// </remarks>
public sealed class PatternMemo {
    private readonly Dictionary<Key, Entry> m_entries = [];

    private ArenaLayout? m_layout;
    private StateArena? m_arena;

    /// <summary>The bytes one memoized walk occupies: a dictionary entry's hash and link, the pattern reference
    /// and word source that key it, and the two proofs, length, state and accepted prefix it holds.</summary>
    public const int EntryBytes = 64;
    /// <summary>The most memory one memo holds before it drops every walk. A board read from every cell in every
    /// direction keys one walk per read, so the figure is a share of memory rather than a count of walks: at
    /// <see cref="EntryBytes"/> each it keeps 16,384 of them.</summary>
    public const int MaxBytes = (1 << 20);

    /// <summary>Gets how many walks resumed from a memoized state.</summary>
    public int Resumed { get; private set; }
    /// <summary>Gets how many walks restarted from the first symbol.</summary>
    public int Restarted { get; private set; }
    /// <summary>Gets how many symbols every walk has read in total.</summary>
    public long Steps { get; private set; }

    /// <summary>Drops every memoized walk.</summary>
    public void Clear() => m_entries.Clear();
    /// <summary>Walks a word through a pattern, resuming from the memoized state when the row proves its prefix
    /// unchanged, and memoizes where the walk ended.</summary>
    /// <param name="pattern">The compiled pattern.</param>
    /// <param name="word">The word an arena read, raw in the pattern's kind. It names its own arena, layout, and
    /// counters; a word no read minted walks from the first symbol and memoizes nothing.</param>
    /// <returns>The walk's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    public PatternWalk Walk(CompiledPattern pattern, ArenaWord word) {
        ArgumentNullException.ThrowIfNull(argument: pattern);

        var memoizes = (word.Arena is not null);

        if (
            memoizes &&
            (!ReferenceEquals(
                objA: m_arena,
                objB: word.Arena
            ) ||
            !ReferenceEquals(
                objA: m_layout,
                objB: word.Layout
            ))
        ) {
            m_arena = word.Arena;
            m_layout = word.Layout;

            m_entries.Clear();
        }

        var source = word.Source;
        var key = new Key(
            Pattern: pattern,
            Source: source
        );
        var letters = word.Letters;
        var proof = word.Proof;
        var attributeProof = word.AttributeProof;
        var consumed = 0;
        var longest = (pattern.Accepts(state: 0)
            ? 0L
            : -1L
        );
        var resumed = false;
        var state = 0;

        if (
            memoizes &&
            m_entries.TryGetValue(
            key: key,
            value: out var entry
        ) &&
            (entry.AttributeProof == attributeProof) &&
            (entry.Proof == proof) &&
            (entry.Length <= letters.Length) &&
            (entry.Length > 0)
        ) {
            consumed = entry.Length;
            longest = entry.LongestAcceptedPrefix;
            resumed = true;
            state = entry.State;
        }

        var steps = (letters.Length - consumed);

        for (var index = consumed; (index < letters.Length); index++) {
            state = pattern.Step(
                letter: pattern.LetterOf(value: letters[index]),
                state: state
            );

            if (pattern.Accepts(state: state)) {
                longest = (index + 1);
            }
        }

        if (memoizes) {
            if (m_entries.Count >= (MaxBytes / EntryBytes)) {
                m_entries.Clear();
            }

            m_entries[key] = new Entry(
                AttributeProof: attributeProof,
                Length: letters.Length,
                LongestAcceptedPrefix: longest,
                Proof: proof,
                State: state
            );
        }

        Steps += steps;

        if (resumed) {
            Resumed++;
        } else {
            Restarted++;
        }

        return new PatternWalk(
            LongestAcceptedPrefix: longest,
            Match: (pattern.Accepts(state: state)
                ? 1L
                : 0L
            ),
            Resumed: resumed,
            State: state,
            Steps: steps
        );
    }

    private readonly record struct Key(CompiledPattern Pattern, WordSource Source);
    private readonly record struct Entry(ulong Proof, ulong AttributeProof, int Length, int State, long LongestAcceptedPrefix);
}
