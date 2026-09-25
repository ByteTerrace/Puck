using System.Diagnostics.CodeAnalysis;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>A render-graph runtime's instance set without a device, for driving a <see cref="WorldViewGraphHost"/>: a
/// reconfiguration keeps the node of every graph instance it continues by name, creates one for each new graph instance
/// through the factory it was given, and disposes the node of every instance it drops; an install swaps the graph onto
/// the instance's node.</summary>
/// <param name="create">Creates the node of a new graph instance, by its name.</param>
internal sealed class FakeGraphInstances(Func<string, ShaderPipelineRenderNode> create) : IRenderGraphInstances, IDisposable {
    private readonly Dictionary<string, ShaderPipelineRenderNode> m_nodes = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets every graph the host installed, by instance, in order.</summary>
    public List<string> Installed { get; } = [];

    /// <summary>Gets how many reconfigurations the runtime accepted.</summary>
    public int Reconfigurations { get; private set; }

    /// <inheritdoc/>
    public string Root { get; private set; } = WorldViewGraphs.WorldInstance;

    /// <summary>Gets the instance set of the latest accepted reconfiguration, or <see langword="null"/> before one.</summary>
    public RenderGraphInstanceSet? Set { get; private set; }

    /// <summary>Attaches a host to a new fake whose nodes render nothing, over the default graph a world with no
    /// extensions and no overlay synthesizes.</summary>
    /// <param name="host">The host.</param>
    /// <param name="create">Creates the node of a new graph instance, by its name.</param>
    /// <returns>The fake.</returns>
    public static FakeGraphInstances Attach(WorldViewGraphHost host, Func<string, ShaderPipelineRenderNode> create) {
        var fake = new FakeGraphInstances(create: create);

        host.Attach(
            compose: static panes => WorldRootGraph.Compose(
                extensions: null,
                overlay: false,
                packages: RenderGraphPackageCatalog.Shipped,
                panes: panes
            ),
            runtime: fake,
            synthesized: WorldRootGraph.Compose(
                extensions: null,
                overlay: false,
                packages: RenderGraphPackageCatalog.Shipped
            )
        );

        return fake;
    }
    /// <inheritdoc/>
    public void Dispose() {
        foreach (var node in m_nodes.Values) {
            node.Dispose();
        }

        m_nodes.Clear();
    }
    /// <inheritdoc/>
    public ShaderPipelineRenderNode? NodeOf(string instance) => m_nodes.GetValueOrDefault(key: instance);
    /// <inheritdoc/>
    public bool TryInstall(string instance, RenderGraphRuntimeGraph graph, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
        m_nodes[instance].Swap(pipeline: graph.Pipeline);
        Installed.Add(item: instance);
        refusal = null;

        return true;
    }
    /// <inheritdoc/>
    public bool TryReconfigure(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, string root, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
        foreach (var name in m_nodes.Keys.ToArray()) {
            if (set.IndexOf(name: name) < 0) {
                m_nodes[name].Dispose();
                m_nodes.Remove(key: name);
            }
        }

        foreach (var instance in set.Instances) {
            if (
                (instance.Kind == RenderGraphInstanceKind.Graph) &&
                !m_nodes.ContainsKey(key: instance.Name)
            ) {
                m_nodes.Add(
                    key: instance.Name,
                    value: create(arg: instance.Name)
                );
            }
        }

        Reconfigurations++;
        Root = root;
        Set = set;
        refusal = null;

        return true;
    }
}
