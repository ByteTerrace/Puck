using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    // The population placement id backing seat <paramref name="slot"/>'s camera body. A camera-capable world
    // authors one inhabited placement per local seat it wants Free Cam over (standard.world.json's
    // "camera-seat-<n>" rows); WorldClient.TryInhabitantBody resolves the placement's CURRENT entity index rather
    // than a baked constant, since an inhabited body's table slot is not authored — it is wherever
    // ReconcileInhabitants placed it this boot.
    private static string CameraPlacementId(int slot) => $"{WorldSeatModeState.CameraPlacementIdPrefix}{slot}";
    // Composes the camera control application: possesses the seat's designated camera body through the SAME
    // ComposeControl/Control(+per-tick Drive) gated path any other possession target uses (Server.WorldEngagement) — never a
    // bespoke authority check. Exclusive composition drops the seat's own-body application for the duration, exactly like an ordinary
    // vehicle possession; WorldPerceptionAnchor then swaps the seat's camera eye/audio listener/HUD bindings onto the
    // camera body as a side effect of the SAME application set, not a second mechanism. Refuses (mutating nothing) when the
    // world declares no camera body for this seat, or the server's own Control check denies the actor — the ModeHandler
    // caller has already checked Drive over the target SEAT's own body; this is the separate Control check the route
    // itself requires.
    private bool ActivateCameraApplication(WorldPrincipal actingPrincipal, int slot) {
        if (!m_client.TryInhabitantBody(
            index: out var cameraBody,
            placementId: CameraPlacementId(slot: slot)
        )) {
            return false;
        }

        var target = GrantSubject.Body(index: cameraBody);

        if (m_server.Engagement.CheckEngage(
            actingPrincipal: actingPrincipal,
            target: target
        ) is { IsAllowed: false }) {
            return false;
        }

        // The seat's own Drive grant over its camera body — minted here rather than authored in the document,
        // because an inhabited placement's entity index is resolved at runtime (ReconcileInhabitants places it),
        // never a fixed body:<n> a document `grants` row could name. A seat cannot grant a subject that is not its
        // own body (HoldsForAdministration), so this rides under Console, exactly as an admission's own Drive
        // template does. Idempotent — re-entering camera mode re-grants the identical row.
        m_link.SubmitGrant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Principal: WorldPrincipal.Seat(slot: slot),
                Capability: WorldCapability.Drive,
                Subject: target,
                Exclusive: false
            )
        );

        m_link.SubmitCommand(command: new WorldCommand.ComposeControl(
            EntityIndex: slot,
            Exclusive: true,
            Principal: actingPrincipal,
            Target: target,
            TargetPrincipal: WorldPrincipal.Seat(slot: slot)
        ));

        return true;
    }
    // Dissolves the camera control application through the ordinary DissolveControl door — the avatar resumes driving
    // itself and the perceived body/camera eye/audio listener fall back to it the instant WorldEngagement restores the
    // own-body application, mirroring ActivateCameraApplication's own single mechanism.
    private void DeactivateCameraApplication(WorldPrincipal actingPrincipal, int slot) {
        WorldCameraApplication.Deactivate(
            actingPrincipal: actingPrincipal,
            link: m_link,
            slot: slot
        );
    }
    private static bool IsCameraTarget(WorldSeatModeState? state) => string.Equals(
        a: state?.Target,
        b: WorldSeatModeState.CameraTarget,
        comparisonType: StringComparison.Ordinal
    );
    private CommandResult ModeHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return CommandResult.Error(output: $"[{ModeCommand}: expected <family> <state> [seat], or <family> [seat] to read back]");
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
        var previousStateName = m_seatBindings.ModeState(
            family: family,
            slot: slot
        );
        WorldSeatModeState? previousState = null;

        if (previousStateName is not null) {
            foreach (var candidate in modeFamily.States) {
                if (string.Equals(
                    a: candidate.Name,
                    b: previousStateName,
                    comparisonType: StringComparison.Ordinal
                )) {
                    previousState = candidate;

                    break;
                }
            }
        }

        var wasCamera = IsCameraTarget(state: previousState);
        var isCamera = IsCameraTarget(state: targetState);

        // Composing or dissolving the camera control application possesses/releases the TARGET body — a Drive
        // authority act. Gate it on the ACTOR's Drive over the target body (the same grant the server's own
        // possession door checks), so a session holding Drive over only its own body cannot idle another seat's
        // body or hijack its camera application by naming a trailing [seat]. A pure non-camera flip is presentation
        // only and needs no such authority.
        if (wasCamera != isCamera) {
            var actingPrincipal = context.ActingPrincipal();

            if (!m_server.Grants.Allows(
                principal: actingPrincipal,
                capability: WorldCapability.Drive,
                subject: GrantSubject.Body(index: slot)
            ).IsAllowed) {
                return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} — {actingPrincipal.Describe()} cannot drive body:{slot} to compose the camera control application]");
            }

            if (wasCamera) {
                DeactivateCameraApplication(
                    actingPrincipal: actingPrincipal,
                    slot: slot
                );
            } else if (!ActivateCameraApplication(
                actingPrincipal: actingPrincipal,
                slot: slot
            )) {
                return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} — no camera body available (declare a '{CameraPlacementId(slot: slot)}' inhabited placement, or {actingPrincipal.Describe()} lacks Control over it — see world.why)]");
            }
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
            return CommandResult.Error(output: $"[{CameraCommand}: expected [seat]]");
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
    private IEnumerable<CommandDefinition> ModeVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CameraCommand,
            description: $"Toggles a seat's Free Cam: player.camera [seat] (seat 1..{PlayerRoster.MaxSlots}, default 1). It resolves the seat's own seatModes state whose target is \"camera\" and flips between that state and the family's default, so a wheel sector or a pad chord composes exactly what player.mode <family> <state> composes — the seat possesses its declared camera body through the ordinary ComposeControl door, its own body intent diverting to Idle while the camera body's pose becomes what the seat perceives, sees, and hears through (see views.cameraRig). Refused by name when the world declares no camera-targeting state.",
            handler: CameraHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: ModeCommand,
            description: "Flips a seat's published state within an AUTHORED per-seat mode family (see the document's seatModes section): player.mode <family> <state> [seat] (seat 1..4, default 1). player.mode <family> [seat] reads back the seat's current state (a two-token line whose second token is an integer 1..4 reads that SEAT; a state named like a seat index needs the explicit three-token form). A state whose family declares target: \"camera\" composes the camera control application: the seat possesses its declared camera body through the ordinary ComposeControl door (the same possession primitive body.engage uses) — its own body intent diverts to Idle while the camera body's pose becomes what the seat perceives, sees, and hears through (see views.cameraRig); leaving such a state disengages, restoring the seat's own body. An unknown family or state is refused by name, naming every admissible sibling.",
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
