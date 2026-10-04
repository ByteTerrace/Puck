using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>One planned pass of a graph: the planner's pass, and the package it runs when it is a package pass.</summary>
/// <param name="Planned">The planner's pass, with its order, dependencies and accesses.</param>
/// <param name="Package">The package a package pass runs, or <see langword="null"/> for a shader pass.</param>
public sealed record RenderGraphStep(ShaderPipelinePlannedPass Planned, RenderGraphPackage? Package) {
    /// <summary>Gets the pass name.</summary>
    public string Name => Planned.Name;
}
/// <summary>A planned frame graph: the pipeline planner's plan over its shader and package passes, each planned pass
/// tagged with the package it runs.</summary>
public sealed class RenderGraphPlan {
    internal RenderGraphPlan(RenderGraphDefinition definition, ShaderPipelinePlan pipeline, IReadOnlyList<RenderGraphStep> steps) {
        Definition = definition;
        Pipeline = pipeline;
        Steps = steps;
        Inputs = new ReadOnlyCollection<string>(list: [.. pipeline.Storages.Where(predicate: static storage => storage.IsExternal).Select(selector: static storage => storage.Name)]);
    }

    /// <summary>Gets the document planned.</summary>
    public RenderGraphDefinition Definition { get; }
    /// <summary>Gets the external versions a host binds, such as another instance's output, in ordinal name
    /// order.</summary>
    public IReadOnlyList<string> Inputs { get; }
    /// <summary>Gets the public versions.</summary>
    public IReadOnlyList<string> Outputs => Pipeline.Outputs;
    /// <summary>Gets the pipeline planner's plan. A package pass appears in it as a planned pass of kind
    /// <see cref="ShaderPipelinePassKind.Package"/> with no declaration and a <see cref="ShaderPipelinePackageStep"/>
    /// naming its package, its ports' versions and its extent; it is ordered, given its accesses and barriers, and kept
    /// live exactly as a shader pass is.</summary>
    public ShaderPipelinePlan Pipeline { get; }
    /// <summary>Gets the planned passes in execution order, parallel to the planner's passes.</summary>
    public IReadOnlyList<RenderGraphStep> Steps { get; }

    /// <summary>Returns what a version of the graph carries, which is what an instance edge bound to it carries: the
    /// kind of an <see cref="Inputs"/> version a consumer's read binds, or of the <see cref="Outputs"/> version its
    /// producer publishes.</summary>
    /// <param name="version">The version name.</param>
    /// <returns>The version's kind.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan holds no version of that name.</exception>
    public ShaderPipelineResourceKind KindOf(string version) {
        ArgumentNullException.ThrowIfNull(argument: version);

        return ((Pipeline.Storages.FirstOrDefault(predicate: storage => storage.Versions.Contains(
            comparer: StringComparer.Ordinal,
            value: version
        )) is { } found)
            ? found.Declaration.Kind
            : throw new ArgumentException(
                message: $"Graph '{Definition.Name}' plans no version '{version}'.",
                paramName: nameof(version)
            ));
    }
}
/// <summary>Validates a <c>puck.render.graph.v1</c> document and plans it with the pipeline planner. Package passes are
/// checked against the host's catalog and planned as passes that read and write their declared versions, so one
/// planner orders, versions and barriers every pass of a frame. It loads no source and creates no GPU object.</summary>
/// <param name="packages">The packages the host offers.</param>
/// <param name="limits">The planner's limits, or <see langword="null"/> for its defaults.</param>
public sealed class RenderGraphCompiler(RenderGraphPackageCatalog packages, ShaderPipelineLimits? limits = null) {
    private readonly ShaderPipelineCompiler m_planner = new(limits: limits);
    private readonly RenderGraphPackageCatalog m_packages = (packages ?? throw new ArgumentNullException(paramName: nameof(packages)));

    /// <summary>Gets the compiler of a host that runs shader passes alone, such as a pipeline instance's node, the
    /// shader packager and the pipeline verbs: it offers no package, so a graph naming one is refused by
    /// <c>RENDERGRAPH_PACKAGE_UNKNOWN</c>.</summary>
    public static RenderGraphCompiler ShaderPasses { get; } = new(packages: RenderGraphPackageCatalog.None);

