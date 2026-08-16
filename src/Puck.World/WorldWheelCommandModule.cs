using System.Globalization;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The radial action menu's verb surface — the three bindable acts the wheel hold pages dispatch
/// (<see cref="RingCommand"/> steps the active ring, the mouse-less twin of the mouse wheel;
/// <see cref="CommitCommand"/> consumes an author-bound release-commit) plus the pipe-assertable
/// <c>world.view.wheel</c> read (the <c>world.view.pointer</c> sibling: live radial presentation state nothing else
/// can echo). The wheel content is authored data — the binding substrate's <c>wheels</c> rows, edited through the
/// ordinary binding layers — and a committed sector returns its compiled activation to the input router, so this module carries no
/// authority of its own. A separate module from <see cref="WorldViewCommandModule"/> to keep every class under its
/// analyzer ceilings.
/// </summary>
/// <remarks>Core-registered for command-vocabulary parity (the document validators must see the same committed
/// vocabulary in every boot shape — <see cref="RingCommand"/>/<see cref="CommitCommand"/> are stock wheel-hold-page
/// rows every group's wheel carries): <see cref="WorldWheelFeed"/> is genuinely presentation-only (it reads the
/// mouse/pointer and viewport state <see cref="WorldBootComposition.AddWorldPresentation"/> alone registers), so it is optional here
/// (default <see langword="null"/> — DI supplies the default rather than throwing headless) and every handler
/// refuses by name at use when it is absent, rather than the module going unregistered.</remarks>
internal sealed class WorldWheelCommandModule(PlayerRoster roster, WorldWheelFeed? feed = null) : ICommandModule {
    /// <summary>The author-bindable explicit cancel act.</summary>
    public const string CancelCommand = "player.wheel.cancel";
    /// <summary>The release-commit act — bound on the wheel hold pages' Tab release edge, and typed as
    /// <c>player.wheel.commit [player]</c> (committing whatever the open wheel currently hovers).</summary>
    public const string CommitCommand = "player.wheel.commit";
    /// <summary>The ring-cycle act — bound on the wheel hold pages (Arrow Up/Down, D-pad Up/Down) with a constant
    /// Axis1D direction, and typed as <c>player.wheel.ring [next|prev] [player]</c>.</summary>
    public const string RingCommand = "player.wheel.ring";
    /// <summary>The author-bindable Axis2D radial selection act.</summary>
    public const string SelectCommand = "player.wheel.select";

    private readonly WorldWheelFeed? m_feed = feed;
    private readonly PlayerRoster m_roster = roster;

