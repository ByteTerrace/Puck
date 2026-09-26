using Puck.Commands;

namespace Puck.Input;

/// <summary>Where <see cref="SourceFocus"/> sends one window input event.</summary>
public enum SourceFocusRoute : byte {
    /// <summary>The game: the event continues through <see cref="WindowInputMapper"/> to the command router.</summary>
    Game = 0,
    /// <summary>The focused source: the event goes to the external window it shows, and the game never sees it.</summary>
    Source = 1,
    /// <summary>Nobody: the Escape that completes the reserved chord while a source has focus, which neither the game
    /// nor the source receives.</summary>
    Consumed = 2,
}
/// <summary>Keyboard focus between the game and a <see cref="SourceDestination.Passthrough"/> source. A pointer press on a
/// permitted passthrough source focuses it, and a press anywhere else returns focus to the game; while a source has
/// focus its keys and text go to it instead of the game. The reserved chord, <see cref="InputSources.Keyboard.ControlLeft"/>
/// or <see cref="InputSources.Keyboard.ControlRight"/> with <see cref="InputSources.Keyboard.AltLeft"/> or
/// <see cref="InputSources.Keyboard.AltRight"/> held and <see cref="InputSources.Keyboard.Escape"/> pressed, returns
/// focus to the game while a source has it, and that Escape reaches nobody; while the game has focus the chord means
/// nothing, and its Escape is the game's. A key's release goes wherever its press went, so neither
/// side is left holding a key after focus moves. Pointer events are routed by the mapping a hit lands on, never by
/// focus, so this routes only key and text events; every other kind goes to the game.</summary>
/// <remarks>Window-pump-thread only. The route is a pure function of the event sequence and the presses reported to
/// <see cref="Press"/>.</remarks>
public sealed class SourceFocus {
    private readonly Dictionary<(KeyCode Key, char Character), SourceFocusRoute> m_pressedTo = [];
    private readonly HeldDigitalInputState m_held = new();

    /// <summary>Gets the source that has keyboard focus, or <see langword="null"/> when the game has it.</summary>
    public SourceHandle? Focused { get; private set; }

    // The chord only returns focus, so with none to return it is no chord and the game keeps its Escape.
    private bool IsChord(in WindowInputEvent inputEvent) => (
        Focused.HasValue &&
        (inputEvent.Key == KeyCode.Escape) &&
        (inputEvent.Phase == CommandPhase.Started) &&
        (m_held.IsHeld(source: InputSources.Keyboard.ControlLeft) || m_held.IsHeld(source: InputSources.Keyboard.ControlRight)) &&
        (m_held.IsHeld(source: InputSources.Keyboard.AltLeft) || m_held.IsHeld(source: InputSources.Keyboard.AltRight))
    );

    /// <summary>Reports a pointer press: focus moves to the pressed source when <paramref name="mapping"/> takes the
    /// passthrough destination, a local user opened it, the mapping is valid, and the press lands on its pixels;
    /// otherwise focus returns to the game.</summary>
    /// <param name="mapping">The mapping the press landed on, or <see langword="null"/> when it landed on none.</param>
    /// <param name="hit">Where on the mapping it landed.</param>
    /// <returns><see langword="true"/> when a source has focus afterwards.</returns>
    public bool Press(SourceMapping? mapping, SourceHit hit) {
        Focused = ((
            (mapping is not null) &&
            (mapping.Destination == SourceDestination.Passthrough) &&
            SourcePassthrough.IsPermitted(opener: mapping.Opener) &&
            mapping.TryValidate(refusal: out _) &&
            hit.IsOnSource
        )
            ? mapping.Source
            : null
        );

        return Focused.HasValue;
    }
    /// <summary>Returns focus to the game.</summary>
    public void ReturnToGame() {
        Focused = null;
    }
    /// <summary>Routes one window input event.</summary>
    /// <param name="inputEvent">The event, in the window's order.</param>
    /// <returns>Where the event goes.</returns>
    public SourceFocusRoute Route(in WindowInputEvent inputEvent) {
        switch (inputEvent.Kind) {
            case WindowInputKind.Key:
                var key = (inputEvent.Key, inputEvent.Character);

                if (inputEvent.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
                    m_held.Observe(
                        frameKey: 0UL,
                        signal: WindowInputMapper.ToInputSignal(inputEvent: inputEvent)
                    );

                    return (m_pressedTo.Remove(
                        key: key,
                        value: out var pressedTo
                    )
                        ? pressedTo
                        : Destination()
                    );
                }

                var chord = IsChord(inputEvent: inputEvent);

                if (chord) {
                    Focused = null;
                }

                m_held.Observe(
                    frameKey: 0UL,
                    signal: WindowInputMapper.ToInputSignal(inputEvent: inputEvent)
                );

                // A repeat of a held key keeps going where its first press went.
                if (!m_pressedTo.TryGetValue(
                    key: key,
                    value: out var route
                )) {
                    route = (chord
                        ? SourceFocusRoute.Consumed
                        : Destination()
                    );
                    m_pressedTo.Add(
                        key: key,
                        value: route
                    );
                }

                return route;
            case WindowInputKind.Text:
                return Destination();
            case WindowInputKind.FocusLost:
                m_held.Clear();
                m_pressedTo.Clear();

                return SourceFocusRoute.Game;
            default:
                return SourceFocusRoute.Game;
        }
    }

    private SourceFocusRoute Destination() => (Focused.HasValue
        ? SourceFocusRoute.Source
        : SourceFocusRoute.Game
    );
}
