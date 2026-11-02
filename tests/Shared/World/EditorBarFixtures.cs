using Puck.Commands;

namespace Puck.World.Testing;

/// <summary>The smallest editor bar a document validates: one bank over a resting page. A world's <c>editor.bar</c>
/// must declare at least one bank, and each bank must name a page the composed binding overlays declare, so a fixture
/// that authors an editor section carries both the bar and the overlay that declares its page.</summary>
internal static class EditorBarFixtures {
    /// <summary>The resting page the bar's bank names and the editor's build toggle composes beneath.</summary>
    internal static WorldBindingOverlay RestingPage { get; } = new(
        Document: new BindingProfileDocument(
            Chords: [new BindingChordDefinition(
                Group: "editorFixture",
                Page: new BindingPageDefinition(Entries: [], Id: "base")
            )],
            Modifiers: [],
            Version: BindingProfileDocument.CurrentVersion
        ),
        Id: "editor-fixture"
    );
    /// <summary>The bar: one bank over <see cref="RestingPage"/>.</summary>
    internal static WorldBindingBarAuthoring Bar { get; } = new(
        Banks: [new WorldBindingBarBank(Alpha: 1f, Id: "resting", PageId: "base")],
        SlotSet: ["keyboard.f2"]
    );

    /// <summary>Returns <paramref name="definition"/> with <see cref="RestingPage"/> added to its binding overlays.</summary>
    /// <param name="definition">The document that authors an editor section over <see cref="Bar"/>.</param>
    internal static WorldDefinition WithRestingPage(this WorldDefinition definition) => (definition with {
        BindingOverlaysRaw = [.. definition.BindingOverlays, RestingPage],
    });
}
