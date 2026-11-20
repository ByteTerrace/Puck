using System.Security.Cryptography;
using Puck.Abstractions.Memory;
using Puck.Assets;
using Puck.World.Authoring;

namespace Puck.World;

public sealed partial class WorldDisclosureImages {
    // One audience's scene identity (SceneIdentity), kept while nothing it was computed from can have moved: the same
    // source document, arena, policy revision and move sequence, and the same frame identity.
    private sealed class SceneEntry {
        public WorldDefinition? Source;
        public StateArena? Arena;
        public long Revision = -1L;
        public long Moves = -1L;
        public ContentPin Frame;
        public ContentPin Identity;
        public int[] Rows = [];
        public object? RowsOf;
    }
    private readonly Dictionary<string, SceneEntry> m_scenes = new(comparer: StringComparer.Ordinal);
    // The rows a source document's scene reads by name (SceneInputRows), resolved once per source.
    private WorldDefinition? m_sceneRowsSource;
    private StateArena? m_sceneRowsArena;
    private int[] m_sceneRows = [];
    private const byte WholeMarker = 1;

    // The content identity of everything a scene drawn from this view's stream reads: the disclosed document less its
    // state rows (FrameIdentity: placements as this audience is dealt them, every other section, the field lattices),
    // and for each row the scene reads (SceneInputRows, and the rows the placements read) whether the view reads it
    // whole and, when it does not, the image it is delivered. Two views with equal identities at one delivery are
    // handed the same scene inputs at that delivery: a row both read whole carries the same cells, a row either does
    // not is compared by its image, and every filter a replica snapshot applies to the scene's channels (the field
    // cells by whole-row reading, the solid sizes by cell disclosure) answers alike for equal answers. Computed once
    // per audience while nothing it reads has moved, so the sessions of one audience share it.
    internal ContentPin SceneIdentity(WorldRecipientView view) {
        var audience = Audience(view);
        var frame = FrameIdentity(view);
        var arena = view.Arena;
        var source = view.Source;
        if (m_scenes.TryGetValue(audience, out var held) && ReferenceEquals(held.Source, source) && ReferenceEquals(held.Arena, arena) &&
            (held.Revision == arena.DisclosureRevision) && (held.Moves == arena.MoveSequence) && (held.Frame == frame)) {
            return held.Identity;
        }
        var entry = held ?? (m_scenes[audience] = new SceneEntry());
        var definition = m_definitions[audience];
        // The rows follow the source and the placements' reads, which only a recomposed definition changes.
        if (!ReferenceEquals(entry.RowsOf, definition) || !ReferenceEquals(entry.Source, source)) { entry.Rows = SceneRows(view, definition); entry.RowsOf = definition; }
        // Each row takes a marker byte and a digest slot, left zero for a row read whole.
        var length = (SHA256.HashSizeInBytes * (1 + entry.Rows.Length)) + entry.Rows.Length;
        ArrayCapacity.EnsureScratch(ref m_sceneParts, length);
        var parts = m_sceneParts.AsSpan(0, length);
        parts.Clear();
        var at = 0;
        definition.Digests.AsSpan(0, SHA256.HashSizeInBytes).CopyTo(parts[at..]); at += SHA256.HashSizeInBytes;
        foreach (var row in entry.Rows) {
            if (view.ReadsWhole(row)) { parts[at] = WholeMarker; at += 1 + SHA256.HashSizeInBytes; continue; }
            at += 1;
            Image(view, row, audience).Digest.CopyTo(parts[at..]); at += SHA256.HashSizeInBytes;
        }
        entry.Source = source; entry.Arena = arena; entry.Revision = arena.DisclosureRevision; entry.Moves = arena.MoveSequence;
        entry.Frame = frame;
        entry.Identity = ContentPin.Compute(parts);
        return entry.Identity;
    }
    // SceneIdentity's scratch, reused across audiences and ticks.
    private byte[] m_sceneParts = [];
    // The first row the scene reads that this view does not read whole, by name, or null when it reads every one whole;
    // for an operator's read-back of why a view's scene differs from a whole reader's.
    internal string? FirstSceneRowWithheld(WorldRecipientView view) {
        var audience = Audience(view);
        _ = FrameIdentity(view);
        foreach (var row in SceneRows(view, m_definitions[audience])) {
            if (!view.ReadsWhole(row)) { return view.Arena.Rows[row].Name.Value; }
        }
        return null;
    }
    // The authored rows a scene reads, in row order: the source's scene input rows and the rows its placements read as
    // this audience is dealt them.
    private int[] SceneRows(WorldRecipientView view, AudienceDefinition definition) {
        var named = SceneInputRows(view);
        if (definition.FrameRowOrdinals.Length == 0) { return named; }
        var rows = new SortedSet<int>(named);
        rows.UnionWith(definition.FrameRowOrdinals);
        return [.. rows];
    }
    private int[] SceneInputRows(WorldRecipientView view) {
        var source = view.Source;
        var arena = view.Arena;
        if (ReferenceEquals(m_sceneRowsSource, source) && ReferenceEquals(m_sceneRowsArena, arena)) { return m_sceneRows; }
        var authored = source.AuthoredState.Count;
        var rows = new SortedSet<int>();
        foreach (var name in WorldSceneInputs.Rows(source)) {
            if (arena.Catalog.TryResolve(StateLane.Document, name, out var handle) && (handle.Ordinal < authored)) { rows.Add(handle.Ordinal); }
        }
        m_sceneRowsSource = source; m_sceneRowsArena = arena;
        return m_sceneRows = [.. rows];
    }
}

