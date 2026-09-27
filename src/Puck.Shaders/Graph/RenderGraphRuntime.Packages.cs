using Puck.Hosting;

namespace Puck.Shaders;

// Package instances. An external instance whose package neither an external producer nor an upload serves, but a
// recorder serves and runs as a fragment with no input port (RenderGraphPackageFragment), renders through a node like a
// graph instance, running a graph the runtime makes: one pass running the package, named by its id, whose one output is
// the instance's, declared as the fragment declares its output version. So an SDF view (sdf.world) is a graph instance
// of its package's passes, planned, allocated and barriered by the one planner, with no graph document of its own.
//
// Before each frame is scheduled the runtime asks the package of every instance whose graph binds no input and runs only
// package passes whether anything the instance renders from has changed since its latest render
// (IRenderGraphPackageFactory.IsUnchanged), and declares the instances none of whose packages saw a change unchanged
// (RenderGraphFrame.Unchanged), so their latest output stands for the frame. An instance a pending capture reads is never
// declared unchanged, since only a render serves it.
public sealed partial class RenderGraphRuntime {
    // The instances the frame being scheduled declares unchanged, reused frame to frame.
    private readonly List<string> m_unchanged = [];

    // Whether an instance renders its package's graph on a node: an external instance whose package no producer or upload
    // serves and a recorder does.
    private static bool RunsPackage(RenderGraphInstance instance, RenderGraphPackageRecorders packages) => (
        (instance.Kind == RenderGraphInstanceKind.External) &&
        !packages.ServesProducer(package: instance.ExternalPackage!) &&
        !packages.ServesSource(package: instance.ExternalPackage!) &&
        packages.Serves(package: instance.ExternalPackage!)
    );
    // Why an external instance of a package no producer or upload serves cannot run, or null when it runs as a package
    // instance.
    private static string? PackageRefusal(string package, RenderGraphPackageRecorders packages) {
        if (!packages.Serves(package: package)) {
            return "names a package neither an external producer, an upload nor a recorder serves";
        }

        _ = PackageGraphOf(
            fault: out var fault,
            package: package
        );

        return fault;
    }
    // Makes the one-pass graph a package instance renders, or returns why the package does not run as an instance: it
    // is no fragment of the engine's catalog with no input port and one image output.
    private static RenderGraphRuntimeGraph? PackageGraphOf(string package, out string? fault) {
        if (
            !RenderGraphPackageCatalog.Engine.TryGet(
                id: package,
                package: out var declared
            ) ||
            (declared.Fragment is not { } fragment) ||
            (declared.Inputs.Count != 0) ||
            (fragment.OutputVersions.Count != 1)
        ) {
            fault = "is served by a recorder, but runs as no fragment with no input port and one output";

            return null;
        }

        var version = fragment.OutputVersions[0];
        var output = fragment.Resources.First(predicate: resource => string.Equals(
            a: resource.Name,
            b: version,
            comparisonType: StringComparison.Ordinal
        ));

        if (output.Kind != ShaderPipelineResourceKind.Image) {
            fault = $"is served by a recorder, but its output '{version}' is {output.Kind}, not an image";

            return null;
        }

        var definition = new RenderGraphDefinition(
            Name: package,
            Outputs: [version],
            Packages: [
                new RenderGraphPackagePass(
                    Name: package,
                    Outputs: [new ResourceReference(Name: version)],
                    Package: package
                ),
            ],
            Resources: [output with { From = null, Transient = false }],
            Schema: RenderGraphSchemas.Graph
        );

        if (!new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).TryCompile(
            definition: definition,
            diagnostics: out var diagnostics,
            plan: out var plan
        )) {
            fault = $"is served by a recorder, but its graph was refused: {string.Join(separator: "; ", values: diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}";

            return null;
        }

        fault = null;

        return new RenderGraphRuntimeGraph(
            Inputs: [],
            Pipeline: new CompiledShaderPipeline(
                plan: plan.Pipeline,
                shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
            )
        );
    }
    // The frame the scheduler reads: the frame as given, with the instances whose packages saw nothing change since their
    // latest render declared unchanged beside any the host declares.
    private RenderGraphFrame WithUnchanged(in RenderGraphFrame frame, in FrameContext context) {
        m_unchanged.Clear();

        var captured = CapturedInstance();

        for (var index = 0; (index < m_nodes.Length); index++) {
            if (
                (index != captured) &&
                (m_sources[index] is null) &&
                (m_inputs[index].Length == 0) &&
                (m_graphs[index] is { } graph) &&
                PackagesUnchanged(
                    context: in context,
                    instance: m_set.Instances[index].Name,
                    plan: graph.Pipeline.Plan
                )
            ) {
                m_unchanged.Add(item: m_set.Instances[index].Name);
            }
        }

        if (m_unchanged.Count == 0) {
            return frame;
        }
        if (frame.Unchanged is { } declared) {
            foreach (var name in declared) {
                if (!m_unchanged.Contains(item: name)) {
                    m_unchanged.Add(item: name);
                }
            }
        }

        return (frame with {
            Unchanged = m_unchanged,
        });
    }
    // Whether a plan runs only package passes, each of whose packages says nothing the instance renders from changed.
    private bool PackagesUnchanged(ShaderPipelinePlan plan, string instance, in FrameContext context) {
        var passes = plan.Passes;

        if (passes.Count == 0) {
            return false;
        }

        for (var position = 0; (position < passes.Count); position++) {
            if (
                (passes[position].Package is not { } step) ||
                !m_packages.TryGetFactory(
                    factory: out var factory,
                    package: step.Package
                ) ||
                !factory.IsUnchanged(
                    context: in context,
                    instance: instance
                )
            ) {
                return false;
            }
        }

        return true;
    }
    // Tells every package's factory the device was lost.
    private void PackagesLostDevice() {
        foreach (var factory in m_packages.Factories) {
            factory.OnDeviceLost();
        }
    }
}
