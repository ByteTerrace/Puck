using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;

namespace Puck.Demo.Overworld;

/// <summary>
/// Console mode's bridge onto the binding-page system. It samples its OWN <see cref="IInputArbiter"/> lane for its
/// slot (the arbiter is the run's actual drainer, shared with the brick-pad service and every other console-mode
/// registrant) and replays that state into the paged resolver as synthesized signals — the modifiers (triggers)
/// first, then button edges in a fixed order — which preserves the router path's semantics exactly: the signal
/// that crosses a threshold selects the page for everything after it, presses latch their resolved binding list,
/// and hysteresis rides the analog trigger values. The player's held/pressed command sets come out the other side;
/// movement stays raw (the sticks are always-on; the dpad contributes only when the active page leaves that
/// direction unbound). Console mode owns ONE INSTANCE PER PLAYER SLOT — each holds its own lane for its own
/// player's slot, so pages, latches, and chords stay per-player exactly as they do on the router path.
/// </summary>
internal sealed class OverworldPageInput {
    /// <summary>The physical buttons replayed as edges, in the profile's slot order (the deterministic
    /// capture-order stand-in).</summary>
    private static readonly (GamepadButtons Button, string Source)[] ButtonSources = [
        (GamepadButtons.DpadUp, InputSources.Gamepad.DpadUp),
        (GamepadButtons.DpadRight, InputSources.Gamepad.DpadRight),
        (GamepadButtons.DpadDown, InputSources.Gamepad.DpadDown),
        (GamepadButtons.DpadLeft, InputSources.Gamepad.DpadLeft),
        (GamepadButtons.LeftShoulder, InputSources.Gamepad.LeftShoulder),
        (GamepadButtons.LeftStickPress, InputSources.Gamepad.LeftStickPress),
        (GamepadButtons.ButtonNorth, InputSources.Gamepad.ButtonNorth),
        (GamepadButtons.ButtonWest, InputSources.Gamepad.ButtonWest),
        (GamepadButtons.ButtonSouth, InputSources.Gamepad.ButtonSouth),
        (GamepadButtons.ButtonEast, InputSources.Gamepad.ButtonEast),
        (GamepadButtons.RightShoulder, InputSources.Gamepad.RightShoulder),
        (GamepadButtons.RightStickPress, InputSources.Gamepad.RightStickPress),
    ];
    private readonly IInputArbiter m_arbiter;
    private readonly PagedInputBindings m_bindings;
    private readonly HashSet<string> m_held = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly object m_lane;
    private readonly HashSet<string> m_pressed = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly int m_slot;
    private bool m_brickInputSuppressed;
    private GamepadState m_last = GamepadState.Neutral;

    /// <summary>Initializes a new instance of the <see cref="OverworldPageInput"/> class.</summary>
    /// <param name="arbiter">The input arbiter this adapter's own lane samples through.</param>
    /// <param name="bindings">The paged resolver shared with the binding bar.</param>
    /// <param name="slot">The player slot this adapter replays into (0 = the room player).</param>
    /// <exception cref="ArgumentNullException"><paramref name="arbiter"/> or <paramref name="bindings"/> is
    /// <see langword="null"/>.</exception>
    public OverworldPageInput(IInputArbiter arbiter, PagedInputBindings bindings, int slot) {
        ArgumentNullException.ThrowIfNull(argument: arbiter);
        ArgumentNullException.ThrowIfNull(argument: bindings);

        m_arbiter = arbiter;
        m_bindings = bindings;
        m_lane = arbiter.RegisterLane(policy: InputLanePolicy.ForPlayer(playerIndex: slot));
        m_slot = slot;
    }

