using System.Runtime.CompilerServices;

using Puck.Commands;

namespace Puck.World.Tests;

/// <summary>
/// This project's OWN wiring of <c>Puck.World.Data</c>'s two composition-root injection seams
/// (<see cref="BindingVocabularyHook"/>, <see cref="WorldExtensionVocabularyHook"/>) — the same shape
/// <c>src/Puck.World/WorldDataHookInstaller.cs</c> installs for the real engine, minimal here because this suite
/// never boots a machine or resolves a live command registry: <see cref="WorldDefinitionValidator"/> still calls
/// both unconditionally while validating the SHAPE of a document's binding overlays and screen rows, so a
/// document-model law suite that loads no composition root must still wire something, or every
/// <see cref="WorldDefinitionSerialization.Deserialize"/> call refuses before any law under test ever runs.
/// <see cref="BindingVocabularyHook.VocabularyCheck"/> is left unset — its own contract is absent-tolerant (a
/// command-registry lint that no-ops until a composition root exists); nothing here needs it. This is the ONE
/// legitimate reason this project reads <c>Puck.Commands</c> types despite not depending on presentation.
/// </summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        BindingVocabularyHook.DefaultDocument = BuildMinimalBindingDocument;
        // Permissive rather than a real registry: this suite exercises the DOCUMENT/AUTHORITY substrate, never
        // machine boot, so every engine id a shipped world names is accepted rather than checked against
        // Puck.HumbleGamingBrick/Puck.AdvancedGamingBrick's real registrations (out of scope; see README.md).
        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = static _ => true;
    }

    // The smallest document BindingProfile.Compile accepts: one chord row, the empty (resting) chord, carrying a
    // page with no entries — satisfies both of Compile's uniqueness rules trivially (one row can neither duplicate
    // a (group, chord) key nor leave its own group without a resting page) and needs no modifiers, since the
    // empty chord names none. Derived directly from BindingProfile.Compile's own rules (src/Puck.Commands), not
    // from the real engine defaults src/Puck.World/WorldDefaultBindings.cs builds (internal to the composition
    // root, out of reach here, and irrelevant — this suite validates document SHAPE, never a real control scheme).
    // The page id is deliberately NOT "base" — ValidateBindingOverlays composes this engine-default layer
    // ALONGSIDE every overlay the document under validation carries, and page ids are unique ACROSS the whole
    // composed set; every shipped world's own overlays use "base" (this project's own code-built fixture,
    // Fixtures.BuildDocument, authors none today), so this stand-in needs a name no real content is plausibly
    // authoring.
    private static BindingProfileDocument BuildMinimalBindingDocument() => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [],
        Chords: [
            new BindingChordDefinition(Group: "main", Chord: [], Page: new BindingPageDefinition(Id: "puck-world-tests-resting-page", Entries: [])),
        ]
    );
}
