using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Puck.State;

/// <summary>A consumed private word, addressed independently of its key.</summary>
public readonly record struct PrivateDrawWord(long Installation, ulong Seed, ulong Stream, long Cursor, uint Value);

/// <summary>Derives a private sampler for an installation and substitutes recorded words during replay.</summary>
public abstract class PrivateDrawAuthority : IJournalParticipant {
    private readonly byte[] m_seed;
    /// <summary>Opens the authority over a private copy of its seed.</summary>
    protected PrivateDrawAuthority(ReadOnlySpan<byte> seed) {
        if (seed.Length != 32) { throw new ArgumentException("A draw seed requires 32 bytes.", nameof(seed)); }
        m_seed = seed.ToArray();
    }

    /// <summary>Whether the stream was issued by this sampler.</summary>
    public bool Owns(PrivateDrawStream? stream) => ReferenceEquals(stream?.Owner, this);

    /// <summary>The active timeline. Installed streams consult it even if their rows survived a rewind.</summary>
    public long Generation { get; private set; }
    /// <summary>Starts a new timeline for every installed site.</summary>
    protected void AdvanceGeneration() => Generation = checked(Generation + 1);

    /// <summary>Number of words evaluated, including reads in a refused firing.</summary>
    public long Reads { get; private set; }

    /// <summary>Any private reads undone with this transaction retire their timeline.</summary>
    protected void TrackRewind(ArenaJournal journal) => journal.RecordParticipant(this, [Reads]);

    void IJournalParticipant.Undo(ReadOnlySpan<long> words) {
        if (Reads != words[0]) { AdvanceGeneration(); }
    }

    private readonly Dictionary<long, Func<long, ulong, ulong, long, uint?>> m_scopedReplay = [];
    private Action<PrivateDrawWord>? m_observer;
    private Func<long, ulong, ulong, long, uint?>? m_recorded;
    private sealed class Scope(Action end) : IDisposable {
        private Action? m_end = end;
        public void Dispose() => Interlocked.Exchange(ref m_end, null)?.Invoke();
    }

    /// <summary>Observes the words an authority reads during capture.</summary>
    protected IDisposable Capture(Action<PrivateDrawWord> observe) {
        m_observer += observe;
        return new Scope(() => m_observer -= observe);
    }

    /// <summary>Substitutes the words actually consumed at each address for one replay tick.</summary>
    protected IDisposable Replay(IEnumerable<PrivateDrawWord> words, Func<string, Exception?> missing, bool historical = false, IReadOnlySet<long>? sites = null) {
        var prior = m_recorded;
        var observing = m_observer;
        var samples = words.GroupBy(w => (w.Installation, w.Seed, w.Stream, w.Cursor))
            .ToDictionary(g => g.Key, g => new Queue<uint>(g.Select(w => w.Value)));
        uint? Read(long installation, ulong seed, ulong stream, long cursor) {
            if (samples.TryGetValue((installation, seed, stream, cursor), out var values) && values.TryDequeue(out var word)) { return word; }
            var error = missing($"installation {installation}, stream {stream}, seed {seed}, cursor {cursor}");
            if (error is not null) { throw error; }
            return null;
        }
        if (sites is not null) {
            var installed = sites.ToArray();
            if (installed.Any(m_scopedReplay.ContainsKey)) { throw new InvalidOperationException("Overlapping private draw replay sites."); }
            foreach (var site in installed) { m_scopedReplay.Add(site, Read); }
            return new Scope(() => { foreach (var site in installed) { m_scopedReplay.Remove(site); } });
        }
        m_recorded = Read;
        if (historical) { m_observer = null; }
        return new Scope(() => {
            m_recorded = prior;
            if (historical) { m_observer = observing; }
        });
    }

    /// <summary>Whether this sampler serves only public, deterministic preview words.</summary>
    protected internal virtual bool PreviewWords => false;

    /// <summary>Whether this sampler can bind a declaration.</summary>
    protected virtual bool CanInstall => true;
    /// <summary>Replay-only samplers require a recorded address for every read.</summary>
    protected virtual bool RecordedOnly => false;

    /// <summary>Opens an isolated sampler under a caller-owned key.</summary>
    protected static PrivateDrawSample FromKey(ClosedBitset256 key) => PrivateDrawStream.Imagined(key: key);


    /// <summary>Binds a private declaration at the allocated ordinal.</summary>
    protected Draw InstallStream(Draw draw, long installation) {
        if (!CanInstall) { throw new InvalidDataException("PrivateDrawSeedMissing: a private draw requires an authority seed."); }
        if (!draw.PrivateStream || installation <= 0) { throw new ArgumentException("Invalid private draw installation."); }
        long keyGeneration = -1;
        ClosedBitset256 key = default;
        uint Sample(ulong seed, ulong stream, long cursor) {
            if (keyGeneration != Generation) {
                Span<byte> address = stackalloc byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(address, Generation);
                BinaryPrimitives.WriteInt64LittleEndian(address[8..], installation);
                var digest = HMACSHA256.HashData(m_seed, address);
                key = new(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(16)), BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(24)));
                keyGeneration = Generation;
            }
            var replay = m_recorded ?? m_scopedReplay.GetValueOrDefault(installation);
            var word = replay?.Invoke(installation, seed, stream, cursor) ??
                (RecordedOnly ? throw new InvalidDataException("MissingPrivateDraw: no recording is active.") :
                PrivateDrawStream.Word(key, seed, stream, cursor));
            Reads++;
            m_observer?.Invoke(new(installation, seed, stream, cursor, word));
            return word;
        }
        return draw with { Secret = new PrivateDrawStream(Sample, installation, this) };
    }
}
