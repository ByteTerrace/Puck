using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The operator's live shape isolation (<c>view.isolate</c>): which shapes of one prototype render, wherever that
/// prototype renders in the boot world's scene (its body stamps, its animated, attached and static placements). A kept
/// shape is one a selection names, or one parented beneath a kept shape, transitively; every other shape of that
/// prototype emits nothing into the program, so it bakes into no mesh and casts no shadow, while its transform slot
/// still packs, so a hidden parent still poses and animates the shapes beneath it.
/// </summary>
/// <remarks>Presentation and session only: no document field, nothing the simulation reads, nothing a replay hashes.
/// The selection is swapped whole and <see cref="Revision"/> moves after it, so the scene emitter that watches the
/// revision rebuilds against the selection it then reads. A changed program changes the bake groups' geometry keys
/// (<c>SdfMeshGeometryBuilder</c> hashes the emitted instructions), so an isolated group never takes the whole body's
/// mesh from the library or the store, nor the reverse.</remarks>
public sealed class WorldShapeIsolation {
    /// <summary>The suffix that makes a shape name a prefix pattern (<c>thigh-*</c>).</summary>
    public const char Wildcard = '*';

    private readonly Puck.Abstractions.CodeDerivedTable<CreationDocument, Resolved> m_resolved = new();
    private Selection? m_selection;
    private int m_revision;

    /// <summary>One isolation: the prototype it applies to and the shape names, or prefix patterns, it keeps.</summary>
    /// <param name="Prototype">The prototype id.</param>
    /// <param name="Names">The kept shape names, each an exact name or a prefix ending in <see cref="Wildcard"/>.</param>
    public sealed record Selection(string Prototype, IReadOnlyList<string> Names);

    // One document's mask under one selection, recomputed when the selection is replaced.
    private sealed record Resolved(Selection Selection, bool[] Hidden);

    /// <summary>Gets the live selection, or <see langword="null"/> when every shape renders.</summary>
    public Selection? Current => Volatile.Read(location: ref m_selection);
    /// <summary>Gets a counter moved by every <see cref="Set"/>, which the scene emitter's rebuild watch reads.</summary>
    public int Revision => Volatile.Read(location: ref m_revision);

