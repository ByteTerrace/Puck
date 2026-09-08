using Xunit;

namespace Puck.Commands.Tests;

/// <summary>
/// Pins the single-page reading of page inheritance to the bulk one: whatever
/// <see cref="BindingProfile.Compile"/> refuses, the walk behind
/// <see cref="BindingSessionPlan.FromPage"/> must refuse in the same words.
/// </summary>
/// <remarks>A tolerant single-page walk is worse than a missing one: it hands a guided session a plan over a
/// document the engine will never run, with steps pulled from a page in another group entirely — so the wizard
/// prompts the player to bind controls that page never presents.</remarks>
public sealed class BindingProfileEffectivePageTests {
    private static BindingProfileDocument CrossGroupInheritance() => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [],
        Chords: [
            new BindingChordDefinition(
                Chord: [],
                Group: "play",
                Page: new BindingPageDefinition(
                    Entries: [new BindingPageEntryDefinition(Command: "play.a", Sources: ["keyboard.a"])],
                    Id: "play-base"
                )
            ),
            new BindingChordDefinition(
                Chord: [],
                Group: "menu",
                Page: new BindingPageDefinition(
                    Entries: [new BindingPageEntryDefinition(Command: "menu.z", Sources: ["keyboard.z"])],
                    Id: "menu-base"
                )
            ),
            new BindingChordDefinition(
                Chord: ["keyboard.leftShift"],
                Group: "play",
                Page: new BindingPageDefinition(
                    Entries: [new BindingPageEntryDefinition(Command: "play.b", Sources: ["keyboard.b"])],
                    Id: "crossed",
                    Inherits: "menu-base"
                )
            ),
        ]
    );

    [Fact]
    public void ACrossGroupInheritsIsRefusedByBothCompileAndFromPage() {
        var document = CrossGroupInheritance();
        var compileRefusal = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: document));
        var planRefusal = Assert.Throws<ArgumentException>(testCode: () => BindingSessionPlan.FromPage(
            document: document,
            pageId: "crossed"
        ));

        Assert.Contains(
            actualString: compileRefusal.Message,
            expectedSubstring: "inherits invalid page \"menu-base\""
        );
        Assert.Contains(
            actualString: planRefusal.Message,
            expectedSubstring: "inherits invalid page \"menu-base\""
        );
    }
    [Fact]
    public void AnEmptyInheritsIsRefusedByBothCompileAndFromPage() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Chord: [],
                    Group: "play",
                    Page: new BindingPageDefinition(
                        Entries: [new BindingPageEntryDefinition(Command: "play.a", Sources: ["keyboard.a"])],
                        Id: "play-base",
                        Inherits: ""
                    )
                ),
            ]
        );
        var compileRefusal = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: document));
        var planRefusal = Assert.Throws<ArgumentException>(testCode: () => BindingSessionPlan.FromPage(
            document: document,
            pageId: "play-base"
        ));

        Assert.Contains(
            actualString: compileRefusal.Message,
            expectedSubstring: "carries an empty inherited page id"
        );
        Assert.Contains(
            actualString: planRefusal.Message,
            expectedSubstring: "carries an empty inherited page id"
        );
    }
    [Fact]
    public void ASameGroupInheritsStillFlattensForFromPage() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Chord: [],
                    Group: "play",
                    Page: new BindingPageDefinition(
                        Entries: [new BindingPageEntryDefinition(Command: "play.a", Sources: ["keyboard.a"])],
                        Id: "play-base"
                    )
                ),
                new BindingChordDefinition(
                    Chord: ["keyboard.leftShift"],
                    Group: "play",
                    Page: new BindingPageDefinition(
                        Entries: [new BindingPageEntryDefinition(Command: "play.b", Sources: ["keyboard.b"])],
                        Id: "modal",
                        Inherits: "play-base"
                    )
                ),
            ]
        );

        // Compile accepts the document, and the plan walks the FLATTENED page: the inherited entry and the
        // override, in inherited-first order.
        _ = BindingProfile.Compile(document: document);

        Assert.Equal(
            actual: BindingSessionPlan.FromPage(
                document: document,
                pageId: "modal"
            ).Steps.Select(selector: static step => step.SuggestedSource),
            expected: ["keyboard.a", "keyboard.b"]
        );
    }
    [Fact]
    public void ACycleIsRefusedByBothCompileAndFromPage() {
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Chord: [],
                    Group: "play",
                    Page: new BindingPageDefinition(
                        Entries: [new BindingPageEntryDefinition(Command: "play.a", Sources: ["keyboard.a"])],
                        Id: "play-base",
                        Inherits: "modal"
                    )
                ),
                new BindingChordDefinition(
                    Chord: ["keyboard.leftShift"],
                    Group: "play",
                    Page: new BindingPageDefinition(
                        Entries: [new BindingPageEntryDefinition(Command: "play.b", Sources: ["keyboard.b"])],
                        Id: "modal",
                        Inherits: "play-base"
                    )
                ),
            ]
        );

        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: document)).Message,
            expectedSubstring: "contains a cycle"
        );
        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => BindingSessionPlan.FromPage(
                document: document,
                pageId: "modal"
            )).Message,
            expectedSubstring: "contains a cycle"
        );
    }
}
