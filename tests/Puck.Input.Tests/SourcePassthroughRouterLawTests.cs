using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;

namespace Puck.Input.Tests;

/// <summary>Laws for the router that hosts <see cref="SourceFocus"/>: a press on a source the local user opened focuses
/// it, and its pointer and keys reach its window at the mapped client point instead of the game; the reserved chord
/// returns focus to the game; a source a document declared never focuses or receives input; and every release goes where
/// its press went.</summary>
/// <remarks>The display is 2048x1024. The editor's pane covers its top-left quarter and the notes' pane the quarter
/// beside it, each showing a 512x256 image of a window whose captured frame is 1024x512 physical pixels with its client
/// area 32 below and 8 right of the frame's corner, at a DPI scale of 2. So display point (256, 128) is source pixel
/// (128, 64), frame point (256, 128) and client point (248, 96) physical, (124, 48) in the window's own
/// coordinates.</remarks>
public sealed class SourcePassthroughRouterLawTests {
    private const int DisplayHeight = 1024;
    private const int DisplayWidth = 2048;

    private static readonly SourceHandle EditorSource = SourceHandle.Instance(name: "editor");
    private static readonly SourceHandle NotesSource = SourceHandle.Instance(name: "notes");
    private static readonly Vector2 OnEditor = new(
        x: 256f,
        y: 128f
    );
    private static readonly Vector2 OnEditorClient = new(
        x: 124f,
        y: 48f
    );
    private static readonly Vector2 OnNotes = new(
        x: 1280f,
        y: 128f
    );

