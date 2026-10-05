using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// Package instances. An external instance whose package neither an external producer nor an upload serves, but a
// recorder serves and runs as a fragment (RenderGraphPackageFragment), renders through a node like a
// graph instance, running a graph the runtime makes: one pass running the package, named by its id, exporting the
// fragment's outputs with the first as its default. So an SDF view (sdf.world) is a graph instance
// of its package's passes, planned, allocated and barriered by the one planner, with no graph document of its own.
//
// Before each frame is scheduled the runtime asks the package of every instance whose inputs stand unchanged and runs only
// package passes whether anything the instance renders from has changed since its latest render
// (IRenderGraphPackageFactory.IsUnchanged), and declares the instances none of whose packages saw a change unchanged
// (RenderGraphFrame.Unchanged), so their latest output stands for the frame. An instance a pending capture reads is never
// declared unchanged, since only a render serves it.
public sealed partial class RenderGraphRuntime {
    private static RenderGraphRuntimeGraph? PackageGraphFor(string package, string instance, RenderGraphPackageRecorders packages) {
        packages.TryGetFactory(factory: out var factory, package: package);
        return PackageGraphOf(package: package, fault: out _, selected: factory?.FragmentOf(instance: instance), inputs: factory?.InputsOf(instance: instance));
    }

    // The instances the frame being scheduled declares unchanged, reused frame to frame.
    private readonly List<string> m_unchanged = [];

