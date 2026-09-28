using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;

namespace Puck.Input.Tests;

/// <summary>Laws for keyboard focus between the game and a passthrough source: a press on a source the local user opened
/// focuses it and its keys stop reaching the game; the reserved chord returns input to the game and its Escape reaches
/// nobody while a source has focus, and is the game's Escape while none does; a key's release follows its press across a focus change; and a document's source never takes
/// focus.</summary>
public sealed class SourceFocusLawTests {
    private static readonly SourceMapping Editor = SourceMapping.WholePane(
        height: 720,
        region: new NormalizedRect(
            Height: 0.5f,
            Width: 0.5f,
            X: 0f,
            Y: 0f
        ),
        source: SourceHandle.Producer(name: "editor"),
        width: 1280
    ) with {
        Destination = SourceDestination.Passthrough,
        Opener = SourceOpener.LocalUser,
    };

    private static SourceHit OnEditor => Editor.MapDisplayPoint(
        displayHeight: 1080,
        displayWidth: 1920,
        point: new FixedVector2(
            X: FixedQ4816.FromInteger(value: 100),
            Y: FixedQ4816.FromInteger(value: 100)
        )
    );

    [Fact]
    public void TheFocusChordReturnsInputToTheGame() {
        var focus = new SourceFocus();

        Assert.True(condition: focus.Press(
            hit: OnEditor,
            mapping: Editor
        ));
        Assert.Equal(
            expected: SourceHandle.Producer(name: "editor"),
            actual: focus.Focused
        );
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.LetterDown(character: 'a'))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.LetterUp(character: 'a'))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: new WindowInputEvent(
                Kind: WindowInputKind.Text,
                Text: "a"
            ))
        );

        // Control and Alt reach the source; Escape completes the chord, reaches nobody, and returns focus.
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlLeft))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltRight))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Consumed,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape))
        );
        Assert.Null(@object: focus.Focused);
        Assert.Equal(
            expected: SourceFocusRoute.Consumed,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape))
        );

        // The modifiers' releases follow their presses to the source, so it is not left holding them.
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.ControlLeft))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.AltRight))
        );

        // The game has the keys back.
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.LetterDown(character: 'a'))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: new WindowInputEvent(
                Kind: WindowInputKind.Text,
                Text: "a"
            ))
        );
    }
    [Fact]
    public void WhileTheGameHasFocusTheChordsEscapeIsTheGames() {
        var focus = new SourceFocus();

        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlRight))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltLeft))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.AltLeft))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape))
        );
    }
    [Fact]
    public void AReleaseFollowsItsPressWhenFocusMoves() {
        var focus = new SourceFocus();

        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Space))
        );
        Assert.True(condition: focus.Press(
            hit: OnEditor,
            mapping: Editor
        ));
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Space))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Enter))
        );
        Assert.False(condition: focus.Press(
            hit: default,
            mapping: null
        ));
        Assert.Equal(
            expected: SourceFocusRoute.Source,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Enter))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Enter))
        );
    }
    [Fact]
    public void ADocumentsSourceOrAPressOffTheSourceNeverTakesFocus() {
        var focus = new SourceFocus();

        Assert.False(condition: focus.Press(
            hit: OnEditor,
            mapping: (Editor with { Opener = SourceOpener.Document })
        ));
        Assert.False(condition: focus.Press(
            hit: OnEditor,
            mapping: (Editor with { Destination = SourceDestination.Presentation })
        ));
        Assert.False(condition: focus.Press(
            hit: Editor.MapDisplayPoint(
                displayHeight: 1080,
                displayWidth: 1920,
                point: new FixedVector2(
                    X: FixedQ4816.FromInteger(value: 1500),
                    Y: FixedQ4816.FromInteger(value: 100)
                )
            ),
            mapping: Editor
        ));
        Assert.Null(@object: focus.Focused);
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: WindowInputEvent.LetterDown(character: 'q'))
        );
        Assert.Equal(
            expected: SourceFocusRoute.Game,
            actual: focus.Route(inputEvent: new WindowInputEvent(
                Kind: WindowInputKind.PointerPosition,
                Phase: CommandPhase.Active,
                Vector: Vector2.One
            ))
        );
    }
}
