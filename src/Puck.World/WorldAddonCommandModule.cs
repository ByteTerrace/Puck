using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Scripting;
using Puck.World.Addons;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The live addon-runtime verb surface — <c>world.addon.mount</c> / <c>world.addon.unmount</c> (the ordered-domain
/// lifecycle submissions, see <see cref="Protocol.WorldAddonLifecycle"/>), <c>world.addon.reload</c> /
/// <c>world.addon.enable</c> / <c>world.addon.disable</c>, the in-session lifecycle control plane
/// <see cref="WorldAddonRuntime.Reload"/> / <see cref="WorldAddonRuntime.SetEnabled"/> expose over the pipe
/// (<c>AddonHost.Reload</c> wired to a verb), plus
/// <c>world.addons</c>, the per-guest cost surface (fuel consumed this tick and cumulatively, for
/// degradation observability). A SEPARATE module from <see cref="WorldGrantCommandModule"/> to keep
/// each class under its analyzer ceilings.
/// </summary>
/// <remarks><b>Mount/unmount travel the ordered domain.</b> They route <see cref="CommandRouting.Simulation"/>
/// and BUFFER to the tick boundary through <c>WorldServer.EnqueueAddonLifecycle</c> — the SAME door a document
/// mutation drains through — rather than applying synchronously in this handler; the server's own loud accept/
/// reject line prints when the buffered op applies, and the Simulation routing's stdin drain barrier still makes a
/// following <c>world.addons</c> read observe the settled state. Server-side gated on
/// <see cref="WorldCapability.Mutate"/> over <see cref="GrantSubject.Section"/>(<see cref="WorldSection.Addons"/>)
/// against the envelope's own principal, checked BEFORE the runtime is touched — the same section the document-side
/// addon rows (<c>UpsertAddon</c>/<c>RemoveAddon</c>) are gated on. They ALSO cross <see cref="LoopbackTransport.AddonLifecycleTap"/>
/// and are captured on the replay tape through the shared addon-lifecycle leaf codec — see
/// <see cref="Protocol.WorldAddonLifecycle"/> — so <see cref="RefuseIfArmed"/> deliberately does NOT apply to them:
/// a live mount/unmount during an active <c>replay.record</c> is no longer invisible to the tape, it is exactly
/// what the tape now exists to reproduce.
/// <para>Reload/enable/disable route <see cref="CommandRouting.Simulation"/>, like <c>world.grant</c>/
/// <c>world.revoke</c>: they apply SYNCHRONOUSLY at submit (not buffered to the tick boundary like a mutation), and
/// Simulation routing makes a following <c>world.addons</c> read behind the stdin barrier observe the settled state.
/// Gated on <see cref="WorldCapability.Mutate"/> over <see cref="GrantSubject.Section"/>(<see cref="WorldSection.Addons"/>)
/// against <c>context.ActingPrincipal()</c>, checked BEFORE the runtime touches anything, so a denial changes
/// nothing. <b>Boot-anchored arming:</b> the replay tape pins its mounted guests' receipts
/// ONCE at record-start (<c>WorldReplaySnapshot</c>), index-by-index — a live reload/enable/disable during an
/// active <c>replay.record</c> would change what is actually running (name/hash/fuel, or a fresh-instance
/// generation) without the tape ever learning of it, because those three verbs still reach the runtime directly
/// rather than through an ordered-domain leaf. <see cref="RefuseIfArmed"/> REFUSES those three outright while a
/// recording is active — superseding the prior warn-and-proceed posture — the operator runs <c>replay.stop</c> or
/// <c>replay.cancel</c> first, or uses <c>world.addon.mount</c>/<c>.unmount</c> instead, which now ride the
/// tape.</para></remarks>
internal sealed class WorldAddonCommandModule(WorldAddonRuntime runtime, WorldReplayTape tape, WorldServer server, IServerLink link) : ICommandModule {
    // The authority gate every lifecycle verb runs BEFORE touching the runtime: the ACTING principal — the submitter,
    // never a laundered identity — must hold Mutate over section:addons, the same section the document-side addon rows
    // are gated on. Returns the loud denial CommandResult on refusal, null to proceed.
    private CommandResult? CheckAuthority(CommandContext context, string verb) {
        var actingPrincipal = context.ActingPrincipal();

        if (server.Grants.Allows(
            principal: actingPrincipal,
            capability: WorldCapability.Mutate,
            subject: GrantSubject.Section(section: WorldSection.Addons)
        ) is { IsAllowed: false } verdict) {
            return CommandResult.Error(output: $"[{verb}: {actingPrincipal.Describe()} cannot mutate section:addons ({verdict.DescribeDenial()}) — see world.why]");
        }

        return null;
    }
    private CommandResult Describe(WireArgs args) {
        if (args.Count > 0) {
            return Usage(
                form: "",
                verb: "world.addons"
            );
        }

        var report = runtime.DescribeCost();

        if (report.Count == 0) {
            return new CommandResult(Output: "[world.addons: no addons]");
        }

        var builder = new StringBuilder(value: "[world.addons:");

        for (var index = 0; (index < report.Count); index++) {
            var entry = report[index];
            var state = StateLabel(entry: entry);

            _ = builder.Append(value: ((index == 0)
                ? " "
                : " | "))
                .Append(value: entry.Name).Append(value: ' ')
                .Append(value: state)
                .Append(value: " fuel-budget:").Append(value: entry.FuelPerTick)
                .Append(value: " fuel-last-tick:").Append(value: entry.LastTickFuelConsumed)
                .Append(value: " fuel-total:").Append(value: entry.TotalFuelConsumed)
                .Append(value: " answers-dropped-total:").Append(value: entry.TotalAnswersDropped)
                .Append(value: " event-gaps-total:").Append(value: entry.EventGaps)
                .Append(value: " event-cells-total:").Append(value: entry.EventCellsDelivered)
                .Append(value: " route-events-total:").Append(value: entry.RouteEventsDelivered)
                .Append(value: " collision-events-total:").Append(value: entry.CollisionEventsDelivered);
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    private CommandResult Mount(CommandContext context, WireArgs args) {
        if (
            (args.Count < 4) ||
            (((args.Count - 4) % 2) != 0)
        ) {
            return Usage(
                form: "<name> <modulePath> <hash> <fuel> [<capability> <subject>]...",
                verb: "world.addon.mount"
            );
        }

        if (!ulong.TryParse(
            s: args[3].ToString(),
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var fuel
        )) {
            return CommandResult.Error(output: "[world.addon.mount: fuel must be a non-negative integer]");
        }

        // Trailing <capability> <subject> pairs are the console's manifest grammar — the SAME token vocabulary
        // world.grant uses (WorldGrantCommandModule.TryParseCapability/TryParseSubject), reused rather than
        // reinvented so "drive body:0" means the identical thing on both verbs. Optional: a mount with none asks
        // for nothing and therefore reaches nothing until granted AND requested (deny-by-default holds regardless).
        List<WorldCapabilityRequest>? requests = null;

        for (var index = 4; (index < args.Count); index += 2) {
            if (!WorldGrantCommandModule.TryParseCapability(
                token: args[index],
                capability: out var capability
            )) {
                return CommandResult.Error(output: $"[world.addon.mount: unrecognized capability '{args[index]}']");
            }

            if (!WorldGrantCommandModule.TryParseSubject(
                token: args[(index + 1)],
                subject: out var subject
            )) {
                return CommandResult.Error(output: $"[world.addon.mount: unrecognized subject '{args[(index + 1)]}']");
            }

            (requests ??= []).Add(item: new WorldCapabilityRequest(
                Capability: capability,
                Subject: subject
            ));
        }

        // No pre-check here: unlike reload/enable/disable (which apply synchronously in THIS handler and so gain
        // nothing from deferring the check), mount buffers to the tick boundary through the SAME door a document
        // mutation drains through — the server itself checks Mutate over section:addons before touching the
        // runtime and prints the loud accept/reject line, exactly like world.row.set addons/world.row.remove addons already do.
        link.SubmitAddonLifecycle(
            lifecycle: new WorldAddonLifecycle.Mount(
                Name: args[0].ToString(),
                ModulePath: args[1].ToString(),
                Hash: args[2].ToString(),
                Fuel: fuel,
                Requests: requests
            ),
            principal: context.ActingPrincipal()
        );

        return CommandResult.None;
    }
    // REFUSES every call while a recording is active — the boot-anchored arming contract's other half:
    // replay.record already refuses to ARM once an addon has ever pumped, and this closes the matching gap on the
    // OTHER side of that boundary — a live reload/enable/disable WHILE armed would change what is actually mounted
    // (name/hash/fuel, the index-by-index pin WorldReplaySnapshot.VerifyMountedAddons checks) without the tape,
    // already committed to its record-start receipts, ever learning of it.
    private CommandResult? RefuseIfArmed(string verb, string name) {
        if (tape.Mode != WorldReplayMode.Recording) {
            return null;
        }

        return CommandResult.Error(output: $"[{verb}: refused — replay recording '{tape.Name}' is active; the mounted-addon receipts a tape pins ONCE at record-start, so a live change to '{name}' would silently invalidate it. replay.stop or replay.cancel first.]");
    }
    private CommandResult Reload(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return Usage(
                form: "<name>",
                verb: "world.addon.reload"
            );
        }

        if (CheckAuthority(
            context: context,
            verb: "world.addon.reload"
        ) is { } denial) {
            return denial;
        }

        var name = args[0].ToString();

        if (RefuseIfArmed(
            name: name,
            verb: "world.addon.reload"
        ) is { } refusal) {
            return refusal;
        }

        return new CommandResult(Output: $"[world.addon.reload: {runtime.Reload(name: name)}]");
    }
    private CommandResult SetEnabled(CommandContext context, WireArgs args, bool enabled) {
        var verb = (enabled
            ? "world.addon.enable"
            : "world.addon.disable"
        );

        if (args.Count != 1) {
            return Usage(
                form: "<name>",
                verb: verb
            );
        }

        if (CheckAuthority(
            context: context,
            verb: verb
        ) is { } denial) {
            return denial;
        }

        var name = args[0].ToString();

        if (RefuseIfArmed(
            name: name,
            verb: verb
        ) is { } refusal) {
            return refusal;
        }

        return new CommandResult(Output: $"[{verb}: {runtime.SetEnabled(
            enabled: enabled,
            name: name
        )}]");
    }
    private static string StateLabel(AddonCostReport entry) => entry.State switch {
        AddonState.Enabled => "ENABLED",
        AddonState.Disabled => "DISABLED",
        _ => $"FAULTED({(entry.FaultDetail ?? entry.State.ToString())})",
    };
    private CommandResult Unmount(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return Usage(
                form: "<name>",
                verb: "world.addon.unmount"
            );
        }

        link.SubmitAddonLifecycle(
            lifecycle: new WorldAddonLifecycle.Unmount(Name: args[0].ToString()),
            principal: context.ActingPrincipal()
        );

        return CommandResult.None;
    }
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: (string.IsNullOrEmpty(value: form)
            ? $"[{verb}: expected no arguments]"
            : $"[{verb}: expected {form}]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addon.mount",
            description: "Live-mounts a NEW guest through the ordered submission domain: world.addon.mount <name> <modulePath> <hash> <fuel> [<capability> <subject>].... Trailing pairs declare the manifest (what the guest asks for — same token grammar as world.grant, e.g. drive body:0); omit them for a guest that asks for nothing and therefore reaches nothing (deny-by-default holds regardless of a later grant). Buffers and applies at the tick boundary (the same door a document mutation drains through), so a following world.addons read behind the stdin barrier observes the settled state; the server's own loud accept/reject line prints when it applies. Refuses a name already tracked in the mounted set — mount never re-admits an existing guest (see world.addon.reload for that). Rides the replay tape through the shared addon-lifecycle leaf codec, so a recorded mount re-executes on replay.verify.",
            handler: (context, args) => Mount(
                args: args,
                context: context
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addon.unmount",
            description: "Fully unmounts a guest by name through the ordered submission domain: world.addon.unmount <name>. Stronger than world.addon.disable: the guest leaves the mounted set and world.addons entirely rather than staying tracked-but-skipped. Buffers and applies at the tick boundary; the server's own loud accept/reject line prints when it applies. Rides the replay tape through the shared addon-lifecycle leaf codec, so a recorded unmount re-executes on replay.verify.",
            handler: (context, args) => Unmount(
                args: args,
                context: context
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addon.reload",
            description: "Reloads a MOUNTED addon from its declared module path and re-runs the admit sequence: world.addon.reload <name>. Re-reads and recompiles the module (an unchanged content hash reuses the module cache; a declared moduleHash pin refuses a content change and leaves the running instance untouched), then re-reports the capability disclosure and re-admits the fresh instance before it can tick again. A row that never reached the mounted set (a boot load fault) is out of this verb's reach. Requires Mutate over section:addons — see world.why. Refused while a replay recording is active: a live reload mid-stream would invalidate the tape's record-start mount pin.",
            handler: (context, args) => Reload(
                args: args,
                context: context
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addon.enable",
            description: "Re-enables a MOUNTED, disabled or faulted addon and re-runs the admit sequence: world.addon.enable <name>. Re-instantiates the SAME instance in place (a fresh store — recovers a tick trap, e.g. the OutOfFuel fault's own retry instruction) then re-admits it. A LOAD fault (missing file, bad bytes, a hash-pin mismatch) constructed no module to re-instantiate and is reported honestly rather than claimed fixed — use world.addon.reload once the module is fixed. Requires Mutate over section:addons — see world.why. Refused while a replay recording is active.",
            handler: (context, args) => SetEnabled(
                args: args,
                context: context,
                enabled: true
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addon.disable",
            description: "Administratively disables a MOUNTED addon: world.addon.disable <name>. Skipped every tick until world.addon.enable brings it back; releases nothing, because a contribution is per-tick and expires on its own. Requires Mutate over section:addons — see world.why. Refused while a replay recording is active.",
            handler: (context, args) => SetEnabled(
                args: args,
                context: context,
                enabled: false
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addons",
            description: "Reports the live per-guest cost surface (Immediate; the stdin barrier makes it read the settled state after a pending reload/enable/disable): world.addons. One segment per mounted addon — lifecycle state (with the fault detail, if faulted), lane, the per-tick fuel budget, fuel consumed by the most recent tick it actually ran (zero on a tick it was skipped), the running fuel total consumed since it was FIRST mounted, answer groups dropped with no verdict cell, event cells dropped to a per-row event budget or the input-ring ceiling, and collision events delivered. Lifetime counters survive a disable/enable cycle and a live reload alike. Diagnostic only — never simulation state, never on a hashed path.",
            handler: (_, args) => Describe(args: args)
        );
    }
}
