using System.Globalization;
using Puck.Commands;
using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The radial action menu's verb surface — the bindable acts the wheel hold pages dispatch
/// (<see cref="RingCommand"/> steps the active ring, the mouse-less twin of the mouse wheel;
/// <see cref="CommitCommand"/> consumes an author-bound release-commit) plus the pipe-assertable
/// <c>world.view.wheel</c> read (the <c>world.view.pointer</c> sibling: live radial presentation state nothing else
/// can echo). The wheel content is authored data — the binding substrate's <c>wheels</c> rows, edited through the
/// ordinary binding layers — and a committed sector returns its compiled activation to the input router, so this module carries no
/// authority of its own. A separate module from <see cref="WorldViewCommandModule"/> to keep every class under its
/// analyzer ceilings.
/// </summary>
/// <remarks>Core-registered: authored bindings, ring selection and directional commits work without a
/// renderer. The feed's optional pointer, viewport and overlay adapters add spatial presentation.</remarks>
internal sealed class WorldWheelCommandModule(PlayerRoster roster, WorldWheelFeed? feed = null) : ICommandModule {
    /// <summary>The author-bindable explicit cancel act.</summary>
    public const string CancelCommand = Puck.World.Client.WorldWheelCommandNames.CancelCommand;
    /// <summary>The release-commit act — bound on the wheel hold pages' Tab release edge, and typed as
    /// <c>player.wheel.commit [player]</c> (committing whatever the open wheel currently hovers).</summary>
    public const string CommitCommand = Puck.World.Client.WorldWheelCommandNames.CommitCommand;
    /// <summary>The ring-cycle act — bound on the wheel hold pages (Arrow Up/Down, D-pad Up/Down) with a constant
    /// Axis1D direction, and typed as <c>player.wheel.ring [next|prev] [player]</c>.</summary>
    public const string RingCommand = Puck.World.Client.WorldWheelCommandNames.RingCommand;
    /// <summary>The author-bindable Axis2D radial selection act.</summary>
    public const string SelectCommand = Puck.World.Client.WorldWheelCommandNames.SelectCommand;
    /// <summary>The relative Axis2D ring-scroll act; vertical motion is measured in notches.</summary>
    public const string ScrollCommand = "player.wheel.scroll";

    private readonly WorldWheelFeed? m_feed = feed;
    private readonly PlayerRoster m_roster = roster;
    private static readonly CommandForms PlayerWheelRingForms = CommandForms.Of(verb: RingCommand, "[next|prev] [player]");
    private static readonly CommandForms PlayerWheelCommitForms = CommandForms.Of(verb: CommitCommand, "[player]");
    private static readonly CommandForms PlayerWheelCancelForms = CommandForms.Of(verb: CancelCommand, "[player]");
    private static readonly CommandForms WorldViewWheelForms = CommandForms.Of(verb: "world.view.wheel", "[<player>]");

