using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Puck.Assets;
using Puck.SignedDistance.Baking;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>One prototype a world's presentation bakes: the row's id, the bake's key, and the creation it bakes.</summary>
/// <param name="PrototypeId">The prototype row's id.</param>
/// <param name="Key">The bake's key.</param>
/// <param name="Document">The creation, in the author frame.</param>
public readonly record struct WorldBakeRequest(string PrototypeId, CreationBakeKey Key, CreationDocument Document);
/// <summary>
/// The one cache of creation bakes, keyed by <see cref="CreationBakeKey.Pin"/> and filled two ways. A compiled world's
/// <c>BAKE</c> chunk (<see cref="WorldBakeChunk"/>) holds, in memory for the process's life, each outcome it names that
/// the build's bake pack (<see cref="WorldBakePack"/>) carries, without copying it; a bake made on the device is kept in
/// memory and written under <see cref="Directory"/> as a <see cref="ContentAddressedStore"/> derived entry of kind
/// <see cref="DerivedKind"/>, so a later boot finds it without baking again. An outcome is either a bake or the refusal of
/// a creation that has none (<see cref="CreationBakeCodec"/>). A store or pack that cannot be read or written costs a bake
/// and nothing else. Every member is safe to call from several threads.
/// </summary>
public sealed class WorldBakeStore {
    /// <summary>The derived-entry kind a bake is recorded under in the content-addressed store.</summary>
    public const string DerivedKind = "creation-bake";

    private static readonly ConcurrentDictionary<string, WorldBakeStore> Opened = new(comparer: StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<WorldPrototype, string> Pins = new();
    private readonly ConcurrentDictionary<ContentPin, ReadOnlyMemory<byte>> m_held = new();
    private readonly ConcurrentDictionary<string, Lazy<WorldBakePack?>> m_packs = new(comparer: StringComparer.Ordinal);

    private readonly Lazy<ContentAddressedStore?> m_disk;

    private long m_baked;
    private long m_fieldEvaluations;
    private long m_refused;

    /// <summary>Initializes a new instance of the <see cref="WorldBakeStore"/> class.</summary>
    /// <param name="directory">The directory device bakes are written under, created on the first write; or
    /// <see langword="null"/> for a store that holds outcomes in memory only.</param>
    public WorldBakeStore(string? directory = null) {
        Directory = ((directory is null) ? null : Path.GetFullPath(path: directory));
        m_disk = new Lazy<ContentAddressedStore?>(valueFactory: () => {
            if (Directory is null) {
                return null;
            }

            try {
                return new ContentAddressedStore(root: Directory);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return null;
            }
        });
    }

    /// <summary>Gets the directory device bakes are written under, or <see langword="null"/> for a memory-only
    /// store.</summary>
    public string? Directory { get; }
    /// <summary>Gets the creations <see cref="GetOrBake"/> baked because the store had no outcome for them.</summary>
    public long Baked => Interlocked.Read(location: ref m_baked);
    /// <summary>Gets the creations <see cref="GetOrBake"/> found have no bake, because the store had no outcome for
    /// them.</summary>
    public long Refused => Interlocked.Read(location: ref m_refused);
    /// <summary>Gets the field evaluations the bakes <see cref="GetOrBake"/> made spent.</summary>
    public long FieldEvaluations => Interlocked.Read(location: ref m_fieldEvaluations);
    /// <summary>Gets the outcomes held in memory.</summary>
    public int HeldCount => m_held.Count;

    /// <summary>Returns the one store over <paramref name="directory"/> in this process, so a boot's compiled world and the
    /// presentation that bakes on a miss fill and read the same cache.</summary>
    /// <param name="directory">The directory device bakes are written under.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty, or white
    /// space.</exception>
    public static WorldBakeStore Open(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        return Opened.GetOrAdd(
            key: Path.GetFullPath(path: directory),
            valueFactory: static full => new WorldBakeStore(directory: full)
        );
    }
    /// <summary>Returns every prototype of <paramref name="definition"/> a presentation bakes at
    /// <paramref name="quality"/>, in the order the definition lists them.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="quality">The quality tier.</param>
    /// <returns>The requests.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldBakeRequest> RequestsOf(WorldDefinition definition, SdfBakeQuality quality) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var requests = new List<WorldBakeRequest>(capacity: definition.Creations.Count);

        foreach (var prototype in definition.Creations) {
            // A row's pin is computed once per row object: rows are replaced, never mutated, when a creation is edited.
            var pin = Pins.GetValue(
                createValueCallback: static row => row.Hash,
                key: prototype
            );

            requests.Add(item: new WorldBakeRequest(
                Document: prototype.Document,
                Key: CreationBakeKey.For(creationPin: pin, quality: quality),
                PrototypeId: prototype.Id.Value
            ));
        }

        return requests;
    }
    /// <summary>Bakes one request and encodes its outcome, a bake or a refusal.</summary>
    /// <param name="request">The request.</param>
    /// <param name="work">The bake's work, or <see langword="default"/> for a refusal.</param>
    /// <returns>The encoded outcome.</returns>
    public static byte[] Bake(WorldBakeRequest request, out SdfBakeWork work) {
        if (!CreationBaker.TryBake(
            bake: out var bake,
            document: request.Document,
            quality: request.Key.Quality,
            reason: out var reason
        )) {
            work = default;
            return CreationBakeCodec.EncodeRefusal(reason: reason);
        }

        work = bake.Work;
        return CreationBakeCodec.Encode(bake: bake);
    }
    /// <summary>Returns a request's outcome, from the store (<see cref="TryGet"/>) when it has one, else baked and held in
    /// memory without writing it anywhere, counted in <see cref="Baked"/> and <see cref="FieldEvaluations"/>: how a
    /// compiled world's derivation gathers the outcomes its build's pack ships.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The encoded outcome.</returns>
    public ReadOnlyMemory<byte> GetOrBake(WorldBakeRequest request) {
        var key = request.Key.Pin;

        if (TryGet(key: key, outcome: out var outcome)) {
            return outcome;
        }

        var baked = Bake(request: request, work: out var work);

        // An outcome opens with 1 for a bake and 0 for a refusal (CreationBakeCodec).
        _ = ((baked[0] == 0)
            ? Interlocked.Increment(location: ref m_refused)
            : Interlocked.Increment(location: ref m_baked));
        _ = Interlocked.Add(location1: ref m_fieldEvaluations, value: work.FieldEvaluations);

        return m_held.GetOrAdd(key: key, value: baked);
    }
    /// <summary>Holds in memory each of <paramref name="keys"/> the pack at <paramref name="packPath"/> carries, without
    /// copying it: how a compiled world's <c>BAKE</c> chunk fills the cache on load. The pack is read once per store and
    /// path; one that is absent or cannot be read carries nothing this time, so its keys are left for the background bake.</summary>
    /// <param name="packPath">The pack's path.</param>
    /// <param name="keys">The key pins to hold.</param>
    /// <returns>The keys held from the pack.</returns>
    /// <exception cref="ArgumentException"><paramref name="packPath"/> is <see langword="null"/>, empty, or white
    /// space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public int HoldFromPack(string packPath, IEnumerable<ContentPin> keys) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: packPath);
        ArgumentNullException.ThrowIfNull(argument: keys);

