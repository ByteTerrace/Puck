using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Puck.State;

/// <summary>A consumed private word, addressed independently of its key.</summary>
public readonly record struct PrivateDrawWord(long Installation, ulong Seed, ulong Stream, long Cursor, uint Value);

/// <summary>Derives a private sampler for an installation and substitutes recorded words during replay.</summary>
public class PrivateDrawAuthority {
    private readonly byte[] m_seed;
    private IReadOnlyDictionary<(long, ulong, ulong, long), uint>? m_replay;

    /// <summary>Opens the authority over a private copy of its seed.</summary>
    public PrivateDrawAuthority(ReadOnlySpan<byte> seed) {
        if (seed.Length != 32) { throw new ArgumentException("A draw seed requires 32 bytes.", nameof(seed)); }
        m_seed = seed.ToArray();
    }

    /// <summary>Copies the authority seed for private persistence.</summary>
    protected byte[] CopySeed() => (byte[])m_seed.Clone();

    /// <summary>Whether the stream was issued by this sampler.</summary>
    public bool Owns(PrivateDrawStream? stream) => ReferenceEquals(stream?.Owner, this);

    /// <summary>Observes a sampled word while recording.</summary>
    public Action<PrivateDrawWord>? WordRead { get; set; }

    /// <summary>Selects recorded words for the next replay tick.</summary>
    public void Replay(IReadOnlyList<PrivateDrawWord>? words) {
        if (words is null) { m_replay = null; return; }
        if (words.Count == 0) { m_replay = System.Collections.Frozen.FrozenDictionary<(long, ulong, ulong, long), uint>.Empty; return; }
        var samples = new Dictionary<(long, ulong, ulong, long), uint>(words.Count);
        foreach (var word in words) {
            var address = (word.Installation, word.Seed, word.Stream, word.Cursor);
            if (!samples.TryAdd(address, word.Value) && samples[address] != word.Value) {
                throw new InvalidDataException("The recording contains conflicting values at one private draw address.");
            }
        }
        m_replay = samples;
    }

    private uint ReadWord(ClosedBitset256 key, long installation, ulong seed, ulong stream, long cursor) {
        var word = m_replay is null ? PrivateDrawStream.Word(key, seed, stream, cursor)
            : m_replay.TryGetValue((installation, seed, stream, cursor), out var recorded) ? recorded
            : throw new InvalidDataException("The recording lacks a private draw at the requested address.");
        WordRead?.Invoke(new(installation, seed, stream, cursor, word));
        return word;
    }

    /// <summary>Binds an executable stream to a private declaration at its allocated ordinal.</summary>
    public Draw InstallStream(Draw draw, long installation) {
        if (!draw.PrivateStream || installation <= 0) { throw new ArgumentException("A private stream requires a private declaration and a positive installation ordinal."); }
        Span<byte> address = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(address, installation);
        var digest = HMACSHA256.HashData(m_seed, address);
        var key = new ClosedBitset256(BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8)), BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(24)));
        return draw with { Secret = new((seed, stream, cursor) => ReadWord(key, installation, seed, stream, cursor), installation, this) };
    }
}
