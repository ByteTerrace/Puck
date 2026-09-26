using System.Text.Json;
using Puck.Hosting;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The views.graphs section: each row's shape, then the rows as one instance set, so a loop of same-frame reads is
    // refused by the scheduler's own rule, naming every instance in it. Returns the rows' names.
    private static HashSet<string> ValidateGraphs(WorldViewDefaults views, HashSet<string> cameras, WorldDefinition definition, List<string> errors) {
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
                    (graph.Overrides is not null) ||
                    (graph.Parameters is not null)
                ) {
                    errors.Add(item: $"{path}: package instance '{graph.Name}' takes no timeScale, output, overrides or parameters.");
                }
                // A source package names a producer by id and opens it from the row's settings, as a screen's producer
                // source does.
                if (graph.Package.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: RenderGraphInstance.SourcePackagePrefix
                )) {
                    var producer = graph.Package[RenderGraphInstance.SourcePackagePrefix.Length..];

                    WorldImageProducerVocabulary.Validate(
                        definition: definition,
                        errors: errors,
                        path: path,
                        source: new WorldScreenSource.Producer(
                            Id: producer,
                            Settings: graph.Settings
                        )
                    );

                    // A row's source renders in the host's graph runtime, which converts an uploaded producer's region;
                    // an imported producer's image reaches only a screen.
                    if (
                        WorldImageProducerVocabulary.TryGet(
                            id: producer,
                            shape: out var shape
                        ) &&
                        (shape.Transport != Puck.Abstractions.Sources.ImageSourceTransport.Uploaded)
                    ) {
                        errors.Add(item: $"{path}.package '{graph.Package}' names the {shape.Transport} producer '{producer}'; a views.graphs row renders only an uploaded producer's source.");
                    }
                }
            }
            if (
                (graph.Settings is not null) &&
                !(graph.Package?.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: RenderGraphInstance.SourcePackagePrefix
                ) ?? false)
            ) {
                errors.Add(item: $"{path}.settings: only a source package row ('{RenderGraphInstance.SourcePackagePrefix}<producer id>') takes settings.");
            }

            ValidateInstanceValues(
                errors: errors,
                output: graph.Output,
                overrides: graph.Overrides,
                path: path,
                timeScale: graph.TimeScale
            );
            ValidateParameters(
                definition: definition,
                errors: errors,
                graph: graph,
                path: path
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
        if (views.GraphBudget is { } budget) {
            foreach (var (member, value) in ((ReadOnlySpan<(string, long)>)[
                ("passPixelsPerFrame", budget.PassPixelsPerFrame),
                ("bytesPerTick", budget.BytesPerTick),
                ("bytesPerFrame", budget.BytesPerFrame),
            ])) {
                if (value < 0) {
                    errors.Add(item: $"views.graphBudget.{member} {value} must be non-negative.");
                }
            }
            // The bound parameters' bytes, priced from the document as the cost report prices them, against the
            // authored ceilings.
            if (
                (errors.Count == shapeErrors) &&
                (WorldPresentationCost.CeilingRefusal(
                    bindings: WorldBindingCost.Measure(definition: definition),
                    budget: budget
                ) is { } ceilingRefusal)
            ) {
                errors.Add(item: ceilingRefusal);
            }
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
    // A row's bound parameters: each value a finite number or a state token naming a Fixed or Int cell, and no field
    // both bound here and overridden, since a binding rewritten over an override every frame hides one of the two. The
    // source's config schema checks that each pass and field exists when the server binds the row.
    private static void ValidateParameters(string path, WorldViewGraph graph, WorldDefinition definition, List<string> errors) {
        foreach (var (pass, fields) in (graph.Parameters ?? new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>())) {
            if (string.IsNullOrWhiteSpace(value: pass)) {
                errors.Add(item: $"{path}.parameters names an empty pass.");

                continue;
            }
            if (fields is not { Count: > 0 }) {
                errors.Add(item: $"{path}.parameters.{pass} binds no field; omit the pass instead.");

                continue;
            }

            var overridden = ((
                (graph.Overrides is { } overrides) &&
                overrides.TryGetValue(
                    key: pass,
                    value: out var config
                ) &&
                (config.ValueKind == JsonValueKind.Object)
            )
                ? config
                : (JsonElement?)null
            );

            foreach (var (field, value) in fields) {
                if (string.IsNullOrWhiteSpace(value: field)) {
                    errors.Add(item: $"{path}.parameters.{pass} names an empty field.");
                } else if (
                    !value.IsAuthorable(definition: definition) &&
                    !BindsWholeRow(
                    definition: definition,
                    value: value
                )
                ) {
                    errors.Add(item: $"{path}.parameters.{pass}.{field} {BindableScalar.Grammar}.");
                } else if (
                    (overridden is { } overrideObject) &&
                    overrideObject.TryGetProperty(
                        propertyName: field,
                        value: out _
                    )
                ) {
                    errors.Add(item: $"{path}.parameters.{pass}.{field}: graph '{graph.Name}' binds pass '{pass}' field '{field}' and also overrides it; a field is bound or overridden, never both.");
                }
            }
        }
    }
    // A token naming a keyed numeric row with no key binds the whole row, which only an array member reads; the server's
    // source bind holds it to the array it fills.
    private static bool BindsWholeRow(BindableScalar value, WorldDefinition definition) => (
        (value.State is { Key: null } binding) &&
        WorldBoundRow.TryResolve(
        definition: definition,
        length: out _,
        row: out var row,
        rowName: binding.Row
    ) &&
        (row.Kind is CellKind.Int or CellKind.Fixed or CellKind.Bool)
    );
    // The views.post rows, which the synthesized root runs over the composed frame: each name, each package against the
    // host's post-process vocabulary, and each config bound against its package's schema, as the graph compiler binds
    // it when the root is composed, so a live views.post edit whose config does not bind is refused naming the row.
    private static void ValidatePostPasses(WorldViewDefaults views, List<string> errors, ICollection<string>? deferred) {
        var passes = (views.Post ?? []);

        if (passes.Count == 0) {
            return;
        }
        if (views.Root is not null) {
            errors.Add(item: "views.post: a world that names views.root authors its whole render graph, and composition synthesizes no root to run post passes in.");
        }

        // The root names each pane's place pass after its views.graphs row, so a post pass may take no row's name.
        var rows = (views.Graphs ?? []).Where(predicate: static graph => (graph is not null)).Select(selector: static graph => graph.Name).ToHashSet(comparer: StringComparer.Ordinal);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < passes.Count); index++) {
            var pass = passes[index];
            var path = $"views.post[{index}]";

            if (pass is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }
            if (!SafeName.TryParse(
                candidate: pass.Name,
                name: out _,
                reason: out var nameReason
            )) {
                errors.Add(item: $"{path}.name {nameReason}");
            } else if (!GeneratedName.TryValidateAuthored(
                name: pass.Name,
                reason: out var reservedReason
            )) {
                errors.Add(item: $"{path}.name {reservedReason}");
            } else if (!names.Add(item: pass.Name)) {
                errors.Add(item: $"{path}.name '{pass.Name}' is duplicated.");
            } else if (rows.Contains(item: pass.Name)) {
                errors.Add(item: $"{path}.name '{pass.Name}' is a views.graphs row's name, which the root's place pass for that row takes.");
            }
            if (string.IsNullOrWhiteSpace(value: pass.Package)) {
                errors.Add(item: $"{path}.package is required.");

                continue;
            }

            switch (WorldPostProcessVocabularyHook.IsPostProcessPackage(package: pass.Package)) {
                case false:
                    errors.Add(item: $"{path}.package '{pass.Package}' names no post-process package.");

                    break;
                case null:
                    deferred?.Add(item: $"{path}.package: post-process package '{pass.Package}' deferred — this host carries no render graph package catalog.");

                    break;
                case true when (WorldPostProcessVocabularyHook.ConfigRefusal(config: pass.Config, package: pass.Package, pass: pass.Name) is { } refusal):
                    errors.Add(item: $"{path}.config of post pass '{pass.Name}' does not bind to package '{pass.Package}': {refusal}");

                    break;
            }
        }
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
