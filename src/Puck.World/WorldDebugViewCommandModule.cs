using System.Numerics;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

// Core registered: an audit is available with the view off and without a renderer.
internal sealed class WorldDebugViewCommandModule(WorldDiagnostics diagnostics, IServerLink link,
    WorldRenderProbe? renderProbe = null, WorldRenderSettings? settings = null) : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(name: "world.debug-view", bindability: CommandBindability.Bindable,
                description: $"world.debug-view [{string.Join('|', DebugViewModes.Names)}|next|toggle <mode>] [focus|pins-only] [--amber] — colliders and contacts preserve the scene; default is exterior marks and pins. report [colliders|contacts|all] [--json] evaluates on demand, even off or headless; incomplete inputs are unknown. goto <finding-id> moves only the operator camera. world.view.pick <view-or-instance> finding <id> reads a retained causal record even while off; primary resolves to the first seat's rendered instance. Run report first to populate findings. Findings name snapshot, tick, revision, provenance, expected relation, measurements, budget, coverage and lifetime. Amber is off by default.",
                handler: (context, args) => Execute(context, args), routing: CommandRouting.Immediate);
    }

    private CommandResult Execute(CommandContext context, WireArgs args) {
        if (args.Count > 0 && args.Is(0, "report")) {
            var view = diagnostics.Mode is "colliders" or "contacts" ? diagnostics.Mode : "all";
            var json = false;
            for (var i = 1; i < args.Count; i++) {
                if (args.Is(i, "--json")) { json = true; }
                else if (args[i].ToString() is "colliders" or "contacts" or "all") { view = args[i].ToString(); }
                else { return CommandResult.Usage("report [colliders|contacts|all] [--json]", "world.debug-view"); }
            }
            var reports = (view == "all" ? new[] { "colliders", "contacts" } : [view]).Select(diagnostics.Report).ToArray();
            return new CommandResult(Output: json
                ? (reports.Length == 1 ? reports[0].ToJson() : "[" + string.Join(",", reports.Select(r => r.ToJson())) + "]")
                : string.Join(Environment.NewLine, reports.Select(Text)));
        }
        if (args.Count == 2 && args.Is(0, "goto")) {
            if (diagnostics.Find(args[1].ToString()) is not { } finding) {
                return CommandResult.Error("[world.debug-view: unknown finding; run report first]");
            }
            if (finding.Severity == "unknown") { return CommandResult.Error("[world.debug-view: finding has no spatial witness]"); }
            var normal = finding.Normal.LengthSquared() > 0.1f ? Vector3.Normalize(finding.Normal) : Vector3.UnitZ;
            var pose = WorldOperatorCamera.FromLookAt(eye: finding.Witness + normal * 3 + Vector3.UnitY,
                target: finding.Witness, fieldOfViewRadians: WorldOperatorCamera.DefaultFieldOfViewRadians);
            link.SubmitComposition(new WorldComposition.SetOperatorCamera(Pose: pose), context.Principal);
            return new CommandResult(Output: $"[world.debug-view: camera requested for finding={finding.Id}; body unchanged]");
        }
        if (args.Count == 0) { return new CommandResult(Output: $"[world.debug-view: {diagnostics.Mode} {diagnostics.Presentation}]"); }
        var name = args[0].ToString();
        var index = 1;
        if (name == "next") {
            _ = DebugViewModes.TryParse(diagnostics.Mode, out var previous);
            name = DebugViewModes.Name((previous + 1) % DebugViewModes.Count);
        } else if (name == "toggle" && args.Count >= 2) {
            name = diagnostics.Mode == args[1].ToString() ? "off" : args[1].ToString();
            index = 2;
        }
        if (!DebugViewModes.TryParse(name, out var mode)) { return CommandResult.Error($"[world.debug-view: unknown mode '{name}']"); }
        var presentation = "default";
        var amber = false;
        while (index < args.Count) {
            var option = args[index++].ToString();
            if (option == "--amber") { amber = true; }
            else if (option is "focus" or "pins-only" && presentation == "default") { presentation = option; }
            else { return CommandResult.Error("[world.debug-view: expected focus, pins-only or --amber]"); }
        }
        if (index != args.Count) { return CommandResult.Error("[world.debug-view: unexpected arguments]"); }
        diagnostics.Mode = DebugViewModes.Name(mode);
        diagnostics.Presentation = presentation;
        diagnostics.Amber = amber;
        if (renderProbe?.Residency is { } residency) { residency.DebugMode = mode; }
        if (settings is not null) { settings.RateTints = DebugViewModes.ShowsRates(mode); }
        if (diagnostics.Mode is "colliders" or "contacts") {
            var report = diagnostics.Report(diagnostics.Mode);
            return new CommandResult(Output: $"[world.debug-view: {report.Summary()}]");
        }
        return new CommandResult(Output: $"[world.debug-view: {diagnostics.Mode}]");
    }

    private static string Text(WorldDiagnosticReport report) => $"[{report.Summary()}]" + Environment.NewLine +
        string.Join(Environment.NewLine, report.Findings.Select(f =>
            $"#{f.Id} {f.Severity} {f.Check} subject={f.Identity.Subject} source={f.Source} separation={f.Observed}m budget={f.Budget.Total}m witness={f.Witness} activeTicks={f.ActiveTicks} resolved={f.Resolved}; {f.Expected}"));
}
