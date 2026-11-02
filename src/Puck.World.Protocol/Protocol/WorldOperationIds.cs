using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>
/// Where an ingress mints the operation id of a submission its caller named none for (a console verb's mutation):
/// a fresh random id for an ordinary run, so a restarted world never repeats an id it already answered, or ids derived
/// from a seed and the submitting principal's own count, so two runs driven by the same inputs submit identical
/// envelopes. The noninterference harness seeds its worlds' links so the base and control of a pair agree on every
/// operation id they show their principals.
/// </summary>
/// <remarks>Safe from any thread.</remarks>
public sealed class WorldOperationIds {
    private readonly Lock m_gate = new();
    private readonly Dictionary<string, long> m_counts = new(comparer: StringComparer.Ordinal);
    private readonly ulong? m_seed;

    private WorldOperationIds(ulong? seed) => m_seed = seed;

    /// <summary>Gets the source that mints a fresh random id for every submission.</summary>
    public static WorldOperationIds Random { get; } = new(seed: null);

    /// <summary>Creates a source that derives each id from <paramref name="seed"/>, the principal and how many ids that
    /// principal has been minted so far.</summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The source.</returns>
    public static WorldOperationIds Seeded(ulong seed) => new(seed: seed);

    /// <summary>Mints the next operation id for a submission <paramref name="principal"/> makes.</summary>
    /// <param name="principal">The acting principal.</param>
    /// <returns>The id; never <see cref="Guid.Empty"/>.</returns>
    public Guid Next(Principal principal) {
        if (m_seed is not { } seed) { return Guid.NewGuid(); }
        var name = principal.Describe();
        long count;

        lock (m_gate) {
            count = (m_counts.TryGetValue(key: name, value: out var previous) ? (previous + 1L) : 1L);
            m_counts[name] = count;
        }
        var nameBytes = Encoding.UTF8.GetBytes(s: name);
        var material = new byte[(16 + nameBytes.Length)];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: material, value: seed);
        BinaryPrimitives.WriteInt64LittleEndian(destination: material.AsSpan(start: 8), value: count);
        nameBytes.CopyTo(array: material, index: 16);
        return new Guid(b: SHA256.HashData(source: material).AsSpan(start: 0, length: 16));
    }
}
