using Puck.Commands;
using Puck.Input;
using Puck.Launcher;

namespace Puck.World;

/// <summary>
/// The engine-default binding document. It is the first layer of every
/// seat's composed mapping (engine default ⊕ world overlays ⊕ profile bindings ⊕ live rebinds), code-authored
/// (never serialized — a null profile <c>Bindings</c> section inherits
/// it), and carries three page groups: the <see cref="PlayGroup"/> rows below (the <c>player.*</c> vocabulary), the
/// <see cref="RosterGroup"/> row (a not-yet-active seat's group, selected by context row — see the class remarks),
/// and the <see cref="WorldEditorBindings"/> rows (group <c>editor</c>, always compiled in — entering the editor is a
/// per-seat <see cref="WorldSeatBindings.SetActiveGroup"/> pointer flip, never a recompose).
/// </summary>
/// <remarks>
/// The play group is four pages: <c>[]</c> base, <c>[ctrl-l]</c>, <c>[ctrl-r]</c>, and <c>[tab]</c>.
/// Holding the chord is the page turn: the binding bar re-renders the page the held chord selects, so the chord
/// vocabulary is discoverable rather than memorized. Gamepad triggers are deliberately NOT claimed as play-page
/// chords: a world can bind their full analog values on the resting page without the trigger first switching away
/// from the very binding it is driving. They remain document-global modifier vocabulary because the editor group
/// explicitly uses them after its mode flip. The two Control pages, <see cref="ControlLeftPageId"/> and
/// <see cref="ControlRightPageId"/>, carry
/// the same keyboard movement rows as the base page for the identical reason — a held Control key must not strand a
/// keyboard player's movement any more than a held trigger strands a gamepad player's — plus Ctrl+C firing
/// <see cref="EditorCommandModule.StatusCommand"/>. Both pages are declared, carrying identical entries, because
/// <see cref="InputSources.Keyboard"/> has no unified "either side" modifier source: a chord group that means
/// either Control key declares both. Page 7, <see cref="WheelHoldPageId"/>, is the play group's wheel-hold page:
/// holding Tab selects it, and selecting it presents the group's radial action menu (the <c>wheels</c> row below) —
/// deliberately no keyboard movement rows there, because the radial is a modal gesture (the avatar stands while a
/// sector is chosen); it carries the stick routers, the ring-cycle rows
/// (<see cref="WorldWheelCommandModule.RingCommand"/> on Arrow Up/Down and D-pad Up/Down — the mouse-less twin of
/// the mouse wheel), and Tab's own release edge firing <see cref="WorldWheelCommandModule.CommitCommand"/> (the
/// press that turned the page latches this page's row, so the release commits through the ordinary chord-latch
/// machinery). Editor entry is Gamepad Back on the base page, or the wheel's Editor sector — Tab belongs wholly to
/// the wheel.
/// The keyboard movement layout — W/S forward/back, A/D strafe left/right, Q/E turn left/right (crisp planar walk, no
/// auto-facing); the arrow keys mirror it (up/down forward/back, left/right turn). Each movement source binds twice —
/// a press edge (default phase) and a release edge (<see cref="CommandPhase.Completed"/>) — so one channel destination
/// reads the phase to hold-or-free its axis. Enter confirms, F1..F4 claim a slot carried as the
/// binding's constant <see cref="CommandValue.Axis(float)"/>, the sticks route structural movement and look, and
/// Start cycles. Worlds author their action-channel bindings in overlays.
/// The <see cref="RosterGroup"/> row: a <c>roster</c> context row (<see cref="WorldContextFamilies.Roster"/>) selects
/// it for a seat whose participant lifecycle is not yet <see cref="WorldContextFamilies.RosterActive"/> — Gamepad
/// South fires <see cref="PlayerCommandModule.ConfirmCommand"/> there instead of driving a channel, so a physical
/// button never carries two meanings inside one command handler; the active state ships no row, so an active seat
/// falls through to its requested group (the play group by default). The roster group declares no wheel and no
/// <c>[tab]</c> page — a not-yet-active seat's editor entry is Gamepad Back. No command name in this document ever
/// names a physical button.
/// </remarks>
internal static class WorldDefaultBindings {
    /// <summary>The play group — the default page group every seat resolves in outside a mode.</summary>
    public const string PlayGroup = "play";

