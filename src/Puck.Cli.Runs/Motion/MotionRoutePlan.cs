using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.World;
using Puck.World.Client;
using Puck.Cli.Sweep;

namespace Puck.Cli.Motion;

/// <summary>Authored tick-relative commands, expanded onto the existing world's tick-script queue.</summary>
public sealed record MotionRouteStep(int At, string Run, int? Through = null);
/// <summary>A named endpoint reading. Counter readings are results, never workload-equivalence requirements.</summary>
public sealed record MotionRouteMetric(string Name, string Run, string Parse = "text");
/// <summary>A generic no-capture route: no body, creation, form, camera or landscape is built into its runner.</summary>
public sealed record MotionRoutePlan(string Schema, string Name, int WarmupTicks, int LastTick,
    IReadOnlyList<string> Setup, IReadOnlyList<MotionRouteStep> InputSteps,
    IReadOnlyList<MotionRouteStep> PresentationSteps, IReadOnlyList<MotionRouteMetric> Metrics) {
    public const string SchemaName = "puck.motion.route.v1";
    /// <summary>Authored read-back postconditions at warmup and after the route. Setup is for presentation controls;
    /// authority changes belong in the recorded InputSteps, not reapplied setup.</summary>
    public IReadOnlyList<SweepSignal> Checks { get; init; } = [];
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };

    public static MotionRoutePlan Read(string path) => Parse(File.ReadAllText(path));

    public static MotionRoutePlan Parse(string json) {
        var plan = JsonSerializer.Deserialize<MotionRoutePlan>(json, Options)
            ?? throw new InvalidDataException("The route file is empty.");
        if ((plan.Schema != SchemaName) || string.IsNullOrWhiteSpace(plan.Name) ||
            (plan.LastTick < 3) || (plan.LastTick > WorldPresentationProbe.MaximumSamples) ||
            (plan.WarmupTicks < 1) || (plan.WarmupTicks >= (plan.LastTick - 1)) ||
            (plan.Setup is null) || (plan.InputSteps is null) || (plan.PresentationSteps is null) || (plan.Metrics is null)) {
            throw new InvalidDataException("A route needs its schema, name, setup/inputSteps/presentationSteps/metrics arrays and a nonempty measured interval after warmup.");
        }
        var count = 0L;
        foreach (var step in plan.InputSteps.Concat(plan.PresentationSteps)) {
            if (step is null) { throw new InvalidDataException("A route step cannot be null."); }
            if ((step.At < 1) || ((step.Through ?? step.At) < step.At) || ((step.Through ?? step.At) > plan.LastTick)) {
                throw new InvalidDataException("Every step must lie on ticks 1..lastTick; through is inclusive and cannot precede at.");
            }
            RequirePinnable(step.Run);
            count += (((long)(step.Through ?? step.At)) - step.At + 1);
        }
        if (plan.Checks is null) { throw new InvalidDataException("Route checks cannot be null."); }
        var checkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in plan.Checks) {
            if (check is null || string.IsNullOrWhiteSpace(check.Name) || !checkNames.Add(check.Name)) { throw new InvalidDataException("Route checks need distinct nonempty names."); }
            RequirePinnable(check.Line);
            if (!SweepSignals.Parsers.Contains(check.Parse)) { throw new InvalidDataException($"Unknown check parser '{check.Parse}'."); }
            foreach (var rule in check.RedRules.Concat(check.AmberRules)) {
                if (!SweepRule.TryParse(rule, out _)) { throw new InvalidDataException($"Invalid check rule '{rule}'."); }
            }
        }
        // Both endpoint readings and the finite pause share the ordinary queue with authored input.
        if ((count + (2L * (plan.Metrics.Count + plan.Checks.Count)) + 2) > WorldTickScript.MaxPending) {
            throw new InvalidDataException($"The route exceeds world.at's {WorldTickScript.MaxPending} pending lines.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in plan.Metrics) {
            if (metric is null) { throw new InvalidDataException("A route metric cannot be null."); }
            RequirePinnable(metric.Run);
            if (!names.Add(metric.Name) || string.IsNullOrWhiteSpace(metric.Name) || !Sweep.SweepSignals.Parsers.Contains(metric.Parse)) {
                throw new InvalidDataException("Metrics need distinct nonempty names and an existing sweep parser.");
            }
        }
        foreach (var line in plan.Setup) { RequirePinnable(line); }
        return plan;
    }

    public static IReadOnlyList<(ulong Tick, string Line)> Expand(IReadOnlyList<MotionRouteStep> steps) {
        var lines = new List<(ulong Tick, string Line)>();
        foreach (var step in steps) {
            for (var tick = step.At; tick <= (step.Through ?? step.At); tick++) { lines.Add(((ulong)tick, step.Run)); }
        }
        return lines.OrderBy(static row => row.Tick).ToArray();
    }

    private static void RequirePinnable(string line) {
        if (string.IsNullOrWhiteSpace(line)) { throw new InvalidDataException("A route command cannot be null or empty."); }
        var command = line.Trim();
        var index = 0;
        _ = Puck.Commands.CommandEcho.TryReadToken(command, ref index, out var verb);
        if (command.Contains('\n') || command.Contains('\r') ||
            verb.ToLowerInvariant() is "world.wait" or "world.at" or "world.screenshot" or "world.rate" or "world.lockstep" or "quit" ||
            verb.StartsWith("replay.", StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException($"Route commands cannot capture, wait, replace replay/pacing, nest schedules or exit: {line}");
        }
    }
}