    private CommandResult CancelHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresFeed(verb: CancelCommand);
        }

        var (slot, error) = SeatCommandArgs.ResolveJoinedSeat(
            args: in args,
            at: 0,
            context: context,
            roster: m_roster,
            verb: CancelCommand
        );

        if (error is { } refusal) {
            return refusal;
        }

        feed.Revoke(slot: slot);

        return new CommandResult(Output: $"[{CancelCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} cancelled]");
    }
    private CommandResult CommitHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresFeed(verb: CommitCommand);
        }

        var (slot, error) = SeatCommandArgs.ResolveJoinedSeat(
            args: in args,
            at: 0,
            context: context,
            defaultSlot: 0,
            roster: m_roster,
            verb: CommitCommand
        );

        if (error is { } refusal) {
            return refusal;
        }

        // The router's synthesized focus-loss cancellation is the ONE non-release edge that reaches this handler
        // (the bound row's ActivateOn gate passes only the real Completed edge and the Canceled synthesis) — an
        // alt-tab mid-hold revokes silently, never commits.
        if (context.Phase == CommandPhase.Canceled) {
            feed.Revoke(slot: slot);

            return CommandResult.None;
        }

        var outcome = feed.Commit(slot: slot);

        return outcome.Status switch {
            BindingWheelCommitStatus.NotArmed => CommandResult.Error(output: $"[{CommitCommand}: refused — no radial commit is armed for seat {PlayerRoster.DisplayNumber(slot: slot)}]"),
            BindingWheelCommitStatus.Deferred => new CommandResult(Output: $"[{CommitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} kept open by another bound source]"),
            BindingWheelCommitStatus.Cancelled => new CommandResult(Output: $"[{CommitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} cancelled ({outcome.Reason})]"),
            BindingWheelCommitStatus.Unregistered => CommandResult.Error(output: $"[{CommitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} refused — sector command '{outcome.Command}' is unregistered]"),
            BindingWheelCommitStatus.Dispatched => new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[{CommitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} ring {(outcome.Ring + 1)} sector {(outcome.Sector + 1)} '{outcome.Label}' -> {outcome.Command}]"
        )),
            _ => CommandResult.Error(output: $"[{CommitCommand}: refused — invalid radial commit outcome]"),
        };
    }
    private static string Describe(WorldWheelStatus status) {
        if (!status.Open) {
            return $"[world.view.wheel: player={PlayerRoster.DisplayNumber(slot: status.Slot)} open=false]";
        }

        var hover = ((status.HoverSector >= 0)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"sector {(status.HoverSector + 1)} '{status.HoverLabel}' -> {status.HoverCommand}"
            )
            : status.HoverReason
        );

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.view.wheel: player={PlayerRoster.DisplayNumber(slot: status.Slot)} open=true id={status.Id} group={status.Group} rings={status.RingCount} active={(status.ActiveRing + 1)} '{status.ActiveRingLabel}' hover={hover} pointer={status.PointerSelection} ringSelection={status.RingSelection} selectorDeadZone={status.SelectorDeadZone:0.###} switchFraction={status.SwitchFraction:0.###} neutralGrace={status.SelectionGraceSeconds:0.###}s placement={status.Placement} center={status.Center.X:0.#},{status.Center.Y:0.#}{(status.CenterKnown
            ? string.Empty
            : " (unanchored)")}]"
        );
    }
    // The missing-feed refusal shared by every handler below — named per verb so a scripted caller reads which
    // act was refused, never a generic "unavailable".
    private CommandResult RequiresFeed(string verb) =>
        CommandResult.Error(output: $"[{verb}: wheel input feed is unavailable]");
    private CommandResult RingHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresFeed(verb: RingCommand);
        }

        int slot;
        int direction;

        if (context.Origin == CommandOrigin.Binding) {
            // A bound dispatch: the seat is the pressing device's, the direction the row's constant Axis1D value
            // (the stepped-twin fold — never a sibling verb per direction).
            slot = context.Slot;
            direction = ((context.Value.AsAxis1D < 0f)
                ? -1
                : 1
            );
        } else {
            var playerAt = 0;

            direction = 1;

            if (args.Count > 0) {
                if (args.Is(
                    index: 0,
                    value: "prev"
                )) {
                    direction = -1;
                    playerAt = 1;
                } else if (args.Is(
                    index: 0,
                    value: "next"
                )) {
                    playerAt = 1;
                }
                // Anything else is left for the player-index parse below, whose own refusal names the grammar.
            }

            var (resolved, error) = SeatCommandArgs.ResolveJoinedSeat(
                args: in args,
                at: playerAt,
                context: context,
                defaultSlot: 0,
                roster: m_roster,
                verb: RingCommand
            );

            if (error is { } refusal) {
                return refusal;
            }

            slot = resolved;
        }

        if (!feed.TryCycleRing(
            activeRing: out var activeRing,
            direction: direction,
            excursionControlled: out var excursionControlled,
            ringCount: out var ringCount,
            ringLabel: out var ringLabel,
            slot: slot
        )) {
            if (excursionControlled) {
                return CommandResult.Error(output: $"[{RingCommand}: refused — seat {PlayerRoster.DisplayNumber(slot: slot)} radial selects rings from authored neutral-relative excursion]");
            }

            return CommandResult.Error(output: $"[{RingCommand}: refused — no radial is open for seat {PlayerRoster.DisplayNumber(slot: slot)}]");
        }

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[{RingCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} ring {(activeRing + 1)}/{ringCount} '{ringLabel}']"
        ));
    }
    private CommandResult SelectHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresFeed(verb: SelectCommand);
        }

        if (context.Origin != CommandOrigin.Binding) {
            return CommandResult.Error(output: $"[{SelectCommand}: this Axis2D act is driven by an authored binding]");
        }

        feed.Select(
            slot: context.Slot,
            axis: context.Value.AsAxis2D,
            relative: ((context.Source is { } source) && InputSourceVocabulary.IsRelative(sourceId: source))
        );

        return CommandResult.None;
    }
    private CommandResult ViewHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresFeed(verb: "world.view.wheel");
        }

        if (args.Count == 0) {
            return new CommandResult(Output: Describe(status: feed.Status));
        }

        if (args.Count > 1) {
            return WorldViewWheelForms.Refuse(got: args[1].ToString());
        }

        var (slot, error) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: "world.view.wheel"
        );

        if (error is { } refusal) {
            return refusal;
        }

        return new CommandResult(Output: Describe(status: feed.StatusFor(slot: slot)));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ScrollCommand,
            description: "Feeds relative Axis2D vertical notches to the open radial's explicit ring selector. Fractional notches accumulate for this gesture; a closed wheel consumes nothing.",
            handler: (context, args) => {
                if (context.Origin != CommandOrigin.Binding) {
                    return CommandResult.Error(output: "[player.wheel.scroll: driven by an authored Axis2D binding]");
                }
                m_feed?.Scroll(slot: context.Slot, notches: context.Value.AsAxis2D.Y);
                return CommandResult.None;
            },
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SelectCommand,
            description: "Aims the open radial for the binding's seat from an authored Axis2D source — a stick's deflection read as-is, a relative source (mouse.motion) accumulated from the moment the radial opened. Bind either on each radial hold page; nothing is hard-coded by the presenter.",
            handler: SelectHandler,
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: RingCommand,
            description: $"Cycles an Explicit open radial menu's ACTIVE ring (wrapping): {PlayerWheelRingForms.Spelling} (player 1..4, default 1). Authors may bind any digital source with a constant Axis1D direction; mouse-wheel motion cycles the pointer seat too. REFUSES when no radial is open or its ring selection is neutral-relative Excursion.",
            forms: PlayerWheelRingForms,
            handler: RingHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared so
            // BindingVocabularyCheck admits the rows (the world.row.step delta-fold precedent).
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CommitCommand,
            description: $"Commits the open radial menu: {PlayerWheelCommitForms.Spelling} (player 1..4, default 1) — queues the hovered sector's compiled activation in that seat's deterministic lane, or cancels when nothing is selected. Authors decide which opener releases commit. A release is deferred while another source still holds the same radial open.",
            forms: PlayerWheelCommitForms,
            handler: CommitHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CancelCommand,
            description: $"Cancels the open radial without activating a sector: {PlayerWheelCancelForms.Spelling}. Bind any desired key or gamepad button on a radial hold page.",
            forms: PlayerWheelCancelForms,
            handler: CancelHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.wheel",
            description: $"Echoes a radial's last composed frame: {WorldViewWheelForms.Spelling} — without a player reads the pointer seat; otherwise reads seat 1..4. Reports open state, radial id/group, rings, active ring, hovered sector or reason, authored pointer/ring-selection and placement policy, and hub anchor.",
            forms: WorldViewWheelForms,
            handler: ViewHandler,
            routing: CommandRouting.Immediate
        );
    }
}