    private CommandResult CancelHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresWindowed(verb: CancelCommand);
        }

        int slot;

        if (context.Origin == CommandOrigin.Binding) {
            slot = context.Slot;
        } else if (!WorldArgs.TryParseIndex(
            args: args,
            at: 0,
            fallback: 1,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var player
        )) {
            return CommandResult.Error(output: $"[{CancelCommand}: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
        } else {
            slot = PlayerRoster.SlotFromDisplay(number: player);
        }

        feed.Revoke(slot: slot);

        return new CommandResult(Output: $"[{CancelCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} cancelled]");
    }
    private CommandResult CommitHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresWindowed(verb: CommitCommand);
        }

        int slot;

        if (context.Origin == CommandOrigin.Binding) {
            slot = context.Slot;

            // The router's synthesized focus-loss cancellation is the ONE non-release edge that reaches this
            // handler (the bound row's ActivateOn gate passes only the real Completed edge and the Canceled
            // synthesis) — an alt-tab mid-hold revokes silently, never commits.
            if (context.Phase == CommandPhase.Canceled) {
                feed.Revoke(slot: slot);

                return CommandResult.None;
            }
        } else {
            if (!WorldArgs.TryParseIndex(
                args: args,
                at: 0,
                fallback: 1,
                max: PlayerRoster.MaxSlots,
                min: 1,
                value: out var player
            )) {
                return CommandResult.Error(output: $"[{CommitCommand}: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
            }

            slot = PlayerRoster.SlotFromDisplay(number: player);
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
            handler: $"[world.view.wheel: player={PlayerRoster.DisplayNumber(slot: status.Slot)} open=true id={status.Id} group={status.Group} rings={status.RingCount} active={(status.ActiveRing + 1)} '{status.ActiveRingLabel}' hover={hover} pointer={status.PointerSelection} ringSelection={status.RingSelection} placement={status.Placement} center={status.Center.X:0.#},{status.Center.Y:0.#}{(status.CenterKnown
            ? string.Empty
            : " (unanchored)")}]"
        );
    }
    // The headless refusal shared by every handler below — named per verb so a scripted caller reads exactly which
    // act was refused, never a generic "unavailable".
    private CommandResult RequiresWindowed(string verb) =>
        CommandResult.Error(output: $"[{verb}: requires a windowed boot — headless registers this verb for vocabulary parity only]");
    private CommandResult RingHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresWindowed(verb: RingCommand);
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

            if (!WorldArgs.TryParseIndex(
                args: args,
                at: playerAt,
                fallback: 1,
                max: PlayerRoster.MaxSlots,
                min: 1,
                value: out var player
            )) {
                return CommandResult.Error(output: $"[{RingCommand}: expected [next|prev] [player], player an integer 1..{PlayerRoster.MaxSlots}]");
            }

            slot = PlayerRoster.SlotFromDisplay(number: player);
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
            return RequiresWindowed(verb: SelectCommand);
        }

        if (context.Origin != CommandOrigin.Binding) {
            return CommandResult.Error(output: $"[{SelectCommand}: this Axis2D act is driven by an authored binding]");
        }

        feed.Select(
            slot: context.Slot,
            axis: context.Value.AsAxis2D
        );

        return CommandResult.None;
    }
    private CommandResult ViewHandler(CommandContext context, WireArgs args) {
        if (m_feed is not { } feed) {
            return RequiresWindowed(verb: "world.view.wheel");
        }

        if (args.Count == 0) {
            return new CommandResult(Output: Describe(status: feed.Status));
        }

        if (args.Count > 1) {
            return CommandResult.Error(output: $"[world.view.wheel: too many arguments — expected [<player>], player an integer 1..{PlayerRoster.MaxSlots}]");
        }

        if (!WorldArgs.TryParseIndex(
            args: args,
            at: 0,
            fallback: 1,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var player
        )) {
            return CommandResult.Error(output: $"[world.view.wheel: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        return new CommandResult(Output: Describe(status: feed.StatusFor(slot: PlayerRoster.SlotFromDisplay(number: player))));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SelectCommand,
            description: "Aims the open radial for the binding's seat from an authored Axis2D source. Bind either stick (or another Axis2D provider) on each radial hold page; no gamepad control is hard-coded by the presenter.",
            handler: SelectHandler,
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: RingCommand,
            description: "Cycles an Explicit open radial menu's ACTIVE ring (wrapping): player.wheel.ring [next|prev] [player] (player 1..4, default 1). Authors may bind any digital source with a constant Axis1D direction; mouse-wheel motion cycles the pointer seat too. REFUSES when no radial is open or its ring selection is neutral-relative Excursion.",
            handler: RingHandler,
            // Every binding row targeting this verb carries a constant Axis1D value — declared so
            // BindingVocabularyCheck admits the rows (the editor.cam.speed precedent).
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CommitCommand,
            description: "Commits the open radial menu: player.wheel.commit [player] (player 1..4, default 1) — queues the hovered sector's compiled activation in that seat's deterministic lane, or cancels when nothing is selected. Authors decide which opener releases commit. A release is deferred while another source still holds the same radial open.",
            handler: CommitHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CancelCommand,
            description: "Cancels the open radial without activating a sector: player.wheel.cancel [player]. Bind any desired key or gamepad button on a radial hold page.",
            handler: CancelHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.wheel",
            description: "Echoes a radial's last composed frame: world.view.wheel [player] — without a player reads the pointer seat; otherwise reads seat 1..4. Reports open state, radial id/group, rings, active ring, hovered sector or reason, authored pointer/ring-selection and placement policy, and hub anchor.",
            handler: ViewHandler,
            routing: CommandRouting.Immediate
        );
    }
}