    // Unlike ordinary cadence, a finite package operation may need its submitted image to remain immutable while
    // sibling producers advance. Only an own image whose whole graph consents can stand across those input changes.
    private bool PackagesHoldOutput(int index) {
        if (m_sources[index] is not null || m_producers[index] is not null || m_nodes[index] is not { IsReady: true } ||
            m_set.Instances[index].Output != ShaderPipelineResourceKind.Image || m_graphs[index] is not { } graph ||
            m_current[index] is not { Frame: >= 0, StandsFor.IsOwn: true, Publication.IsKnown: true } output ||
            graph.Pipeline.Plan.Passes.Count == 0) { return false; }
        var passes = graph.Pipeline.Plan.Passes;
        for (var position = 0; position < passes.Count; position++) {
            var pass = passes[position];
            if (pass.Package is not { } package || !m_packages.TryGetFactory(package.Package, out var factory) ||
                !factory.HoldsOutput(m_set.Instances[index].Name, output.Publication)) { return false; }
        }
        return true;
    }

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
    private static string? PackageRefusal(string package, string instance, RenderGraphPackageRecorders packages) {
        if (!packages.Serves(package: package)) {
            return "names a package neither an external producer, an upload nor a recorder serves";
        }

        packages.TryGetFactory(factory: out var factory, package: package);
        _ = PackageGraphOf(
            fault: out var fault,
            package: package,
            selected: factory?.FragmentOf(instance: instance)
        );

        return fault;
    }
    // Makes the one-pass graph a package instance renders, or returns why the package does not run as an instance: it
    // is no selected or catalog fragment with an exported output.
    private static RenderGraphRuntimeGraph? PackageGraphOf(string package, out string? fault, RenderGraphPackageFragment? selected = null, IReadOnlyList<RenderGraphRuntimeInput>? inputs = null) {
        if (
            !RenderGraphPackageCatalog.Engine.TryGet(
                id: package,
                package: out var declared
            ) ||
            ((selected ?? declared.Fragment) is not { } fragment) ||
            (fragment.OutputVersions.Count == 0)
        ) {
            fault = "is served by a recorder, but runs as no fragment with an exported output";

            return null;
        }

        var outputs = new ShaderPipelineResource[fragment.OutputVersions.Count];
        for (var index = 0; index < outputs.Length; index++) {
            var version = fragment.OutputVersions[index];
            var output = fragment.Resources.FirstOrDefault(predicate: resource => string.Equals(
                a: resource.Name, b: version, comparisonType: StringComparison.Ordinal));
            if (output is null) {
                fault = $"selected a fragment whose output '{version}' has no resource declaration";
                return null;
            }
            outputs[index] = output with { From = null, Transient = false };
        }
        var inputResources = fragment.InputVersions.Select(selector: name => fragment.Resources.Single(predicate: resource => (resource.Name == name))).ToArray();

        var definition = new RenderGraphDefinition(
            Name: package,
            Outputs: fragment.OutputVersions,
            Packages: [
                new RenderGraphPackagePass(
                    Name: package,
                    Inputs: [.. fragment.InputVersions.Select(selector: name => new ResourceReference(Name: name))],
                    Outputs: [.. fragment.OutputVersions.Select(static name => new ResourceReference(Name: name))],
                    Package: package
                ),
            ],
            Resources: [.. inputResources, .. outputs],
            Schema: RenderGraphSchemas.Graph
        );

        if (!new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [declared with {
            // The instance graph owns its external input declarations. Expansion contributes private and output
            // versions only; its input-port names refer to the declarations already in the graph.
            Fragment = ((inputResources.Length == 0) ? fragment : fragment with {
                Resources = [.. fragment.Resources.Where(predicate: resource => !fragment.InputVersions.Contains(value: resource.Name))],
            }),
            Inputs = [.. inputResources.Select(selector: resource => new RenderGraphPackagePort(Kind: resource.Kind,
                Access: FragmentAccess(fragment: fragment, name: resource.Name, input: true),
                StrideBytes: resource.StrideBytes, Count: resource.Count))],
            Outputs = [.. outputs.Select(resource => new RenderGraphPackagePort(Kind: resource.Kind,
                Access: FragmentAccess(fragment: fragment, name: resource.Name, input: false),
                StrideBytes: resource.StrideBytes, Count: resource.Count))],
        }])).TryCompile(
            definition: definition,
            diagnostics: out var diagnostics,
            plan: out var plan
        )) {
            fault = $"is served by a recorder, but its graph was refused: {string.Join(separator: "; ", values: diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}";

            return null;
        }

        fault = null;

        return new RenderGraphRuntimeGraph(
            Inputs: (inputs ?? []),
            Pipeline: new CompiledShaderPipeline(
                plan: plan.Pipeline,
                shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
            )
        );
    }
    private static RenderGraphPortAccess FragmentAccess(RenderGraphPackageFragment fragment, string name, bool input) {
        var selected = (input ? RenderGraphPortAccess.ComputeRead : RenderGraphPortAccess.ComputeWrite);
        foreach (var pass in fragment.Passes) {
            var versions = (input ? pass.Inputs : pass.Outputs);
            var accesses = (input ? pass.InputAccesses : pass.OutputAccesses);
            for (var index = 0; index < versions.Count; index++) {
                if (versions[index].Name != name) { continue; }
                if (accesses[index] == RenderGraphPortAccess.ComputeReadWrite) { return accesses[index]; }
                selected = accesses[index];
            }
        }
        return selected;
    }
    // The frame the scheduler reads: the frame as given, with the instances whose packages saw nothing change since their
    // latest render declared unchanged beside any the host declares.
    private RenderGraphFrame WithUnchanged(in RenderGraphFrame frame, in FrameContext context) {
        m_unchanged.Clear();

        var captured = CapturedInstance();

        for (var position = 0; (position < m_set.Order.Count); position++) {
            var index = m_set.Order[position];

            if (
                (index != captured) &&
                !IsConverging(index: index) &&
                (m_sources[index] is null) &&
                InputsUnchanged(index: index) &&
                (m_graphs[index] is { } graph) &&
                PackagesUnchanged(
                    context: in context,
                    instance: m_set.Instances[index].Name,
                    plan: graph.Pipeline.Plan,
                    unreadFrames: m_unreadFrames[index]
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
    private bool InputsUnchanged(int index) {
        var inputs = m_inputs[index];

        for (var position = 0; (position < inputs.Length); position++) {
            var input = inputs[position];

            if (input.PreviousFrame || !m_unchanged.Contains(item: input.ProducerName)) { return false; }
        }
        return true;
    }
    // Cadence may stand only after the current allocation revisions and scheduled pixel extent have installed.
    // A counter change must reach ProduceFrame's rebuild check even when the package's content is unchanged;
    // the node keeps being polled until the matching build installs, including buffer graphs with no pixel footprint.
    private bool MatchesAllocatedExtent(int index, in RenderGraphFrame frame) {
        if (m_nodes[index] is not { IsReady: true } node || node.CountsChanged) {
            return false;
        }
        if (m_set.Instances[index].Output == ShaderPipelineResourceKind.Buffer) {
            return !node.HasPendingCandidate;
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
    private bool PackagesUnchanged(ShaderPipelinePlan plan, string instance, long unreadFrames, in FrameContext context) {
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
                    instance: instance,
                    unreadFrames: unreadFrames
                )
            ) {
                return false;
            }
        }

        return true;
    }
    // Counts the frames an instance stays unread: nothing the display shows reaches it, though something still names it. The
    // scheduler already keeps an instance a held consumer shows or reads waiting, so only a parked row is unread.
    private void CountUnread(RenderGraphSchedule schedule) {
        var instances = schedule.Instances;

        for (var index = 0; (index < instances.Count); index++) {
            if (instances[index].Status == RenderGraphInstanceStatus.Unread) {
                m_unreadFrames[index]++;
            }
        }
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
    /// <returns><see langword="true"/> when the instance renders a graph and has completed an image: its own, or another
    /// instance's it stands for that its owner still keeps.</returns>
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
            (m_nodes[index] is null)
        ) {
            return false;
        }

        var latest = LatestOf(index: index);

        if (
            (latest.Frame < 0) ||
            !latest.Image.IsSameDeviceImage
        ) {
            return false;
        }

        image = latest.Image;

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