    /// <summary>Replaces the live selection.</summary>
    /// <param name="selection">The new selection, or <see langword="null"/> to render every shape.</param>
    public void Set(Selection? selection) {
        Volatile.Write(location: ref m_selection, value: selection);
        _ = Interlocked.Increment(location: ref m_revision);
    }
    /// <summary>Gets the shapes of one prototype's document the live selection hides, by shape index, or
    /// <see langword="null"/> when the selection does not apply to that prototype.</summary>
    /// <param name="prototype">The prototype id.</param>
    /// <param name="document">The prototype's engine document.</param>
    /// <returns>The hidden mask, shared across calls under one selection and never written by the caller, or
    /// <see langword="null"/> when no selection names <paramref name="prototype"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public bool[]? HiddenFor(string prototype, CreationDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        if ((Current is not { } selection) || !string.Equals(a: selection.Prototype, b: prototype, comparisonType: StringComparison.Ordinal)) {
            return null;
        }
        if (m_resolved.TryGetValue(key: document, value: out var cached) && ReferenceEquals(objA: cached.Selection, objB: selection)) {
            return cached.Hidden;
        }

        var hidden = Resolve(names: selection.Names, shapes: (document.Shapes ?? []), matched: null);

        m_resolved.AddOrUpdate(key: document, value: new Resolved(Selection: selection, Hidden: hidden));

        return hidden;
    }
    /// <summary>Returns whether a shape name answers a selection name: equal to it, or, for a name ending in
    /// <see cref="Wildcard"/>, starting with what precedes the wildcard.</summary>
    /// <param name="pattern">The selection name.</param>
    /// <param name="name">The shape's name, or <see langword="null"/> for an unnamed shape, which nothing matches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    public static bool Matches(string pattern, string? name) {
        ArgumentNullException.ThrowIfNull(argument: pattern);

        if (name is null) {
            return false;
        }

        return (pattern.EndsWith(value: Wildcard)
            ? name.StartsWith(value: pattern[..^1], comparisonType: StringComparison.Ordinal)
            : string.Equals(a: pattern, b: name, comparisonType: StringComparison.Ordinal));
    }
    /// <summary>Resolves which shapes a selection hides: a shape is kept when a name matches it or its parent is kept,
    /// and hidden otherwise. A parent precedes its children (the canonicalizer refuses one that does not), so one
    /// forward pass reaches every descendant; a parent no earlier shape names leaves its child at the root.</summary>
    /// <param name="shapes">The document's shapes, in declaration order.</param>
    /// <param name="names">The selection's names and prefix patterns.</param>
    /// <param name="matched">Receives, per name, whether it matched any shape; <see langword="null"/> to skip.</param>
    /// <returns>The hidden mask, one entry per shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> or <paramref name="names"/> is
    /// <see langword="null"/>.</exception>
    public static bool[] Resolve(IReadOnlyList<ShapeDocument> shapes, IReadOnlyList<string> names, bool[]? matched) {
        ArgumentNullException.ThrowIfNull(argument: shapes);
        ArgumentNullException.ThrowIfNull(argument: names);

        var hidden = new bool[shapes.Count];
        var keptNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var name = shape.Name?.Value;
            var kept = ((shape.Parent is { } parent) && keptNames.Contains(item: parent));

            for (var pattern = 0; (pattern < names.Count); pattern++) {
                if (Matches(name: name, pattern: names[pattern])) {
                    kept = true;

                    if (matched is not null) {
                        matched[pattern] = true;
                    }
                }
            }

            hidden[index] = !kept;

            if (kept && (name is not null)) {
                _ = keptNames.Add(item: name);
            }
        }

        return hidden;
    }
    /// <summary>Admits one authored volume of a creation for emission, the walk every emitter of volumes shares: a
    /// disabled volume, and one parented to a shape the mask hides, emit nothing. Resolves the shape a volume parents
    /// to, which a caller composes its own frame from: a static emitter bakes the shape's rest pose, an animated one
    /// rides the shape's transform slot.</summary>
    /// <param name="creation">The creation whose <see cref="CreationDocument.Volumes"/> holds the volume.</param>
    /// <param name="position">The index of the volume in <see cref="CreationDocument.Volumes"/>.</param>
    /// <param name="hidden">The hidden mask over the creation's shapes, or <see langword="null"/> for none.</param>
    /// <param name="emitted">The number of volumes the caller's frame already holds.</param>
    /// <param name="volume">The volume at <paramref name="position"/>.</param>
    /// <param name="parentIndex">The index in <see cref="CreationDocument.Shapes"/> of the first shape the volume names as
    /// its parent, or -1 when it parents to the creation root or names a shape none carries.</param>
    /// <returns><see langword="true"/> when the volume is to be emitted.</returns>
    /// <exception cref="InvalidOperationException">The volume would exceed <see cref="SdfProgramBuilder.MaxVolumes"/>.</exception>
    public static bool TryAdmitVolume(CreationDocument creation, int position, bool[]? hidden, int emitted, out VolumeDocument volume, out int parentIndex) {
        ArgumentNullException.ThrowIfNull(argument: creation);

        volume = creation.Volumes![position];
        parentIndex = -1;

        if (!volume.Enabled) {
            return false;
        }

        if (volume.Parent is { } parent) {
            var shapes = (creation.Shapes ?? []);

            for (var index = 0; (index < shapes.Count); index++) {
                if (string.Equals(a: shapes[index].Name?.Value, b: parent, comparisonType: StringComparison.Ordinal)) {
                    parentIndex = index;

                    break;
                }
            }

            if ((hidden is not null) && (parentIndex >= 0) && (parentIndex < hidden.Length) && hidden[parentIndex]) {
                return false;
            }
        }

        if (emitted >= SdfProgramBuilder.MaxVolumes) {
            throw new InvalidOperationException(message: $"Creation '{creation.Name}' exceeds the {SdfProgramBuilder.MaxVolumes} bounded-volume frame capacity.");
        }

        return true;
    }
}