    /// <summary>The play group's resting page id (chord: nothing held) — page 0.</summary>
    public const string BasePageId = "base";
    /// <summary>The roster group — a not-yet-active seat's group, selected by a <c>roster</c> context row (see the
    /// class remarks). Holds the roster-management verbs and the stick routers a pending seat's profile picker
    /// needs; carries no channel destination, so no physical button drives gameplay while a seat sits here.</summary>
    public const string RosterGroup = "roster";

    /// <summary>The roster group's sole (resting) page id.</summary>
    public const string RosterBasePageId = "roster-base";

    /// <summary>The left-trigger modifier id (chord vocabulary: <c>lt</c>). Declared here, on the engine default,
    /// because modifiers are document-global: every play and editor page chord references the same two
    /// declarations.</summary>
    public const string LeftTriggerModifierId = "lt";
    /// <summary>The right-trigger modifier id (chord vocabulary: <c>rt</c>).</summary>
    public const string RightTriggerModifierId = "rt";
    /// <summary>The left Control key modifier id (chord vocabulary: <c>ctrl-l</c>) — a native OS-modifier key
    /// declared as ordinary chord vocabulary via <see cref="InputSources.Keyboard.ControlLeft"/>, the same way
    /// <see cref="LeftTriggerModifierId"/> declares a gamepad trigger. Paired with <see cref="RightControlModifierId"/>
    /// per <see cref="InputSources.Keyboard"/>'s "either side declares both" rule; backs <see cref="ControlLeftPageId"/>.</summary>
    public const string LeftControlModifierId = "ctrl-l";
    /// <summary>The right Control key modifier id (chord vocabulary: <c>ctrl-r</c>) — <see cref="LeftControlModifierId"/>'s
    /// mirror, declared via <see cref="InputSources.Keyboard.ControlRight"/> so holding either physical Control key
    /// reaches the equivalent page; backs <see cref="ControlRightPageId"/>.</summary>
    public const string RightControlModifierId = "ctrl-r";

    /// <summary>The play group's left-Ctrl-held page id (chord: left Control held). Fires <c>Ctrl+C</c> as
    /// <see cref="EditorCommandModule.StatusCommand"/> — harmless (it only echoes the seat's active binding
    /// group/page), and observable over stdout the moment it fires. Carries the keyboard movement rows (see
    /// <see cref="ControlPageEntries"/>) so holding Ctrl never strands a keyboard player's movement.</summary>
    public const string ControlLeftPageId = "play-ctrl-l";
    /// <summary>The play group's right-Ctrl-held page id (chord: right Control held) — <see cref="ControlLeftPageId"/>'s
    /// mirror, carrying identical entries, so <c>Ctrl+C</c> and movement behave the same whichever physical Control
    /// key is held.</summary>
    public const string ControlRightPageId = "play-ctrl-r";

    /// <summary>The Tab modifier id (chord vocabulary: <c>tab</c>) — the radial action menu's hold key, declared via
    /// <see cref="InputSources.Keyboard.Tab"/> the same way <see cref="LeftControlModifierId"/> declares a native
    /// key. Tab belongs wholly to the wheel: no page binds it to a command's press edge, and each wheel-bearing
    /// group's hold page binds its release to <see cref="WorldWheelCommandModule.CommitCommand"/>.</summary>
    public const string TabModifierId = "tab";

    /// <summary>The play group's wheel hold page id (chord: Tab held) — see the class remarks.</summary>
    public const string WheelHoldPageId = "play-wheel";

    /// <summary>The play radial's profile-unique presentation id.</summary>
    public const string PlayWheelId = "play-primary";

