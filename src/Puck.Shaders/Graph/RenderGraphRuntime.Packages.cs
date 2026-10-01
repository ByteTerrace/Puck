using Puck.Abstractions.Presentation;
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
    private static RenderGraphRuntimeGraph? PackageGraphOf(string package, out string? fault, RenderGraphPackageFragment? selected = null) {
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

        fragment = (selected ?? fragment);
        if ((fragment.InputVersions.Count != 0) || (fragment.OutputVersions.Count != 1)) {
            fault = "selected a fragment without exactly one output and no inputs";
            return null;
        }
        var version = fragment.OutputVersions[0];
        var output = fragment.Resources.FirstOrDefault(predicate: resource => string.Equals(
            a: resource.Name,
            b: version,
            comparisonType: StringComparison.Ordinal
        ));

        if (output is null) {
            fault = $"selected a fragment whose output '{version}' has no resource declaration";
            return null;
        }
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

        if (!new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [declared with { Fragment = fragment }])).TryCompile(
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
                !IsConverging(index: index) &&
                (m_sources[index] is null) &&
                (m_inputs[index].Length == 0) &&
                (m_graphs[index] is { } graph) &&
                PackagesUnchanged(
                    context: in context,
                    instance: m_set.Instances[index].Name,
                    plan: graph.Pipeline.Plan
                ) &&
                MatchesAllocatedExtent(frame: in frame, index: index)
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
    // Cadence may stand only after the scheduled pixel extent has installed. Fractions alone miss a display resize,
    // and a node still drawing its old graph while the new one builds must keep being polled until that build installs.
    private bool MatchesAllocatedExtent(int index, in RenderGraphFrame frame) {
        if (m_nodes[index] is not { IsReady: true } node) {
            return false;
        }
        if (node.Export is { } export) {
            return (node.Extent == (export.Width, export.Height));
        }

        if (m_set.Instances[index].OutputExtent is { } exact) {
            return (node.Extent == (((uint)exact.Width), ((uint)exact.Height)));
        }
        var (width, height) = m_history.Allocated(index: index);

        return (
            (width > 0.0) &&
            (height > 0.0) &&
            (node.Extent == (
                ((uint)RenderGraphExtent.Pixels(display: frame.DisplayWidth, fraction: width)),
                ((uint)RenderGraphExtent.Pixels(display: frame.DisplayHeight, fraction: height))
            ))
        );
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
        var factories = m_packages.Factories;

        for (var index = 0; (index < factories.Count); index++) {
            factories[index].OnDeviceLost();
        }
    }

    /// <summary>Returns a graph instance's latest completed image, the one its consumers bind, for a host that samples it
    /// the way a consumer does: in a submission of the frame it reads it in, with no lease, since the instance's node
    /// holds an image it published until two of its own submissions after a newer one.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="image">The image, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the instance renders a graph and has completed an image.</returns>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    public bool TryLatestImage(string instance, out Surface image) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var index = m_set.IndexOf(name: instance);

        image = default;

        if (
            (index < 0) ||
            (m_nodes[index] is null) ||
            (m_current[index].Frame < 0) ||
            !m_current[index].Image.IsSameDeviceImage
        ) {
            return false;
        }

        image = m_current[index].Image;

        return true;
    }

    // Starts the frame for every package's factory.
    private void PackagesBeginFrame(in FrameContext context) {
        var factories = m_packages.Factories;

        for (var index = 0; (index < factories.Count); index++) {
            factories[index].BeginFrame(context: in context);
        }
        RefreshPackageFragments();
    }
}
