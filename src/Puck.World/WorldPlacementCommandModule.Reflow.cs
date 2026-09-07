using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class WorldPlacementCommandModule {
    private readonly Lock m_reflowGate = new();
    private readonly Dictionary<WorldPrincipal, ReflowPreview> m_reflowPreviews = [];
    // Host-side authoring cache limits, not simulation timers. Eviction also reclaims disconnected actors.
    private const int PreviewCapacity = 64;
    private const long PreviewLifetimeMilliseconds = 300_000;
    private sealed record ReflowPreview(Task<(WorldPlacementProposal? Proposal, string Reason)> Pending, long Created, bool Reviewed = false);

    private void ExpireReflowPreviews() {
        var now = Environment.TickCount64;
        foreach (var key in m_reflowPreviews.Where(pair => now - pair.Value.Created >= PreviewLifetimeMilliseconds).Select(pair => pair.Key).ToArray()) {
            m_reflowPreviews.Remove(key);
        }
    }

    private IEnumerable<CommandDefinition> ReflowCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.reflow.preview",
            description: "Starts bounded background layout planning. Usage: world.reflow.preview <template>. Read world.reflow.status before world.reflow.commit. Previews expire after five minutes.",
            handler: (context, args) => {
                if (args.Count != 1) { return CommandResult.Error("usage: world.reflow.preview <template>"); }
                lock (m_reflowGate) {
                    ExpireReflowPreviews();
                    var principal = context.ActingPrincipal();
                    if (!server.TryStartReflowPreview(args[0].ToString(), principal, out var pending, out var reason)) { return CommandResult.Error($"[world.reflow: {reason}]"); }
                    m_reflowPreviews.Remove(principal);
                    if (m_reflowPreviews.Count >= PreviewCapacity) {
                        m_reflowPreviews.Remove(m_reflowPreviews.MinBy(pair => pair.Value.Created).Key);
                    }
                    m_reflowPreviews[principal] = new ReflowPreview(pending!, Environment.TickCount64);
                    return new CommandResult(Output: "[world.reflow: planning; read world.reflow.status for the proposal]");
                }
            });
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.reflow.status",
            description: "Reads the pending layout or reviews its proposed positions and price. Usage: world.reflow.status.",
            handler: (context, args) => {
                if (args.Count != 0) { return CommandResult.Error("usage: world.reflow.status"); }
                lock (m_reflowGate) {
                    ExpireReflowPreviews();
                    var principal = context.ActingPrincipal();
                    if (!m_reflowPreviews.TryGetValue(principal, out var entry)) { return CommandResult.Error("[world.reflow: preview a layout first]"); }
                    if (!entry.Pending.IsCompleted) { return new CommandResult(Output: "[world.reflow: planning]"); }
                    if (!entry.Pending.IsCompletedSuccessfully) {
                        m_reflowPreviews.Remove(principal);
                        return CommandResult.Error("[world.reflow: preview worker failed; preview again]");
                    }
                    var (preview, reason) = entry.Pending.Result;
                    if (preview is null) { m_reflowPreviews.Remove(principal); return CommandResult.Error($"[world.reflow: {reason}]"); }
                    m_reflowPreviews[principal] = entry with { Reviewed = true };
                    var changes = preview.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>()
                        .Select(edit => string.Create(CultureInfo.InvariantCulture,
                            $"{edit.Placement.Id} -> local ({edit.Placement.Position.X:0.###}, {edit.Placement.Position.Y:0.###}, {edit.Placement.Position.Z:0.###})"));
                    return new CommandResult(Output: $"[world.reflow: {preview.Moved} moved; cost={preview.Cost}; checks={preview.Candidates}; {string.Join("; ", changes)}]");
                }
            });
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.reflow.commit",
            routing: CommandRouting.Simulation,
            description: "Submits the reviewed layout and price as one guarded mutation batch. Usage: world.reflow.commit.",
            handler: (context, args) => {
                if (args.Count != 0) { return CommandResult.Error("usage: world.reflow.commit"); }
                WorldPlacementProposal preview;
                lock (m_reflowGate) {
                    ExpireReflowPreviews();
                    var principal = context.ActingPrincipal();
                    if (!m_reflowPreviews.TryGetValue(principal, out var entry)) { return CommandResult.Error("[world.reflow: preview a layout first]"); }
                    if (!entry.Reviewed) { return CommandResult.Error("[world.reflow: read world.reflow.status before committing]"); }
                    preview = entry.Pending.Result.Proposal!;
                    m_reflowPreviews.Remove(principal);
                }
                return link.Submit(preview.Mutation, echoes, "world.reflow.commit");
            });
    }
}
