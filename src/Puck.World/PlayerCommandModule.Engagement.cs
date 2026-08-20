using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private CommandResult DisengageHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.disengage: expected at most 1 value — an optional player index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.disengage"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var actingPrincipal = context.ActingPrincipal();
        var targetPrincipal = TargetPrincipalFor(index: index);
        // A READ-ONLY peek of the decision Server.WorldEngagement.Dissolve will make — the console echo's source of
        // truth (see WorldEngagement.PeekDissolve's own remarks for why this is safe over loopback). The command
        // below is submitted UNCONDITIONALLY regardless of the peek, so the SERVER's own check is what actually
        // decides (a denied-dissolve attack case must be refused there, never merely by this client choosing not to
        // submit).
        var outcome = m_server.Engagement.PeekDissolve(
            actingPrincipal: actingPrincipal,
            entityIndex: (index - 1),
            targetPrincipal: targetPrincipal
        );

        m_link.SubmitCommand(command: new WorldCommand.DissolveControl(
            EntityIndex: (index - 1),
            Principal: actingPrincipal,
            TargetPrincipal: targetPrincipal
        ));

        if (outcome == ControlOutcome.Denied) {
            return CommandResult.Error(output: $"[player.disengage: {actingPrincipal.Describe()} lacks control over an application p{index} holds — see world.why]");
        }

        // A dissolve drops p{index}'s held device state. This is client-side held state only — a
        // BindingEntryMode.Toggle latch (InputRouter) survives a dissolve on purpose, since rerouting input is not a
        // stop.
        if (
            (outcome == ControlOutcome.Dissolved) &&
            IsSeat(index: index)
        ) {
            m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.ReleaseAllHeld();
        }

        return ((outcome == ControlOutcome.Dissolved)
            ? Echoed(
                args: in args,
                handler: $"[player.disengage: p{index} disengaged]"
            )
            : Echoed(
                args: in args,
                handler: $"[player.disengage: p{index} was not engaged]"
            )
        );
    }
    private CommandResult EngageHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return CommandResult.Error(output: "[player.engage: expected a target (a screen index or body:<n>) — plus an optional capture:on|off and an optional player index]");
        }

        // capture:on|off, when present, is ALWAYS the LAST token — this keeps the target and the (optional) player
        // index at their historical fixed positions (0 and 1) so nothing about the classic screen-engage shape moves.
        var capture = true;
        var tokenCount = args.Count;

        if (
            (tokenCount >= 2) &&
            LooksLikeCaptureToken(token: args[(tokenCount - 1)])
        ) {
            if (!TryParseCapture(
                token: args[(tokenCount - 1)],
                capture: out capture
            )) {
                return CommandResult.Error(output: $"[player.engage: '{args[(tokenCount - 1)].ToString()}' must be capture:on or capture:off]");
            }

            tokenCount--;
        }

        if (!TryParseEngageTarget(
            token: args[0],
            target: out var target
        )) {
            return CommandResult.Error(output: $"[player.engage: target '{args[0].ToString()}' must be a screen index or body:<n>]");
        }

        // The player index (if any) trails the target at token 1 — read directly rather than through
        // WorldArgs.TryParseIndex, which reads the ORIGINAL args by position and would misparse a stripped capture:
        // token sitting at args[1] in the (target, capture) two-token shape (tokenCount == 1, original args.Count == 2)
        // as a malformed player index instead of the absent-token default.
        var index = 1;

        if (tokenCount >= 2) {
            if (
                !args.TryInt(
                index: 1,
                value: out index
            ) ||
                (index < 1) ||
                (index > m_population.Capacity)
            ) {
                return CommandResult.Error(output: $"[player.engage: player index must be an integer 1..{m_population.Capacity}]");
            }
        }

        var player = ((index <= PlayerRoster.MaxSlots)
            ? (m_roster.IsJoined(slot: PlayerRoster.SlotFromDisplay(number: index))
                ? m_server.Body(index: PlayerRoster.SlotFromDisplay(number: index))
                : null)
            : m_population.EntryBody(index: (index - 1))
        );

        if (player is null) {
            var missError = ((index <= PlayerRoster.MaxSlots)
                ? $"[player.engage: player {index} is not joined — see world.players]"
                : $"[player.engage: player {index} is not an active population entry — see world.population]"
            );

            return CommandResult.Error(output: missError);
        }

        // Authority check happens before any mutation, including the auto-insert boot below: it checks the acting
        // principal (the submitter), not the target player's own principal — every seat is pre-seeded Control/all,
        // so checking the target would pass unconditionally. This is a client-side precheck against the server's
        // grant table; the mutation itself re-checks the identical pair atomically in ComposeControl's apply.
        var actingPrincipal = context.ActingPrincipal();

        if (m_server.Engagement.CheckEngage(
            actingPrincipal: actingPrincipal,
            target: target
        ) is { IsAllowed: false } engageVerdict) {
            return CommandResult.Error(output: $"[player.engage: {actingPrincipal.Describe()} cannot control {target.Describe()} ({engageVerdict.DescribeDenial()}) — see world.why]");
        }

        if (target.Kind == GrantSubjectKind.Screen) {
            var screenIndex = target.Value;

            if (FindScreen(screenIndex: screenIndex) is not { } screen) {
                return CommandResult.Error(output: $"[player.engage: no screen {screenIndex} — see world.screens]");
            }

            // Engaging requires the screen to permit engagement, carry a machine to receive input, and — when the
            // route sets a radius — the avatar be within that planar distance of the screen's origin. The radius
            // check here reads the server body's pose in-process (loopback only); a socket transport checks the
            // radius server-side in the engage command instead.
            if (!screen.Route.Engageable) {
                return CommandResult.Error(output: $"[player.engage: screen {screenIndex} is not engageable]");
            }

            // route.autoInsert: engaging an empty engageable screen first boots its selected magazine entry (the "walk
            // over, press the button, the screen lights" gesture is one act, not an insert then an engage).
            // The boot itself is a WorldScreenOp.Select submission
            // through the ordered domain — Server.WorldMachineHost applies it SYNCHRONOUSLY, so the HasMachine check
            // two lines below observes its effect immediately, exactly like the pre-inversion direct binder call did.
            if (
                screen.Route.AutoInsert &&
                !m_screens.HasMachine(index: screenIndex) &&
                m_screens.TryMagazine(
                index: screenIndex,
                magazine: out _,
                selected: out var selected
            )
            ) {
                m_link.SubmitScreenOp(
                    op: new WorldScreenOp.Select(
                        Entry: selected,
                        Index: screenIndex
                    ),
                    principal: actingPrincipal
                );
            }

            if (!m_screens.HasMachine(index: screenIndex)) {
                return CommandResult.Error(output: $"[player.engage: screen {screenIndex} has no machine to control — screen.insert a cart first]");
            }

            if (screen.Route.EngageRadius > 0f) {
                var position = player.FixedPosition;
                var delta = new FixedVector2(
                    X: (position.X - FixedQ4816.FromDouble(value: screen.Origin.X)),
                    Y: (position.Z - FixedQ4816.FromDouble(value: screen.Origin.Z))
                );
                var radius = FixedQ4816.FromDouble(value: screen.Route.EngageRadius);

                if (delta.LengthSquared > (radius * radius)) {
                    return CommandResult.Error(output: string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"[player.engage: p{index} is {((double)delta.Length):0.0}u from screen {screenIndex} — within {screen.Route.EngageRadius:0.0}u to engage (player.pose closer)]"
                    ));
                }
            }
        } else if (m_population.EntryBody(index: target.Value) is null) {
            return CommandResult.Error(output: $"[player.engage: no body {target.Value} — see world.population]");
        }

        // The precheck above already confirmed actingPrincipal holds Control over target on this same thread, so
        // this submission is guaranteed to land; the command re-checks the identical pair atomically server-side in
        // Server.WorldEngagement.Engage. p{index}'s device state (held keys/lanes) is dropped client-side in the
        // same breath as the submission.
        var targetPrincipal = TargetPrincipalFor(index: index);

        m_link.SubmitCommand(command: new WorldCommand.ComposeControl(
            EntityIndex: (index - 1),
            Exclusive: capture,
            Principal: actingPrincipal,
            Target: target,
            TargetPrincipal: targetPrincipal
        ));

        // Only the CLIENT-side held-device image is dropped here — deliberately NOT InputRouter.ClearSlotHeld (the
        // input-layer BindingEntryMode.Toggle latch): engaging reroutes where a seat's held channels are DELIVERED
        // (this body vs. a possessed one), it does not stop the seat, so a toggled-on sprint should still read
        // toggled-on once the route lands. player.stop is the one seam that clears the latch (see its own remarks).
        if (
            capture &&
            IsSeat(index: index)
        ) {
            m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.ReleaseAllHeld();
        }

        return Echoed(
            args: in args,
            handler: $"[player.engage: p{index} routed to {target.Describe()} ({(capture
            ? "capture"
            : "mirror")})]"
        );
    }
    // The declared screen with the given engine index, or null when no screen declares it.
    private WorldScreen? FindScreen(int screenIndex) {
        foreach (var screen in m_definition.Screens) {
            if (screen.Index == screenIndex) {
                return screen;
            }
        }

        return null;
    }
    // Whether a trailing token spells the capture:on|off shape at all — used to decide whether the LAST token is a
    // capture argument (and so must be stripped before the player-index position is read) or genuinely the player
    // index itself.
    private static bool LooksLikeCaptureToken(ReadOnlySpan<char> token) =>
        token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "capture:"
        );
    // The identity an engagement route is recorded under for a 1-based display index — the seat's own claimed
    // identity (PlayerRoster.PrincipalOf, falling back to WorldPrincipal.Seat) for 1..4, or the population's current
    // peer identity for 5..128. Passed explicitly because only the client's roster knows about a claim override;
    // Server.WorldEngagement resolves a body's own principal by index arithmetic alone and has no roster to ask.
    private WorldPrincipal TargetPrincipalFor(int index) {
        return (IsSeat(index: index)
            ? m_roster.PrincipalOf(slot: PlayerRoster.SlotFromDisplay(number: index))
            : m_server.Population.PeerPrincipal(index: (index - 1))
        );
    }
    // Parses a confirmed capture: token's on|off value. Returns false (capture defaulted true) for anything else,
    // so the caller can report the exact malformed token rather than a generic parse failure.
    private static bool TryParseCapture(ReadOnlySpan<char> token, out bool capture) {
        var value = token[8..];

        if (value.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "on"
        )) {
            capture = true;

            return true;
        }

        if (value.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "off"
        )) {
            capture = false;

            return true;
        }

        capture = true;

        return false;
    }
    // Parses player.engage's target token: a bare non-negative integer names a SCREEN (the historical, unchanged
    // shape); "screen:<n>"/"body:<n>" name either explicitly — the SAME grammar
    // world.grant's subject token already uses, so an operator who knows one already knows the other. Any other
    // GrantSubject shape (all/section/profile/composition) is not a legitimate engage target and is rejected.
    private static bool TryParseEngageTarget(ReadOnlySpan<char> token, out GrantSubject target) {
        if (
            GrantSubject.TryParse(
            subject: out target,
            token: token
        ) &&
            (target.Kind is GrantSubjectKind.Screen or GrantSubjectKind.Body)
        ) {
            return true;
        }

        if (
            int.TryParse(
            s: token,
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var screenIndex
        ) &&
            (screenIndex >= 0)
        ) {
            target = GrantSubject.Screen(index: screenIndex);

            return true;
        }

        target = default;

        return false;
    }
}