    /// <summary>Gets the active page's view (the binding bar reads it; the movement rule checks its dpad claims).</summary>
    public BindingPageView View =>
        m_bindings.ViewFor(slot: m_slot);
    /// <summary>Gets whether this slot is on the no-modifier/default page. Only this page may feed GamingBrick joypad
    /// input; debug pages own their buttons without leaking them into the emulated machine.</summary>
    public bool IsDefaultPage {
        get {
            foreach (var modifier in View.Modifiers) {
                if (modifier.Required) {
                    return false;
                }
            }

            return true;
        }
    }
    /// <summary>Gets whether this slot may feed the GamingBrick joypad this frame. Entering a debug page suppresses
    /// brick input until every page button is released, so a button pressed for a debug command cannot leak into the
    /// game by releasing the modifier first.</summary>
    public bool AllowsBrickInput => (IsDefaultPage && !m_brickInputSuppressed);

    /// <summary>Gets this frame's raw drained state for this adapter's slot (the same value <see cref="BeginFrame"/>
    /// just sampled) — the movement/cycle/authoring logic outside the paged resolver reads this instead of a
    /// separate sample of its own.</summary>
    public GamepadState Raw => m_last;

    /// <summary>Drains the arbiter (a no-op if some other registrant already drained this frame key), samples this
    /// adapter's own lane, and replays the result into the resolver: trigger values first (page selection), then
    /// button edges (command presses/releases). Call once per advanced frame, before reading the command sets.</summary>
    /// <param name="frameKey">The caller's render-frame key (any value that changes once per pumped frame).</param>
    public void BeginFrame(ulong frameKey) {
        m_arbiter.DrainFrame(frameKey: frameKey);

        var state = m_arbiter.Sample(laneToken: m_lane);

        m_pressed.Clear();

        // Modifiers before buttons — the router applies signals in capture order, and the signal that crosses a
        // threshold must select the page for everything after it in the same frame.
        _ = m_bindings.Resolve(slot: m_slot, signal: new InputSignal(
            DeviceId: default,
            Phase: CommandPhase.Active,
            Source: InputSources.Gamepad.LeftTrigger,
            Value: CommandValue.Axis(value: state.LeftTrigger)
        ));
        _ = m_bindings.Resolve(slot: m_slot, signal: new InputSignal(
            DeviceId: default,
            Phase: CommandPhase.Active,
            Source: InputSources.Gamepad.RightTrigger,
            Value: CommandValue.Axis(value: state.RightTrigger)
        ));

        foreach (var (button, source) in ButtonSources) {
            var was = (0 != (m_last.Buttons & button));
            var now = (0 != (state.Buttons & button));

            if (was == now) {
                continue;
            }

            var signal = (now ? InputSignal.Press(source: source) : InputSignal.Release(source: source));
            var resolved = m_bindings.Resolve(slot: m_slot, signal: in signal);

            if (resolved is null) {
                continue;
            }

            // A release resolves to the list its press latched (even across a page change), so held bookkeeping
            // by command name can never leak a held command.
            foreach (var binding in resolved) {
                if (now) {
                    _ = m_held.Add(item: binding.Command);
                    _ = m_pressed.Add(item: binding.Command);
                } else {
                    _ = m_held.Remove(item: binding.Command);
                }
            }
        }

        m_last = state;

        if (!IsDefaultPage) {
            m_brickInputSuppressed = true;
        } else if (!AnyPageButtonHeld(state: in state)) {
            m_brickInputSuppressed = false;
        }
    }

    /// <summary>Whether a command is held as of this frame.</summary>
    /// <param name="command">The command name.</param>
    public bool IsHeld(string command) =>
        m_held.Contains(item: command);

    /// <summary>Whether a command's press edge landed this frame.</summary>
    /// <param name="command">The command name.</param>
    public bool WasPressed(string command) =>
        m_pressed.Contains(item: command);

    /// <summary>Whether the active page binds a physical source — a page that claims a dpad direction removes it
    /// from movement for as long as the page is selected.</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    public bool Binds(string source) {
        foreach (var button in View.Buttons) {
            if (string.Equals(a: button.Source, b: source, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    private static bool AnyPageButtonHeld(in GamepadState state) {
        foreach (var (button, _) in ButtonSources) {
            if (0 != (state.Buttons & button)) {
                return true;
            }
        }

        return false;
    }
}