    /// <summary>The play wheel's action-ring page id (see <see cref="PlayWheel"/>).</summary>
    public const string WheelActRingId = "play-wheel-act";
    // Editor trigger-chord hysteresis: latch at a deliberate squeeze, release only on a clear letoff, so a trigger
    // resting near its threshold never flaps an editor page mid-gesture. Play has no trigger page rows, leaving the
    // same sources wholly available to a world's ordinary analog bindings.
    private const float TriggerPress = 0.55f;
    private const float TriggerRelease = 0.35f;

    /// <summary>Builds the engine-default binding document.</summary>
    /// <returns>A fresh default document (callers compose it with overlays/profile/session layers before compiling).</returns>
    public static BindingProfileDocument BuildDocument() {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: LeftTriggerModifierId, Source: InputSources.Gamepad.LeftTrigger, PressThreshold: TriggerPress, ReleaseThreshold: TriggerRelease, Label: "LT"),
                new BindingModifierDefinition(Id: RightTriggerModifierId, Source: InputSources.Gamepad.RightTrigger, PressThreshold: TriggerPress, ReleaseThreshold: TriggerRelease, Label: "RT"),
                // Digital source, default thresholds: keyboard.controlLeft/Right read 0/1, so the 0.5/0.4 hysteresis
                // band latches on press and releases on release with no flicker — the same defaults every digital
                // modifier below the trigger pair would use. Declared as a pair (see the class remarks) because
                // InputSources.Keyboard has no unified "either side" Control source.
                new BindingModifierDefinition(Id: LeftControlModifierId, Source: InputSources.Keyboard.ControlLeft, Label: "Ctrl"),
                new BindingModifierDefinition(Id: RightControlModifierId, Source: InputSources.Keyboard.ControlRight, Label: "Ctrl"),
                // The wheel hold key — one physical key, one modifier (Tab has no left/right pair to declare).
                new BindingModifierDefinition(Id: TabModifierId, Source: InputSources.Keyboard.Tab, Label: "Tab"),
            ],
            Chords: [
                // The play resting page — first row, so "play" is the profile's DEFAULT group.
                new BindingChordDefinition(
                    Group: PlayGroup,
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: BasePageId,
                        Entries: [
                            // Movement — each source press-and-release onto the six hold/release verbs. Shared with
                            // the Ctrl pages (KeyboardMovementEntries) so a held Ctrl carries the identical rows.
                            .. KeyboardMovementEntries(),
                            // Roster verbs on the keyboard: Enter confirms; F1..F4 claim a slot as the binding's Axis1D value.
                            new BindingPageEntryDefinition(Source: InputSources.Keyboard.Enter, Command: PlayerCommandModule.ConfirmCommand, ActivateOn: CommandPhase.Started),
                            Claim(function: 1),
                            Claim(function: 2),
                            Claim(function: 3),
                            Claim(function: 4),
                            // The gamepad sticks route move/look (default active phase — the router re-dispatches the carried
                            // analog sample each tick).
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: PlayerCommandModule.MoveCommand),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: PlayerCommandModule.LookCommand),
                            // Start = device cycle (press edge).
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.Start, Command: PlayerCommandModule.CycleCommand, ActivateOn: CommandPhase.Started),
                            // Editor entry — Gamepad Back (the view/menu button); the keyboard's editor entry is the
                            // wheel's Editor sector (hold Tab). Trigger pages belong to the editor group only.
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.Back, Command: EditorCommandModule.EnterCommand, ActivateOn: CommandPhase.Started, Label: "Editor"),
                        ],
                        Label: "Base"
                    )
                ),
                // The Ctrl-held pages — the default chord binding a native OS-modifier key drives: hold either
                // Control key, then press C to fire editor.status (harmless; echoes the seat's active group/page
                // over stdout). Proves keyboard.controlLeft/Right resolve through the same held-chord machinery a
                // gamepad trigger does, end to end. Declared as a left/right pair, both carrying the identical
                // ControlPageEntries (movement rows included — a page without them would strand a keyboard player's
                // movement the same way a sparse trigger page would strand a gamepad player's), because
                // InputSources.Keyboard's "either side declares both" rule leaves no single source to chord on.
                new BindingChordDefinition(
                    Group: PlayGroup,
                    Chord: [LeftControlModifierId],
                    Page: new BindingPageDefinition(Id: ControlLeftPageId, Entries: ControlPageEntries(), Label: "Ctrl")
                ),
                new BindingChordDefinition(
                    Group: PlayGroup,
                    Chord: [RightControlModifierId],
                    Page: new BindingPageDefinition(Id: ControlRightPageId, Entries: ControlPageEntries(), Label: "Ctrl")
                ),
                // The wheel hold page — holding Tab selects it, and its selection presents the play wheel (the
                // wheels row below). Sticks stay routed so a gamepad hand keeps flying; keyboard movement is
                // deliberately absent (the radial is a modal gesture — see the class remarks); the arrows and D-pad
                // cycle the active ring, and Tab's own release edge commits (the press that turned the page latched
                // this row, so the release resolves back to it through the ordinary chord-latch machinery).
                new BindingChordDefinition(
                    Group: PlayGroup,
                    Chord: [TabModifierId],
                    Page: new BindingPageDefinition(
                        Id: WheelHoldPageId,
                        Entries: [
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: PlayerCommandModule.MoveCommand),
                            .. WheelHoldEntries(openerSource: InputSources.Keyboard.Tab),
                        ],
                        Label: "Wheel"
                    )
                ),
                // The roster group's sole page — a not-yet-active seat's group (see the class remarks and
                // RosterContextRows below). Carries the roster-management verbs (confirm/cycle/claim), editor entry,
                // and the stick routers a pending seat's profile picker needs (PlayerRoster.RouteMove's own picker
                // for a gamepad; the "turn" channel rows in KeyboardMovementEntries for a keyboard) — the same set
                // the play group's base page carries outside its two gameplay channel rows (Space/East), so a seat
                // loses no roster or editor-entry capability while it sits here, only gameplay itself.
                new BindingChordDefinition(
                    Group: RosterGroup,
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: RosterBasePageId,
                        Entries: [
                            .. KeyboardMovementEntries(),
                            new BindingPageEntryDefinition(Source: InputSources.Keyboard.Enter, Command: PlayerCommandModule.ConfirmCommand, ActivateOn: CommandPhase.Started),
                            Claim(function: 1),
                            Claim(function: 2),
                            Claim(function: 3),
                            Claim(function: 4),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: PlayerCommandModule.MoveCommand),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: PlayerCommandModule.LookCommand),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.ButtonSouth, Command: PlayerCommandModule.ConfirmCommand, ActivateOn: CommandPhase.Started),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.Start, Command: PlayerCommandModule.CycleCommand, ActivateOn: CommandPhase.Started),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.Back, Command: EditorCommandModule.EnterCommand, ActivateOn: CommandPhase.Started, Label: "Editor"),
                        ],
                        Label: "Roster"
                    )
                ),
                // The editor group (always compiled in; editor.enter flips the seat's active group onto it).
                .. WorldEditorBindings.Rows(),
            ],
            Contexts: RosterContextRows(),
            Wheels: [
                PlayWheel(),
                .. WorldEditorBindings.Wheels(),
            ]
        );
    }

    /// <summary>The rows every wheel hold page carries beside its own stick routers: the ring-cycle pair on Arrow
    /// Up/Down and D-pad Up/Down (the mouse-less twin of the mouse wheel — Up cycles outward, matching a wheel
    /// notch away from the user), and Tab's release edge firing the commit (see the class remarks on the
    /// chord-latch machinery that makes the release resolve back to this page).</summary>
    /// <returns>The shared hold-page entries.</returns>
    public static BindingPageEntryDefinition[] WheelHoldEntries(string openerSource) => [
        // The aim source is ordinary authoring data. A player layer may replace this source with either stick,
        // a D-pad adapter, or any future Axis2D provider without changing radial code.
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: WorldWheelCommandModule.SelectCommand),
        RingStep(source: InputSources.Keyboard.ArrowUp, direction: 1f),
        RingStep(source: InputSources.Keyboard.ArrowDown, direction: -1f),
        RingStep(source: InputSources.Gamepad.DpadUp, direction: 1f),
        RingStep(source: InputSources.Gamepad.DpadDown, direction: -1f),
        new BindingPageEntryDefinition(Source: openerSource, Command: WorldWheelCommandModule.CommitCommand, ActivateOn: CommandPhase.Completed),
    ];

    // One ring-cycle row: press edge, direction carried as the constant Axis1D value the player.wheel.ring handler
    // reads (the stepped-twin fold — see WorldEditorBindings.PressValue's doctrine comment).
    private static BindingPageEntryDefinition RingStep(string source, float direction) => new(
        Source: source,
        Command: WorldWheelCommandModule.RingCommand,
        Value: CommandValue.Axis(value: direction),
        ActivateOn: CommandPhase.Started,
        Label: ((direction > 0f) ? "Ring+" : "Ring-"),
        Icon: ((direction > 0f) ? "edit.next" : "edit.prev")
    );

    // The play wheel — the engine-default radial the play group presents while Tab holds WheelHoldPageId open.
    // One ring of six bindable acts. Every sector is an ordinary compiled command activation in the seat's lane; the Editor
    // sector carries the editor entry Tab used to bind directly.
    private static BindingWheelDefinition PlayWheel() => new(
        Id: PlayWheelId,
        Group: PlayGroup,
        HoldPages: [WheelHoldPageId],
        Rings: [
            new BindingPageDefinition(
                Id: WheelActRingId,
                Entries: [
                    Sector(command: EditorCommandModule.EnterCommand, label: "Editor", icon: "edit.place"),
                    Sector(command: EditorCommandModule.StatusCommand, label: "Status", icon: "action.target"),
                    Sector(command: TerminalCommandNames.Console, label: "Console", icon: "edit.op"),
                    Sector(command: "player.where", label: "Where", icon: "action.target"),
                    Sector(command: "player.channels", label: "Channels", icon: "edit.link"),
                    Sector(command: "player.disengage", label: "Disengage", icon: "edit.exit"),
                ],
                Label: "Act"
            ),
        ]
    );

    /// <summary>One wheel sector row — a bare command destination plus display metadata, the narrowed page-entry
    /// shape <see cref="BindingWheelDefinition"/> documents.</summary>
    /// <param name="command">The bindable command the sector activates.</param>
    /// <param name="label">The sector's display label.</param>
    /// <param name="icon">The sector's display icon id.</param>
    /// <param name="value">An optional constant activation value; otherwise active Digital.</param>
    /// <returns>The sector row.</returns>
    public static BindingPageEntryDefinition Sector(string command, string label, string icon, CommandValue? value = null) => new(
        Source: null,
        Command: command,
        Label: label,
        Icon: icon,
        Value: value
    );

    // Selects RosterGroup for every roster state short of active — unjoined, claimed, and pending. ACTIVE ships no
    // row: with no row a seat falls through to its requested group (see WorldSeatBindings.DeriveActiveGroup), which
    // is the play group by default. Claimed mapping to the roster group is deliberate: player.confirm's own handler
    // already refuses a claimed slot, so an exclusive claim's refusal is reported through the ordinary confirm path
    // rather than silently swallowed by a channel destination that never fires while pending.
    private static BindingContextDefinition[] RosterContextRows() => [
        new BindingContextDefinition(Family: WorldContextFamilies.Roster, State: WorldContextFamilies.RosterUnjoined, Group: RosterGroup),
        new BindingContextDefinition(Family: WorldContextFamilies.Roster, State: WorldContextFamilies.RosterClaimed, Group: RosterGroup),
        new BindingContextDefinition(Family: WorldContextFamilies.Roster, State: WorldContextFamilies.RosterPending, Group: RosterGroup),
    ];

    // The three movement channel names every shipped world declares identically — the engine default binds the
    // keyboard to these NAMES directly.
    private const string ForwardChannelName = "forward";
    private const string StrafeChannelName = "strafe";
    private const string TurnChannelName = "turn";

    // The base page's keyboard movement rows — each source press-and-release onto a CHANNEL destination with a
    // scale into three movement channels, each fed by two opposing-scale rows. Shared
    // with the Ctrl-held pages (ControlPageEntries) so holding either Control key carries the same movement the base
    // page does, rather than stalling it the way a sparse-trigger-style page (stick routers only) would.
    private static BindingPageEntryDefinition[] KeyboardMovementEntries() => [
        .. HoldReleaseChannel(source: InputSources.Keyboard.Letter(letter: 'w'), channel: ForwardChannelName, scale: 1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.ArrowUp, channel: ForwardChannelName, scale: 1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.Letter(letter: 's'), channel: ForwardChannelName, scale: -1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.ArrowDown, channel: ForwardChannelName, scale: -1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.Letter(letter: 'a'), channel: StrafeChannelName, scale: -1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.ArrowLeft, channel: TurnChannelName, scale: 1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.Letter(letter: 'd'), channel: StrafeChannelName, scale: 1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.ArrowRight, channel: TurnChannelName, scale: -1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.Letter(letter: 'q'), channel: TurnChannelName, scale: 1f),
        .. HoldReleaseChannel(source: InputSources.Keyboard.Letter(letter: 'e'), channel: TurnChannelName, scale: -1f),
    ];

    // The Ctrl-held pages' shared entries: the keyboard movement rows (so a keyboard player keeps moving under
    // either held Control key), the gamepad sticks (so a gamepad player keeps flying under it too), and Ctrl+C's
    // editor.status entry — the default binding proving a native OS-modifier key resolves through this same
    // held-chord machinery.
    private static BindingPageEntryDefinition[] ControlPageEntries() => [
        .. KeyboardMovementEntries(),
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: PlayerCommandModule.MoveCommand),
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: PlayerCommandModule.LookCommand),
        new BindingPageEntryDefinition(Source: InputSources.Keyboard.Letter(letter: 'c'), Command: EditorCommandModule.StatusCommand, ActivateOn: CommandPhase.Started, Label: "Status"),
    ];

    // A source bound to a command on BOTH edges (the HoldRelease pattern): a press-edge entry (default phase, fires on
    // Started/Active) and a release-edge entry (ActivateOn Completed).
    private static BindingPageEntryDefinition[] HoldRelease(string source, string command) => [
        new BindingPageEntryDefinition(Source: source, Command: command),
        new BindingPageEntryDefinition(Source: source, Command: command, ActivateOn: CommandPhase.Completed),
    ];

    // A source bound to a CHANNEL destination on both edges — the channel-generic twin of HoldRelease: a press-edge
    // row and a release-edge row, both carrying the same scale (a digital source's constant activation value; see
    // BindingProfile.Compile's synthesis). Default scale +One.
    private static BindingPageEntryDefinition[] HoldReleaseChannel(string source, string channel, float scale = 1f) => [
        new BindingPageEntryDefinition(Source: source, Channel: new ChannelRef.Name(Value: channel), Scale: scale),
        new BindingPageEntryDefinition(Source: source, Channel: new ChannelRef.Name(Value: channel), Scale: scale, ActivateOn: CommandPhase.Completed),
    ];

    // A function-key claim entry: press edge, carrying the 1-based slot as the constant Axis1D value the
    // player.claim handler reads.
    private static BindingPageEntryDefinition Claim(int function) => new(
        Source: InputSources.Keyboard.Function(number: function),
        Command: PlayerCommandModule.ClaimCommand,
        ActivateOn: CommandPhase.Started,
        Value: CommandValue.Axis(value: function)
    );
}
