using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEditorSeats {
    /// <summary>Reads one seat's inspection of a named world, retaining it across activations and pruning names removed
    /// by a new document. Reading an unchanged document allocates nothing.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="world">The stable world instance identity, never its activation.</param>
    /// <param name="definition">The world's currently admitted document.</param>
    /// <returns>The immutable selection captured by a view.</returns>
    public SdfSkyInspection SkyOf(int slot, string world, WorldDefinition definition) {
        ArgumentException.ThrowIfNullOrEmpty(world);
        ArgumentNullException.ThrowIfNull(definition);
        if (!At(slot: slot).Skies.TryGetValue(key: world, value: out var entry)) { return SdfSkyInspection.None; }
        if (ReferenceEquals(objA: entry.Definition, objB: definition)) { return entry.Selection; }
        var selection = entry.Selection;

        if ((selection.Solo is { } solo) && !HasSkyLayer(definition: definition, name: solo)) {
            selection = selection.WithSolo(layer: null);
            ReportSkyRemoval(layer: solo, slot: slot, world: world);
        }
        foreach (var muted in entry.Selection.Muted) {
            if (!HasSkyLayer(definition: definition, name: muted)) {
                selection = selection.WithMute(layer: muted, muted: false);
                if (muted != entry.Selection.Solo) { ReportSkyRemoval(layer: muted, slot: slot, world: world); }
            }
        }
        entry.Definition = definition;
        if (!ReferenceEquals(objA: entry.Selection, objB: selection)) { entry.Selection = selection; Moved(); }
        return selection;
    }
    /// <summary>Sets the solo for one seat and world. Clearing it restores that world's retained mutes.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="world">The stable world instance identity.</param>
    /// <param name="definition">The current document, used to refuse unknown names.</param>
    /// <param name="layer">The soloed layer, or null to clear the solo.</param>
    /// <exception cref="ArgumentException">The document has no named layer.</exception>
    public void SetSkySolo(int slot, string world, WorldDefinition definition, string? layer) {
        if ((layer is not null) && !HasSkyLayer(definition: definition, name: layer)) { throw new ArgumentException(message: (("The world has no sky layer '" + layer) + "'."), paramName: nameof(layer)); }
        SetSky(slot, world, definition, SkyOf(definition: definition, slot: slot, world: world).WithSolo(layer: layer));
    }
    /// <summary>Sets a mute for one seat and world without changing its solo.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="world">The stable world instance identity.</param>
    /// <param name="definition">The current document, used to refuse unknown names.</param>
    /// <param name="layer">The authored layer name.</param>
    /// <param name="muted">Whether the layer is muted when no solo is active.</param>
    /// <exception cref="ArgumentException">The document has no named layer.</exception>
    public void SetSkyMute(int slot, string world, WorldDefinition definition, string layer, bool muted) {
        if (!HasSkyLayer(definition: definition, name: layer)) { throw new ArgumentException(message: (("The world has no sky layer '" + layer) + "'."), paramName: nameof(layer)); }
        SetSky(slot, world, definition, SkyOf(definition: definition, slot: slot, world: world).WithMute(layer: layer, muted: muted));
    }
    /// <summary>Enumerates every world's retained selection for a seat, in ordinal world order, for command echoes.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <returns>The stable world identities and their immutable selections.</returns>
    public IEnumerable<KeyValuePair<string, SdfSkyInspection>> SkySelections(int slot) {
        foreach (var pair in At(slot: slot).Skies.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
            if (!ReferenceEquals(objA: pair.Value.Selection, objB: SdfSkyInspection.None)) { yield return new(key: pair.Key, value: pair.Value.Selection); }
        }
    }

    /// <summary>Gets or sets the sink for named removal reports, or null to write to standard error.</summary>
    public Action<string>? SkyReport { get; set; }

    private static bool HasSkyLayer(WorldDefinition definition, string name) {
        if (definition.Render.Sky?.Layers is not { } layers) { return false; }
        for (var index = 0; (index < layers.Count); index++) { if (layers[index].Name == name) { return true; } }
        return false;
    }
    private void ReportSkyRemoval(int slot, string world, string layer) {
        var message = (((((("[world.sky: seat " + PlayerRoster.DisplayNumber(slot: slot)) + " world '") + world) + "' removed layer '") + layer) + "'; inspection selection dropped]");

        if (SkyReport is { } report) { report(message); } else { Console.Error.WriteLine(value: message); }
    }
    private void SetSky(int slot, string world, WorldDefinition definition, SdfSkyInspection selection) {
        var states = At(slot: slot).Skies;

        if (states.TryGetValue(key: world, value: out var entry)) {
            if (ReferenceEquals(objA: entry.Selection, objB: selection)) { return; }
            entry.Selection = selection;
            entry.Definition = definition;
        } else {
            if (ReferenceEquals(objA: selection, objB: SdfSkyInspection.None)) { return; }
            states.Add(key: world, value: new(definition: definition, selection: selection));
        }
        Moved();
    }

    private sealed class SkyEntry(WorldDefinition definition, SdfSkyInspection selection) {
        public WorldDefinition Definition = definition;
        public SdfSkyInspection Selection = selection;
    }
    private sealed partial class Seat {
        public Dictionary<string, SkyEntry> Skies { get; } = new(comparer: StringComparer.Ordinal);
    }
}
