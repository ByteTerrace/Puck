using System.Text.Json;
using Puck.Hosting;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The views.graphs section: each row's shape, then the rows as one instance set, so a loop of same-frame reads is
    // refused by the scheduler's own rule, naming every instance in it. Returns the rows' names.
    private static HashSet<string> ValidateGraphs(WorldViewDefaults views, HashSet<string> cameras, List<string> errors) {
        var graphs = (views.Graphs ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var shapeErrors = errors.Count;

        for (var index = 0; (index < graphs.Count); index++) {
            var graph = graphs[index];
            var path = $"views.graphs[{index}]";

            if (graph is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }
            if (!SafeName.TryParse(
                candidate: graph.Name,
                name: out _,
                reason: out var nameReason
            )) {
                errors.Add(item: $"{path}.name {nameReason}");
            } else if (!GeneratedName.TryValidateAuthored(
                name: graph.Name,
                reason: out var reservedReason
            )) {
                // The synthesized root names its own versions and passes in the generated form, so an instance, whose
                // name is also its version and place pass there, can never collide with one.
                errors.Add(item: $"{path}.name {reservedReason}");
            } else if (!names.Add(item: graph.Name)) {
                errors.Add(item: $"{path}.name '{graph.Name}' is duplicated.");
            } else if (
                (views.Root is null) &&
                WorldViewGraphs.IsSynthesized(name: graph.Name)
            ) {
                errors.Add(item: $"{path}.name '{graph.Name}' is an instance of the render graph composition synthesizes; name another, or name views.root to author the whole graph.");
            }
            if (string.IsNullOrWhiteSpace(value: graph.Source) == string.IsNullOrWhiteSpace(value: graph.Package)) {
                errors.Add(item: $"{path} must author exactly one of source and package.");
            } else if (graph.Package is not null) {
                if (graph.Inputs is { Count: > 0 }) {
                    errors.Add(item: $"{path}.inputs: package instance '{graph.Name}' renders through its producer, which reads no input.");
                }
                if (
                    (graph.TimeScale != 1f) ||
                    (graph.Output is not null) ||
                    (graph.Overrides is not null)
                ) {
                    errors.Add(item: $"{path}: package instance '{graph.Name}' takes no timeScale, output or overrides.");
                }
            }

            ValidateInstanceValues(
                errors: errors,
                output: graph.Output,
                overrides: graph.Overrides,
                path: path,
                timeScale: graph.TimeScale
            );
            if (
                (graph.Camera is { } camera) &&
                !cameras.Contains(item: camera)
            ) {
                errors.Add(item: $"{path}.camera '{camera}' names no camera row.");
            }
            if (
                (graph.Refresh is { } refresh) &&
                !WorldViewGraphs.RefreshOf(graph: graph).IsValid
            ) {
                errors.Add(item: $"{path}.refresh must author exactly one of divisor or hertz, and it must be positive (divisor {(refresh.Divisor?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "absent")}, hertz {(refresh.Hertz?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "absent")}).");
            }

            var resources = new HashSet<string>(comparer: StringComparer.Ordinal);
            var inputs = (graph.Inputs ?? []);

            for (var inputIndex = 0; (inputIndex < inputs.Count); inputIndex++) {
                var input = inputs[inputIndex];
                var inputPath = $"{path}.inputs[{inputIndex}]";

                if (input is null) {
                    errors.Add(item: $"{inputPath} is required.");

                    continue;
                }
                if (string.IsNullOrWhiteSpace(value: input.Resource)) {
                    errors.Add(item: $"{inputPath}.resource is required.");
                } else if (!resources.Add(item: input.Resource)) {
                    errors.Add(item: $"{inputPath}.resource '{input.Resource}' is bound more than once.");
                }
            }
        }
        for (var index = 0; (index < graphs.Count); index++) {
            var inputs = (graphs[index]?.Inputs ?? []);

            for (var inputIndex = 0; (inputIndex < inputs.Count); inputIndex++) {
                if (
                    (inputs[inputIndex] is { } input) &&
                    !names.Contains(item: (input.Instance ?? string.Empty))
                ) {
                    errors.Add(item: $"views.graphs[{index}].inputs[{inputIndex}].instance '{input.Instance}' names no views.graphs row.");
                }
            }
        }
        if (
            (views.Root is { } root) &&
            !names.Contains(item: root)
        ) {
            errors.Add(item: $"views.root '{root}' names no views.graphs row.");
        }
        if (
            (views.GraphBudget is { } budget) &&
            (budget.PassPixelsPerFrame < 0)
        ) {
            errors.Add(item: $"views.graphBudget.passPixelsPerFrame {budget.PassPixelsPerFrame} must be non-negative.");
        }
        if (
            (errors.Count == shapeErrors) &&
            !RenderGraphInstanceSet.TryCreate(
                instances: WorldViewGraphs.Instances(
                    graphs: graphs,
                    passes: static _ => 1
                ),
                refusal: out var refusal,
                set: out _
            )
        ) {
            errors.Add(item: $"views.graphs: {refusal.Message}");
        }

        return names;
    }
    // A shown instance's presentation values: its clock rate, the output it shows and the shape of its override set;
    // the source's config schema binds the values themselves.
    private static void ValidateInstanceValues(string path, float timeScale, string? output, IReadOnlyDictionary<string, JsonElement>? overrides, List<string> errors) {
        if (
            !float.IsFinite(f: timeScale) ||
            (timeScale < 0f)
        ) {
            errors.Add(item: $"{path}.timeScale {timeScale} must be finite and non-negative.");
        }
        if (
            (output is not null) &&
            string.IsNullOrWhiteSpace(value: output)
        ) {
            errors.Add(item: $"{path}.output must name an image version when present.");
        }

        foreach (var (pass, config) in (overrides ?? new Dictionary<string, JsonElement>())) {
            if (string.IsNullOrWhiteSpace(value: pass)) {
                errors.Add(item: $"{path}.overrides names an empty pass.");
            } else if (config.ValueKind != JsonValueKind.Object) {
                errors.Add(item: $"{path}.overrides.{pass} must be an object of config fields.");
            } else if (!config.EnumerateObject().Any()) {
                errors.Add(item: $"{path}.overrides.{pass} overrides no field; omit the pass instead.");
            }
        }
    }
}