/// <summary>The state rows a scene drawn from a world's delivered document reads by name, beside the rows its placements
/// read (which the disclosure records as it deals them): every bindable and text-screen placeholder the document binds to
/// a cell (<see cref="WorldKeyedValues.BoundOf"/>), every field region's field rows and travel-resistance row, every
/// state cell a creation's pose reads (<see cref="CreationRows"/>: its drivers' signals and gates, its effectors' gates,
/// targets and the cells that select a target body, its size source), and every state clock's row. Body poses, registers and facts cross in the
/// snapshot's body table, which a stream redacts by its world's observer disclosure, not by a row. A seat's input
/// publications are no scene input: only a seat's own client reads them (<c>WorldClient</c>, <c>WorldSeatBindings</c>),
/// never a scene emitter.</summary>
/// <remarks>This list is held by review: a new way for a scene to read a state row owes a term here,
/// since a scene shared between two streams is shared on the identity these rows feed
/// (<see cref="WorldRecipientView.SceneIdentity"/>).</remarks>
public static class WorldSceneInputs {
    /// <summary>Returns the names of the state rows a scene of the document reads by name.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The row names, each once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlySet<string> Rows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rows = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var bound in WorldKeyedValues.BoundOf(definition: definition)) { _ = rows.Add(item: bound.Binding.Row); }
        foreach (var region in definition.FieldRegions) {
            foreach (var field in region.Fields) { _ = rows.Add(item: field.Name); }
            if (region.TravelResistance?.Row is { } resistance) { _ = rows.Add(item: resistance); }
        }
        rows.UnionWith(other: CreationRows(definition: definition));
        foreach (var clock in (definition.Timeline.Clocks ?? [])) {
            if (clock?.State is { } state) { _ = rows.Add(item: state); }
        }

        return rows;
    }
    /// <summary>Returns the names of the state rows the document's creations' poses read: every state read of each
    /// creation's compiled rig (<see cref="CreationRig.Reads"/>), the one list the pose pass reads through and the
    /// presentation manifest registers for delivery.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The row names, each once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlySet<string> CreationRows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rows = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var creation in definition.Creations) {
            // A rig's lowered document compiles no rig; what it still reads is its size source.
            if (creation.Document.Lowered) {
                if (StateBinding.Parse(token: creation.Document.SizeBy) is { } size) { _ = rows.Add(item: size.Row); }
                continue;
            }
            foreach (var read in CreationRig.Of(document: creation.Document).Reads) {
                if ((read.Kind == CreationRigReadKind.State) && (StateBinding.Parse(token: read.Reference) is { } binding)) { _ = rows.Add(item: binding.Row); }
            }
        }

        return rows;
    }
}