        var full = Path.GetFullPath(path: packPath);
        var entry = m_packs.GetOrAdd(
            key: full,
            valueFactory: static path => new Lazy<WorldBakePack?>(valueFactory: () => ReadPack(path: path))
        );
        var pack = entry.Value;
        var held = 0;

        // A pack that could not be read is read again by the next load, which may find one a build has since written.
        if (pack is null) {
            _ = m_packs.TryRemove(item: KeyValuePair.Create(key: full, value: entry));
            return held;
        }

        foreach (var key in keys) {
            if (pack.TryGet(key: key, outcome: out var outcome)) {
                m_held[key] = outcome;
                held++;
            }
        }

        return held;
    }

    private static WorldBakePack? ReadPack(string path) {
        try {
            return ((File.Exists(path: path) && WorldBakePack.TryDecode(content: File.ReadAllBytes(path: path), pack: out var pack, reason: out _))
                ? pack
                : null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }

    /// <summary>Holds an outcome in memory and writes it under <see cref="Directory"/>: how a bake made on the device
    /// fills the cache.</summary>
    /// <param name="key">The bake's key pin.</param>
    /// <param name="outcome">The encoded outcome; the store keeps the array, which the caller must not change.</param>
    /// <returns><see langword="true"/> when the outcome was written, or the store is memory-only.</returns>
    public bool Keep(ContentPin key, byte[] outcome) {
        ArgumentNullException.ThrowIfNull(argument: outcome);

        m_held[key] = outcome;

        if (m_disk.Value is not { } disk) {
            return (Directory is null);
        }

        try {
            disk.SetDerived(
                inputHash: key,
                kind: DerivedKind,
                outputHash: disk.Put(content: outcome)
            );
            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
    /// <summary>Finds an outcome held in memory, without reading <see cref="Directory"/>.</summary>
    /// <param name="key">The bake's key pin.</param>
    /// <param name="outcome">The encoded outcome, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the store holds the outcome in memory.</returns>
    public bool TryGetHeld(ContentPin key, out ReadOnlyMemory<byte> outcome) =>
        m_held.TryGetValue(key: key, value: out outcome);
    /// <summary>Finds an outcome: in memory, else under <see cref="Directory"/>, holding what it reads there.</summary>
    /// <param name="key">The bake's key pin.</param>
    /// <param name="outcome">The encoded outcome, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the store has the outcome.</returns>
    public bool TryGet(ContentPin key, out ReadOnlyMemory<byte> outcome) {
        if (m_held.TryGetValue(key: key, value: out outcome)) {
            return true;
        }

        if (m_disk.Value is not { } disk) {
            return false;
        }

        try {
            if (
                disk.TryResolveDerived(hash: out var stored, inputHash: key, kind: DerivedKind) &&
                disk.TryGet(content: out var content, pin: stored)
            ) {
                outcome = content;
                m_held[key] = outcome;
                return true;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            outcome = default;
        }

        return false;
    }
}