    private static SourceMapping Pane(SourceHandle source, float x) => SourceMapping.WholePane(
        height: 256,
        region: new NormalizedRect(
            Height: 0.5f,
            Width: 0.5f,
            X: x,
            Y: 0f
        ),
        source: source,
        width: 512
    ) with {
        Destination = SourceDestination.Passthrough,
        Opener = SourceOpener.LocalUser,
    };
    private static (SourcePassthroughRouter Router, FakeWindow Editor, FakeWindow Notes) Build(params SourceMapping[] panes) {
        var editor = new FakeWindow();
        var notes = new FakeWindow();
        var router = new SourcePassthroughRouter(windows: new FakeWindows(windows: new Dictionary<SourceHandle, FakeWindow> {
            [EditorSource] = editor,
            [NotesSource] = notes,
        }));

        router.Publish(
            displayHeight: DisplayHeight,
            displayWidth: DisplayWidth,
            panes: ((panes.Length == 0)
                ? [
                    Pane(
                        source: EditorSource,
                        x: 0f
                    ),
                    Pane(
                        source: NotesSource,
                        x: 0.5f
                    ),
                ]
                : panes
            )
        );

        return (router, editor, notes);
    }
    private static bool Click(SourcePassthroughRouter router, Vector2 point) {
        _ = router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: point));

        var pressed = router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Started
        ));
        var released = router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Completed
        ));

        Assert.Equal(
            actual: released,
            expected: pressed
        );

        return pressed;
    }

    [Fact]
    public void AFocusedSourcesPointerAndKeysReachItsWindowAtTheMappedClientPoint() {
        var (router, editor, notes) = Build();

        // The game keeps the pointer's position to draw its cursor; the window hears the move too.
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: OnEditor)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerDelta(delta: Vector2.One)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Started
        )));
        Assert.Equal(
            expected: EditorSource,
            actual: router.Focus.Focused
        );
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Completed
        )));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerWheel(notches: 1f)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.LetterDown(character: 'a')));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.TypedText(text: "a")));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.LetterUp(character: 'a')));

        Assert.Equal(
            expected: [
                Delivery.Pointer(
                    kind: WindowInputKind.PointerPosition,
                    phase: CommandPhase.Active,
                    point: OnEditorClient
                ),
                Delivery.Pointer(
                    button: 0,
                    kind: WindowInputKind.PointerButton,
                    phase: CommandPhase.Started,
                    point: OnEditorClient
                ),
                Delivery.Pointer(
                    button: 0,
                    kind: WindowInputKind.PointerButton,
                    phase: CommandPhase.Completed,
                    point: OnEditorClient
                ),
                Delivery.Pointer(
                    kind: WindowInputKind.PointerWheel,
                    phase: CommandPhase.Active,
                    point: OnEditorClient
                ),
                Delivery.OfKey(
                    character: 'a',
                    phase: CommandPhase.Started
                ),
                Delivery.OfText(text: "a"),
                Delivery.OfKey(
                    character: 'a',
                    phase: CommandPhase.Completed
                ),
            ],
            actual: editor.Deliveries
        );
        Assert.Empty(collection: notes.Deliveries);
    }
    [Fact]
    public void TheReservedChordReturnsFocusToTheGame() {
        var (router, editor, _) = Build();

        Assert.True(condition: Click(
            point: OnEditor,
            router: router
        ));
        editor.Deliveries.Clear();

        // Control and Alt reach the editor; Escape completes the chord, reaches nobody and returns focus.
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlLeft)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltLeft)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape)));
        Assert.Null(@object: router.Focus.Focused);
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape)));

        // The modifiers' releases follow their presses to the editor, so it is not left holding them.
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.ControlLeft)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.AltLeft)));

        // The game has the keys back.
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.LetterDown(character: 'b')));
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.TypedText(text: "b")));
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.LetterUp(character: 'b')));

        Assert.Equal(
            expected: [
                Delivery.OfKey(
                    key: KeyCode.ControlLeft,
                    phase: CommandPhase.Started
                ),
                Delivery.OfKey(
                    key: KeyCode.AltLeft,
                    phase: CommandPhase.Started
                ),
                Delivery.OfKey(
                    key: KeyCode.ControlLeft,
                    phase: CommandPhase.Completed
                ),
                Delivery.OfKey(
                    key: KeyCode.AltLeft,
                    phase: CommandPhase.Completed
                ),
            ],
            actual: editor.Deliveries
        );
    }
    [Fact]
    public void TheChordsEscapeIsTheGamesUnlessASourceHoldsFocus() {
        var (router, editor, _) = Build();

        // With nothing focused the chord returns nothing, so every key of it, Escape included, reaches the game.
        foreach (var key in ((ReadOnlySpan<KeyCode>)[KeyCode.ControlLeft, KeyCode.AltLeft, KeyCode.Escape])) {
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: key)));
        }
        foreach (var key in ((ReadOnlySpan<KeyCode>)[KeyCode.Escape, KeyCode.AltLeft, KeyCode.ControlLeft])) {
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: key)));
        }

        // With the editor focused the same chord returns focus and its Escape reaches nobody.
        Assert.True(condition: Click(
            point: OnEditor,
            router: router
        ));
        editor.Deliveries.Clear();
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlLeft)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltLeft)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape)));
        Assert.Null(@object: router.Focus.Focused);
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape)));
        Assert.DoesNotContain(
            collection: editor.Deliveries,
            filter: static delivery => (delivery.Key == KeyCode.Escape)
        );

        // A second Escape, the modifiers still held, finds nothing focused and is the game's.
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape)));
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape)));
    }
    [Fact]
    public void ADocumentDeclaredSourceNeverFocusesOrReceivesInput() {
        // The resolver holds a window for the source, so only the opener and destination stand between it and input.
        SourceMapping[] declared = [
            (Pane(
                source: EditorSource,
                x: 0f
            ) with { Opener = SourceOpener.Document }),
            (Pane(
                source: NotesSource,
                x: 0.5f
            ) with { Destination = SourceDestination.Presentation }),
        ];

        var (router, editor, notes) = Build(panes: declared);

        foreach (var point in ((ReadOnlySpan<Vector2>)[OnEditor, OnNotes])) {
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: point)));
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.PointerDelta(delta: Vector2.One)));
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.PointerWheel(notches: 1f)));
            Assert.False(condition: Click(
                point: point,
                router: router
            ));
            Assert.Null(@object: router.Focus.Focused);
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.LetterDown(character: 'q')));
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.TypedText(text: "q")));
            Assert.False(condition: router.Route(inputEvent: WindowInputEvent.LetterUp(character: 'q')));
        }

        Assert.Empty(collection: editor.Deliveries);
        Assert.Empty(collection: notes.Deliveries);
    }
    [Fact]
    public void EveryReleaseGoesWhereItsPressWent() {
        var (router, editor, notes) = Build();

        // A key the game pressed is released to the game after the editor takes focus.
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Space)));
        Assert.True(condition: Click(
            point: OnEditor,
            router: router
        ));
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Space)));

        // A key the editor was pressed with is released to the editor after the notes take focus.
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Enter)));
        Assert.True(condition: Click(
            point: OnNotes,
            router: router
        ));
        Assert.Equal(
            expected: NotesSource,
            actual: router.Focus.Focused
        );
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Enter)));
        Assert.Equal(
            expected: Delivery.OfKey(
                key: KeyCode.Enter,
                phase: CommandPhase.Completed
            ),
            actual: editor.Deliveries[^1]
        );
        Assert.DoesNotContain(
            collection: notes.Deliveries,
            filter: static delivery => (delivery.Kind == WindowInputKind.Key)
        );
        Assert.DoesNotContain(
            collection: editor.Deliveries,
            filter: static delivery => (delivery.Key == KeyCode.Space)
        );

        // A button pressed on the editor drags off its pane and is released there, to the editor alone.
        editor.Deliveries.Clear();
        notes.Deliveries.Clear();
        _ = router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: OnEditor));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 1,
            phase: CommandPhase.Started
        )));
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: OnNotes)));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 1,
            phase: CommandPhase.Completed
        )));

        // Display point (1280, 128) lies past the editor's pane: source pixel (640, 64), client point (1272, 96).
        var dragged = new Vector2(
            x: 636f,
            y: 48f
        );

        Assert.Equal(
            expected: [
                Delivery.Pointer(
                    kind: WindowInputKind.PointerPosition,
                    phase: CommandPhase.Active,
                    point: OnEditorClient
                ),
                Delivery.Pointer(
                    button: 1,
                    kind: WindowInputKind.PointerButton,
                    phase: CommandPhase.Started,
                    point: OnEditorClient
                ),
                Delivery.Pointer(
                    inClient: false,
                    kind: WindowInputKind.PointerPosition,
                    phase: CommandPhase.Active,
                    point: dragged
                ),
                Delivery.Pointer(
                    button: 1,
                    inClient: false,
                    kind: WindowInputKind.PointerButton,
                    phase: CommandPhase.Completed,
                    point: dragged
                ),
            ],
            actual: editor.Deliveries
        );
        Assert.Empty(collection: notes.Deliveries);

        // A press on the captured title bar focuses the editor and reaches no client, nor does its release.
        editor.Deliveries.Clear();
        Assert.True(condition: Click(
            point: new Vector2(
                x: 256f,
                y: 8f
            ),
            router: router
        ));
        Assert.Equal(
            expected: EditorSource,
            actual: router.Focus.Focused
        );
        Assert.Empty(collection: editor.Deliveries);

        // A press off every pane returns focus to the game and is the game's.
        Assert.False(condition: Click(
            point: new Vector2(
                x: 1500f,
                y: 900f
            ),
            router: router
        ));
        Assert.Null(@object: router.Focus.Focused);
    }
    [Fact]
    public void LosingWindowFocusReleasesWhatASourceHolds() {
        var (router, editor, _) = Build();

        _ = router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: OnEditor));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Started
        )));
        Assert.True(condition: router.Route(inputEvent: WindowInputEvent.LetterDown(character: 'c')));
        editor.Deliveries.Clear();

        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.FocusLost()));
        Assert.Equal(
            expected: [
                Delivery.Pointer(
                    button: 0,
                    kind: WindowInputKind.PointerButton,
                    phase: CommandPhase.Completed,
                    point: OnEditorClient
                ),
                Delivery.OfKey(
                    character: 'c',
                    phase: CommandPhase.Completed
                ),
            ],
            actual: editor.Deliveries
        );

        // Nothing is left held: the next release is the game's.
        Assert.False(condition: router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Completed
        )));
    }

    private sealed record Delivery(WindowInputKind Kind, CommandPhase Phase, KeyCode Key, char Character, string? Text, int Button, Vector2 Point, bool InClient) {
        public static Delivery OfKey(CommandPhase phase, KeyCode key = KeyCode.Letter, char character = '\0') => new(
            Button: -1,
            Character: character,
            InClient: false,
            Key: key,
            Kind: WindowInputKind.Key,
            Phase: phase,
            Point: Vector2.Zero,
            Text: null
        );
        public static Delivery OfText(string text) => new(
            Button: -1,
            Character: '\0',
            InClient: false,
            Key: KeyCode.None,
            Kind: WindowInputKind.Text,
            Phase: CommandPhase.Started,
            Point: Vector2.Zero,
            Text: text
        );
        public static Delivery Pointer(WindowInputKind kind, CommandPhase phase, Vector2 point, int button = -1, bool inClient = true) => new(
            Button: button,
            Character: '\0',
            InClient: inClient,
            Key: KeyCode.None,
            Kind: kind,
            Phase: phase,
            Point: point,
            Text: null
        );
    }
    private sealed class FakeWindow : ISourcePassthroughWindow {
        public List<Delivery> Deliveries { get; } = [];
        public string Title => "fake";

        public void DeliverKey(in WindowInputEvent inputEvent) => Deliveries.Add(item: new Delivery(
            Button: -1,
            Character: inputEvent.Character,
            InClient: false,
            Key: inputEvent.Key,
            Kind: inputEvent.Kind,
            Phase: inputEvent.Phase,
            Point: Vector2.Zero,
            Text: inputEvent.Text
        ));
        public void DeliverPointer(in WindowInputEvent inputEvent, SourcePassthroughPoint point) => Deliveries.Add(item: new Delivery(
            Button: inputEvent.ButtonIndex,
            Character: '\0',
            InClient: point.InClient,
            Key: KeyCode.None,
            Kind: inputEvent.Kind,
            Phase: inputEvent.Phase,
            Point: point.Logical,
            Text: null
        ));
        public bool TryDescribe(out SourcePassthroughWindow window) {
            window = new SourcePassthroughWindow(
                Client: new SourcePixelRect(
                    Height: 472,
                    Width: 1008,
                    X: 8,
                    Y: 32
                ),
                DpiScale: 2f,
                FrameHeight: 512,
                FrameWidth: 1024
            );

            return true;
        }
    }
    private sealed class FakeWindows(Dictionary<SourceHandle, FakeWindow> windows) : ISourcePassthroughWindows {
        public bool TryGet(SourceHandle source, [NotNullWhen(returnValue: true)] out ISourcePassthroughWindow? window) {
            if (windows.TryGetValue(
                key: source,
                value: out var fake
            )) {
                window = fake;

                return true;
            }

            window = null;

            return false;
        }
    }
}