    private static void Add(List<ShaderPipelineDiagnostic> diagnostics, string code, string message, string? name) => diagnostics.Add(item: new ShaderPipelineDiagnostic(
        Code: code,
        Message: message,
        Name: name
    ));
    private void Validate(RenderGraphDefinition definition, List<ShaderPipelineDiagnostic> diagnostics) {
        if (
            (definition.Resources is null) ||
            (definition.Outputs is null) ||
            definition.PackagePasses.Any(predicate: static pass => (
                (pass is null) ||
                string.IsNullOrWhiteSpace(value: pass.Name) ||
                pass.InputReferences.Concat(second: pass.OutputReferences).Any(predicate: static reference => ((reference is null) || string.IsNullOrWhiteSpace(value: reference.Name)))
            ))
        ) {
            Add(
                code: "RENDERGRAPH_DOCUMENT_SHAPE",
                diagnostics: diagnostics,
                message: "Graph resources, outputs, package passes and their references must be non-null and named.",
                name: definition.Name
            );

            return;
        }
        if (!string.Equals(
            a: definition.Schema,
            b: RenderGraphSchemas.Graph,
            comparisonType: StringComparison.Ordinal
        )) {
            Add(
                code: "RENDERGRAPH_SCHEMA",
                diagnostics: diagnostics,
                message: $"Graph '{definition.Name}' declares schema '{definition.Schema}'; expected '{RenderGraphSchemas.Graph}'.",
                name: definition.Name
            );
        }
        // A declared tier names one variant, so it is declared once.
        if (
            (definition.Tiers is { } tiers) &&
            (tiers.Distinct().Count() != tiers.Count)
        ) {
            Add(
                code: "RENDERGRAPH_TIERS",
                diagnostics: diagnostics,
                message: $"Graph '{definition.Name}' declares the tiers [{string.Join(separator: ", ", values: tiers.Select(selector: QualityTiers.Name))}]; each tier is declared once.",
                name: definition.Name
            );
        }

        var resources = new Dictionary<string, ShaderPipelineResource>(comparer: StringComparer.Ordinal);

        foreach (var resource in definition.Resources) {
            if (resource?.Name is { } name) {
                resources.TryAdd(
                    key: name,
                    value: resource
                );
            }
        }
        foreach (var pass in definition.PackagePasses) {
            if (!m_packages.TryGet(
                id: (pass.Package ?? string.Empty),
                package: out var package
            )) {
                Add(
                    code: "RENDERGRAPH_PACKAGE_UNKNOWN",
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' names package '{pass.Package}', which the host does not offer.",
                    name: pass.Name
                );

                continue;
            }
            if (
                (pass.InputReferences.Count != package.Inputs.Count) ||
                (pass.OutputReferences.Count != package.Outputs.Count)
            ) {
                Add(
                    code: "RENDERGRAPH_PACKAGE_PORTS",
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' binds {pass.InputReferences.Count} input(s) and {pass.OutputReferences.Count} output(s); package '{package.Id}' has {package.Inputs.Count} and {package.Outputs.Count}.",
                    name: pass.Name
                );
            }
            if (pass.InputReferences.Concat(second: pass.OutputReferences).Any(predicate: static reference => (reference.As is not null))) {
                Add(
                    code: "RENDERGRAPH_PACKAGE_AS",
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' names a port with \"as\"; a package compiles no source, so nothing reads a port by name.",
                    name: pass.Name
                );
            }
            CheckPorts(
                code: "RENDERGRAPH_PACKAGE_OUTPUT",
                diagnostics: diagnostics,
                direction: "output",
                package: package,
                pass: pass,
                ports: package.Outputs,
                references: pass.OutputReferences,
                resources: resources
            );
            CheckPorts(
                code: "RENDERGRAPH_PACKAGE_INPUT",
                diagnostics: diagnostics,
                direction: "input",
                package: package,
                pass: pass,
                ports: package.Inputs,
                references: pass.InputReferences,
                resources: resources
            );
            if (package.Fragment is { } inputFragment) {
                foreach (var part in inputFragment.Passes) {
                    for (var index = 0; (index < Math.Min(part.Inputs.Count, part.InputAccesses.Count)); index++) {
                        if (part.InputAccesses[index] != RenderGraphPortAccess.ComputeReadWrite) { continue; }
                        var port = IndexOf(list: inputFragment.InputVersions, name: part.Inputs[index].Name);
                        if ((port >= 0) && (package.Inputs[port].Access == RenderGraphPortAccess.ComputeReadWrite)) { continue; }
                        Add(diagnostics, "RENDERGRAPH_MUTABLE_INPUT",
                            $"Fragment pass '{pass.Name}${part.Name}' updates '{part.Inputs[index].Name}' without a mutable package input port.", pass.Name);
                    }
                }
            }
            // A fragment's output version forwards what the fragment's version forwards, so the version a pass binds to it
            // starts no chain of its own.
            if (package.Fragment is not null) {
                foreach (var output in pass.OutputReferences) {
                    if (
                        resources.TryGetValue(
                            key: output.Name,
                            value: out var bound
                        ) &&
                        (bound.From is not null)
                    ) {
                        Add(
                            code: "RENDERGRAPH_PACKAGE_OUTPUT",
                            diagnostics: diagnostics,
                            message: $"Package pass '{pass.Name}' binds '{bound.Name}', which forwards '{bound.From}', to an output of package '{package.Id}', whose passes write it from the start of the frame.",
                            name: bound.Name
                        );
                    }
                }
            }
            if (!TryBindConfig(
                config: out _,
                package: package,
                pass: pass,
                reason: out var reason
            )) {
                Add(
                    code: "RENDERGRAPH_PACKAGE_CONFIG",
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' config does not bind to package '{package.Id}': {reason}",
                    name: pass.Name
                );
            }
        }
    }
    // Binds a pass's config against its package's schema: the schema with each field defaulting to the pass's value, so
    // the planned frame block starts from it; null for a package that takes no config and a pass that gives none.
    private static bool TryBindConfig(RenderGraphPackagePass pass, RenderGraphPackage package, out IReadOnlyDictionary<string, ShaderConfigField>? config, out string reason) {
        config = null;

        if (package.Config is not { } schema) {
            reason = "the package takes no config";

            return (pass.Config is null);
        }
        if (!ShaderConfigBinding.TryBind(
            config: pass.Config,
            ownerName: pass.Name,
            reason: out reason,
            schema: schema,
            values: out var values
        )) {
            return false;
        }

        var json = values.ToJson();

        config = schema.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: pair => (pair.Value with { Default = json.GetProperty(propertyName: pair.Key) }),
            keySelector: static pair => pair.Key
        );

