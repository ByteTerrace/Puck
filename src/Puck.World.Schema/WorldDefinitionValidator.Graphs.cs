using Puck.Hosting;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The views.graphs section: each row's shape, then the rows as one instance set, so a loop of same-frame reads is
    // refused by the scheduler's own rule, naming every instance in it.
    private static void ValidateGraphs(WorldViewDefaults views, HashSet<string> cameras, List<string> errors) {
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
            } else if (!names.Add(item: graph.Name)) {
                errors.Add(item: $"{path}.name '{graph.Name}' is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(value: graph.Source)) {
                errors.Add(item: $"{path}.source is required.");
            }
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
    }
}
