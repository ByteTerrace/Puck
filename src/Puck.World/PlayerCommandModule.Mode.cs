using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private static readonly CommandForms PlayerBuildForms = CommandForms.Of(verb: WorldEditorBindings.ToggleCommand, SeatForm);
    private static readonly CommandForms PlayerFrameForms = CommandForms.Of(verb: FrameCommand, SeatForm);
    private static readonly CommandForms PlayerModeForms = CommandForms.Of(verb: ModeCommand, "<family> <state> [seat]", "<family> [seat]");
    private static readonly CommandForms PlayerCameraForms = CommandForms.Of(verb: CameraCommand, SeatForm);

    private CommandResult ModeHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return PlayerModeForms.Refuse(args: in args);
        }

        var family = args[0].ToString();

        if (args.Count == 1) {
            var (readSlot, readError) = SeatCommandArgs.ResolveSlot(
                args: in args,
                at: 1,
                context: context,
                verb: ModeCommand
            );

            if (readError is { } resolveReadError) {
                return resolveReadError;
            }

            return ReadMode(
                family: family,
                slot: readSlot
            );
        }

        // The two-token fold: an integer 1..MaxSlots second token reads that seat back; anything else is a state
        // for the resolved seat. A state named like a seat index stays reachable through the three-token form.
        if (
            (args.Count == 2) &&
            WorldArgs.TryParseIndex(
            args: args,
            at: 1,
            fallback: null,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var readSeat
        )
        ) {
            return ReadMode(
                family: family,
                slot: PlayerRoster.SlotFromDisplay(number: readSeat)
            );
        }

        var state = args[1].ToString();

        var (slot, error) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 2,
            context: context,
            verb: ModeCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_seatBindings.TryResolveMode(
            family: family,
            slot: slot
        ) is not { } modeFamily) {
            return CommandResult.Error(output: $"[{ModeCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)}'s document declares no family '{family}']");
        }

        WorldSeatModeState? targetState = null;

        foreach (var candidate in modeFamily.States) {
            if (string.Equals(
                a: candidate.Name,
                b: state,
                comparisonType: StringComparison.Ordinal
            )) {
                targetState = candidate;

                break;
            }
        }

        if (targetState is null) {
            var admissible = string.Join(
                separator: ", ",
                values: modeFamily.States.Select(selector: static candidate => candidate.Name)
            );

            return CommandResult.Error(output: $"[{ModeCommand}: family '{family}' has no state '{state}' — {admissible}]");
        }

        return ApplyMode(
            context: context,
            modeFamily: modeFamily,
            slot: slot,
            targetState: targetState,
            verb: ModeCommand
        );
    }
    // The one mode flip: it publishes the seat's new state and, on a camera-target edge, composes or dissolves the
    // camera control application under the actor's own Drive over the target body. player.mode reaches it by name;
    // player.camera reaches it by resolving the family/state pair itself, so both compose the identical application.
    private CommandResult ApplyMode(CommandContext context, WorldSeatModeFamily modeFamily, WorldSeatModeState targetState, int slot, string verb) {
        var family = modeFamily.Name;

        var authoritative = m_seatBindings.IsAuthoritativeMode(slot, family);
        if (authoritative && seatRouter.TryRoute(slot: slot) is { } inputRoute) {
            inputRoute.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.SelectInputState(context.Principal,
                inputRoute.EntityIndex, m_roster.PrincipalOf(slot: slot), family, targetState.Name, context.Activation));
            return SeatCommandArgs.Echo(detail: $"'{family}' requested '{targetState.Name}'", slot: slot, verb: verb);
        }
        if (authoritative) {
            return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no authority route]");
        }

        m_seatBindings.SetContextState(
            family: family,
            slot: slot,
            state: targetState.Name
        );

        return SeatCommandArgs.Echo(
            detail: $"'{family}' = '{targetState.Name}'",
            slot: slot,
            verb: verb
        );
    }
    // The no-token Free Cam toggle a wheel sector or a pad chord fires. It resolves the seat's own camera-targeting
    // family/state from the routed document (never a hard-coded family name) and flips between that state and the
    // family's authored default, then runs the SAME ApplyMode path player.mode does.
    private CommandResult CameraHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return PlayerCameraForms.Refuse();
        }

        var (slot, error) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: CameraCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_seatBindings.TryResolveCameraMode(slot: slot) is not { } camera) {
            return CommandResult.Error(output: $"[{CameraCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)}'s document declares no seatModes state targeting '{WorldSeatModeState.CameraTarget}']");
        }

        var published = m_seatBindings.ModeState(
            family: camera.Family.Name,
            slot: slot
        );
        var leaving = string.Equals(
            a: published,
            b: camera.State.Name,
            comparisonType: StringComparison.Ordinal
        );
        WorldSeatModeState? targetState = null;

        foreach (var candidate in camera.Family.States) {
            if (string.Equals(
                a: candidate.Name,
                b: (leaving
                ? camera.Family.DefaultState
                : camera.State.Name),
                comparisonType: StringComparison.Ordinal
            )) {
                targetState = candidate;

                break;
            }
        }

        if (targetState is null) {
            return CommandResult.Error(output: $"[{CameraCommand}: family '{camera.Family.Name}' has no state '{camera.Family.DefaultState}' to fall back to]");
        }

        return ApplyMode(
            context: context,
            modeFamily: camera.Family,
            slot: slot,
            targetState: targetState,
            verb: CameraCommand
        );
    }
    // The frame toggle a key or button fires: the seat's view of the screen it engages switches between the framed
    // picture and the other view (the held view of a carried screen, else the room). Presentation only.
    private CommandResult FrameHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return PlayerFrameForms.Refuse();
        }

        var (slot, error) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: FrameCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }
        if (!m_anchor.ToggleFrame(slot: slot)) {
            return CommandResult.Error(output: $"[{FrameCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} engages no screen]");
        }

        return SeatCommandArgs.Echo(
            detail: $"view = {(m_anchor.ScreenFramed(slot: slot)
                ? "picture"
                : (m_anchor.HoldView(slot: slot)
                ? "held"
                : "room"))}",
            slot: slot,
            verb: FrameCommand
        );
    }
    // The no-token build toggle a key or button fires: the seat's editor family flips between play and build through
    // the same ApplyMode player.mode editor takes. A seat whose principal may not change the placements still builds,
    // and the echo says it is read-only, as its refused edits will.
    private CommandResult BuildHandler(CommandContext context, WireArgs args) {
        var (slot, error) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: WorldEditorBindings.ToggleCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        var target = (m_seatBindings.IsBuilding(slot: slot)
            ? WorldContextFamilies.EditorPlay
            : WorldContextFamilies.EditorBuild);
        var result = ApplyMode(
            context: context,
            modeFamily: WorldSeatBindings.EditorFamily,
            slot: slot,
            targetState: WorldSeatBindings.EditorFamily.States.First(predicate: state => (state.Name == target)),
            verb: WorldEditorBindings.ToggleCommand
        );

        return ((m_seatBindings.IsBuilding(slot: slot) && !m_server.Grants.Allows(
            capability: WorldCapability.Mutate,
            principal: context.Principal,
            subject: GrantSubject.Section(section: WorldSection.Placements)
        ).IsAllowed)
            ? (result with { Output = $"{result.Output}\n[{WorldEditorBindings.ToggleCommand}: read-only — {context.Principal.Describe()} cannot mutate placements]" })
            : result);
    }
    private IEnumerable<CommandDefinition> ModeVerbs() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: WorldEditorBindings.ToggleCommand,
            description: $"Flips a seat between playing and building: {PlayerBuildForms.Spelling} (seat 1..{PlayerRoster.MaxSlots}, default 1), the same flip player.mode editor build|play makes. While a seat builds, the world's authored context row for the editor family selects its build page (the standard's: the toggle, the grid and snapping, placing, and nudging and turning the seat's current placement) and its view draws its grid. A seat whose principal may not mutate placements still builds, and the echo marks it read-only.",
            forms: PlayerBuildForms,
            handler: BuildHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: FrameCommand,
            description: $"Switches a seat's view of the screen it is engaged on: {PlayerFrameForms.Spelling} (seat 1..{PlayerRoster.MaxSlots}, default 1). The framed picture (the screen's picture alone, as an emulator shows it) and the other view trade places: on a screen a body carries whose carry authors a camera (the Humble Gaming Brick in the Moth's hands) the engagement starts on the view that shows the body holding it and the toggle goes to the picture and back, and on any other screen it starts on the picture and the toggle goes back to the room. The choice lasts until the seat leaves the screen, and the device's own controls play in both views. Echoes [player.frame: p<n> view = picture|held|room]; refused by name when the seat engages no screen. Presentation only: nothing reaches the simulation.",
            forms: PlayerFrameForms,
            handler: FrameHandler,
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: CameraCommand,
            forms: PlayerCameraForms,
            description: $"Toggles a seat's Free Cam: {PlayerCameraForms.Spelling} (seat 1..{PlayerRoster.MaxSlots}, default 1). It resolves the seat's own seatModes state whose target is \"camera\" and flips between that state and the family's default, so a wheel sector or a pad chord composes exactly what player.mode <family> <state> composes — the seat possesses its declared camera body through the ordinary ComposeControl door, its own body intent diverting to Idle while the camera body's pose becomes what the seat perceives, sees, and hears through (see views.cameraRig). Refused by name when the world declares no camera-targeting state.",
            handler: CameraHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: ModeCommand,
            forms: PlayerModeForms,
            description: $"Flips a seat's published state within an AUTHORED per-seat mode family (see the document's seatModes section): {PlayerModeForms.Spelling} (seat 1..4, default 1). The family-only form reads back the seat's current state (a two-token line whose second token is an integer 1..4 reads that SEAT; a state named like a seat index needs the explicit three-token form). A state whose family declares target: \"camera\" composes the camera control application: the seat possesses its declared camera body through the ordinary ComposeControl door (the same possession primitive body.engage uses) — its own body intent diverts to Idle while the camera body's pose becomes what the seat perceives, sees, and hears through (see views.cameraRig); leaving such a state disengages, restoring the seat's own body. An unknown family or state is refused by name, naming every admissible sibling.",
            handler: ModeHandler,
            routing: CommandRouting.Simulation
        );
    }
    private CommandResult ReadMode(int slot, string family) {
        if (m_seatBindings.TryResolveMode(
            family: family,
            slot: slot
        ) is null) {
            return CommandResult.Error(output: $"[{ModeCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)}'s document declares no family '{family}']");
        }

        var state = (m_seatBindings.ModeState(
            family: family,
            slot: slot
        ) ?? "(unpublished)");

        return SeatCommandArgs.Echo(
            detail: $"'{family}' = '{state}'",
            slot: slot,
            verb: ModeCommand
        );
    }
}