        return true;
    }
    // Each version a pass binds must carry what its port carries: its kind, and a buffer port's stride and count. A pass
    // binding the wrong number of versions is refused by its port count instead, so only the ports both sides name are
    // compared.
    private static void CheckPorts(string code, string direction, RenderGraphPackagePass pass, RenderGraphPackage package, IReadOnlyList<RenderGraphPackagePort> ports, IReadOnlyList<ResourceReference> references, Dictionary<string, ShaderPipelineResource> resources, List<ShaderPipelineDiagnostic> diagnostics) {
        for (var index = 0; (index < Math.Min(val1: ports.Count, val2: references.Count)); index++) {
            var port = ports[index];

            if (
                resources.TryGetValue(
                    key: references[index].Name,
                    value: out var resource
                ) &&
                !port.Accepts(resource: resource)
            ) {
                Add(
                    code: code,
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' binds '{resource.Name}', carrying {RenderGraphPackagePort.Describe(count: resource.Count, kind: resource.Kind, strideBytes: resource.StrideBytes)}, to {direction} port {index} of package '{package.Id}', which carries {RenderGraphPackagePort.Describe(count: port.Count, kind: port.Kind, strideBytes: port.StrideBytes)}.",
                    name: resource.Name
                );
            }
        }
    }

    /// <summary>Validates and plans a graph.</summary>
    /// <param name="definition">The graph document.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ShaderPipelineCompilationException">The graph names an unknown package, binds a package's
    /// ports wrongly, or fails the planner; every diagnostic is carried, a package refusal with a
    /// <c>RENDERGRAPH_</c> code and a planner refusal with its <c>SHADERPIPE_</c> code.</exception>
    public RenderGraphPlan Compile(RenderGraphDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var diagnostics = new List<ShaderPipelineDiagnostic>();

        Validate(
            definition: definition,
            diagnostics: diagnostics
        );

        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }

        var packageByPass = new Dictionary<string, RenderGraphPackage>(comparer: StringComparer.Ordinal);
        var packagePasses = new List<ShaderPipelinePackagePass>(capacity: definition.PackagePasses.Count);
        var resources = definition.Resources.ToList();

        foreach (var pass in definition.PackagePasses) {
            m_packages.TryGet(
                id: pass.Package,
                package: out var package
            );
            TryBindConfig(
                config: out var config,
                package: package!,
                pass: pass,
                reason: out _
            );

            if (package!.Fragment is { } fragment) {
                Splice(
                    config: config,
                    fragment: fragment,
                    package: package,
                    packageByPass: packageByPass,
                    packagePasses: packagePasses,
                    pass: pass,
                    resources: resources
                );

                continue;
            }

            packageByPass.TryAdd(
                key: pass.Name,
                value: package
            );
            packagePasses.Add(item: new ShaderPipelinePackagePass(
                Config: config,
                CountsKernelWork: package.CountsKernelWork,
                InputAccesses: [.. package.Inputs.Select(selector: static port => port.Access)],
                Inputs: pass.InputReferences,
                Members: package.Members,
                Name: pass.Name,
                OutputAccesses: [.. package.Outputs.Select(selector: static port => port.Access)],
                Outputs: pass.OutputReferences,
                Package: pass.Package,
                PushesIndex: package.PushesIndex
            ));
        }

        var pipeline = m_planner.Compile(
            definition: (packagePasses.Any(predicate: static pass => (pass.Part is not null))
                ? definition with { Resources = resources }
                : definition),
            packages: packagePasses
        );
        var steps = pipeline.Passes.Select(selector: planned => new RenderGraphStep(
            Package: (packageByPass.TryGetValue(
                key: planned.Name,
                value: out var package
            )
                ? package
                : null),
            Planned: planned
        )).ToArray();

        return new RenderGraphPlan(
            definition: definition,
            pipeline: pipeline,
            steps: Array.AsReadOnly(array: steps)
        );
    }

    // Splices a fragment package's passes in place of the pass naming it (RenderGraphPackageFragment): its own versions
    // join the graph's resources under spliced names, a name standing for an input port reads the version the pass binds
    // to that port, and a version standing for an output port is the version the pass binds to it, declared as the graph
    // declares it but forwarding what the fragment's version forwards. Each fragment pass plans as a package pass of the
    // package, laid out by its members and config, with the fragment pass as its part.
    private static void Splice(RenderGraphPackagePass pass, RenderGraphPackage package, RenderGraphPackageFragment fragment, IReadOnlyDictionary<string, ShaderConfigField>? config, Dictionary<string, RenderGraphPackage> packageByPass, List<ShaderPipelinePackagePass> packagePasses, List<ShaderPipelineResource> resources) {
        var names = new Dictionary<string, ResourceReference>(comparer: StringComparer.Ordinal);

        foreach (var resource in fragment.Resources) {
            names[resource.Name] = new ResourceReference(Name: RenderGraphPackageFragment.Spliced(
                name: resource.Name,
                pass: pass.Name
            ));
        }
        for (var port = 0; (port < fragment.InputVersions.Count); port++) {
            names[fragment.InputVersions[port]] = pass.InputReferences[port];
        }
        for (var port = 0; (port < fragment.OutputVersions.Count); port++) {
            names[fragment.OutputVersions[port]] = new ResourceReference(Name: pass.OutputReferences[port].Name);
        }

        ResourceReference Rename(ResourceReference reference) => (names[reference.Name] with {
            PreviousFrame = (reference.PreviousFrame || names[reference.Name].PreviousFrame),
        });

        foreach (var resource in fragment.Resources) {
            var from = ((resource.From is { } predecessor)
                ? names[predecessor].Name
                : null);
            var port = IndexOf(
                list: fragment.OutputVersions,
                name: resource.Name
            );

            if (port < 0) {
                resources.Add(item: resource with {
                    From = from,
                    Name = names[resource.Name].Name,
                });

                continue;
            }

            var bound = pass.OutputReferences[port].Name;
            var declared = resources.FindIndex(match: candidate => string.Equals(
                a: candidate.Name,
                b: bound,
                comparisonType: StringComparison.Ordinal
            ));

            if (declared >= 0) {
                resources[declared] = resources[declared] with { From = from };
            }
        }
        foreach (var part in fragment.Passes) {
            var name = RenderGraphPackageFragment.Spliced(
                name: part.Name,
                pass: pass.Name
            );

            packageByPass.TryAdd(
                key: name,
                value: package
            );
            packagePasses.Add(item: new ShaderPipelinePackagePass(
                Config: config,
                CountsKernelWork: part.CountsKernelWork,
                Dispatch: ((part.Dispatch is { Arguments: { } arguments } dispatch)
                    ? dispatch with { Arguments = names[arguments].Name }
                    : part.Dispatch),
                InputAccesses: part.InputAccesses,
                Inputs: [.. part.Inputs.Select(selector: Rename)],
                Members: (part.Members ?? package.Members),
                Name: name,
                OutputAccesses: part.OutputAccesses,
                Outputs: [.. part.Outputs.Select(selector: Rename)],
                Package: package.Id,
                Part: part.Name,
                PushesIndex: package.PushesIndex
            ));
        }
    }
    private static int IndexOf(IReadOnlyList<string> list, string name) {
        for (var index = 0; (index < list.Count); index++) {
            if (string.Equals(
                a: list[index],
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Validates and plans a graph without throwing for an authored refusal.</summary>
    /// <param name="definition">The graph document.</param>
    /// <param name="plan">The plan, when this returns <see langword="true"/>.</param>
    /// <param name="diagnostics">Every refusal, empty when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the graph planned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool TryCompile(RenderGraphDefinition definition, [NotNullWhen(returnValue: true)] out RenderGraphPlan? plan, out IReadOnlyList<ShaderPipelineDiagnostic> diagnostics) {
        try {
            plan = Compile(definition: definition);
            diagnostics = [];

            return true;
        } catch (ShaderPipelineCompilationException exception) {
            plan = null;
            diagnostics = exception.Diagnostics;

            return false;
        }
    }
}
