using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

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
    /// <summary>Gets the pipeline planner's plan. A package pass appears in it as a compute pass whose source is
    /// <see cref="RenderGraphCompiler.PackageSourcePrefix"/> followed by its package id; it is ordered, given its
    /// accesses and barriers, and kept live exactly as a shader pass is.</summary>
    public ShaderPipelinePlan Pipeline { get; }
    /// <summary>Gets the planned passes in execution order, parallel to the planner's passes.</summary>
    public IReadOnlyList<RenderGraphStep> Steps { get; }
}
/// <summary>Validates a <c>puck.render.graph.v1</c> document and plans it with the pipeline planner. Package passes are
/// checked against the host's catalog and planned as passes that read and write their declared versions, so one
/// planner orders, versions and barriers every pass of a frame. It loads no source and creates no GPU object.</summary>
/// <param name="packages">The packages the host offers.</param>
/// <param name="limits">The planner's limits, or <see langword="null"/> for its defaults.</param>
public sealed class RenderGraphCompiler(RenderGraphPackageCatalog packages, ShaderPipelineLimits? limits = null) {
    /// <summary>The source a package pass carries in the planner's plan, followed by its package id.</summary>
    public const string PackageSourcePrefix = "package:";

    private readonly ShaderPipelineCompiler m_planner = new(limits: limits);
    private readonly RenderGraphPackageCatalog m_packages = (packages ?? throw new ArgumentNullException(paramName: nameof(packages)));

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

        foreach (var pass in definition.ShaderPasses) {
            if (pass?.Source?.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: PackageSourcePrefix
            ) == true) {
                Add(
                    code: "RENDERGRAPH_SOURCE_RESERVED",
                    diagnostics: diagnostics,
                    message: $"Shader pass '{pass.Name}' names source '{pass.Source}'; the '{PackageSourcePrefix}' prefix marks a package pass in the plan, so a shader pass names a file.",
                    name: pass.Name
                );
            }
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
                (pass.InputReferences.Count != package.Inputs) ||
                (pass.OutputReferences.Count != package.Outputs)
            ) {
                Add(
                    code: "RENDERGRAPH_PACKAGE_PORTS",
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' binds {pass.InputReferences.Count} input(s) and {pass.OutputReferences.Count} output(s); package '{package.Id}' has {package.Inputs} and {package.Outputs}.",
                    name: pass.Name
                );
            }
            if (pass.InputReferences.Concat(second: pass.OutputReferences).Any(predicate: static reference => reference.Binding.HasValue)) {
                Add(
                    code: "RENDERGRAPH_PACKAGE_BINDING",
                    diagnostics: diagnostics,
                    message: $"Package pass '{pass.Name}' declares a descriptor binding; a package binds its own descriptors.",
                    name: pass.Name
                );
            }
            foreach (var output in pass.OutputReferences) {
                if (
                    resources.TryGetValue(
                        key: output.Name,
                        value: out var resource
                    ) &&
                    (resource.Kind != ShaderPipelineResourceKind.Image)
                ) {
                    Add(
                        code: "RENDERGRAPH_PACKAGE_OUTPUT",
                        diagnostics: diagnostics,
                        message: $"Package pass '{pass.Name}' writes '{output.Name}', which is not an image; every package port carries an image.",
                        name: output.Name
                    );
                }
            }
            foreach (var input in pass.InputReferences) {
                if (
                    resources.TryGetValue(
                        key: input.Name,
                        value: out var resource
                    ) &&
                    (resource.Kind != ShaderPipelineResourceKind.Image)
                ) {
                    Add(
                        code: "RENDERGRAPH_PACKAGE_INPUT",
                        diagnostics: diagnostics,
                        message: $"Package pass '{pass.Name}' reads '{input.Name}', which is not an image; every package port carries an image.",
                        name: input.Name
                    );
                }
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
        var lowered = new List<ShaderPipelinePass>(collection: definition.ShaderPasses);

        foreach (var pass in definition.PackagePasses) {
            m_packages.TryGet(
                id: pass.Package,
                package: out var package
            );
            packageByPass.TryAdd(
                key: pass.Name,
                value: package!
            );
            lowered.Add(item: new ShaderPipelinePass(
                EntryPoint: pass.Package,
                Inputs: pass.InputReferences,
                Kind: ShaderPipelinePassKind.Compute,
                Name: pass.Name,
                Outputs: pass.OutputReferences,
                Source: (PackageSourcePrefix + pass.Package)
            ));
        }

        var pipeline = m_planner.Compile(definition: new ShaderPipelineDefinition(
            Config: null,
            Name: definition.Name,
            Outputs: definition.Outputs,
            Passes: lowered,
            Resources: definition.Resources,
            Schema: ShaderPipelineSchemas.Pipeline
        ));
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
