using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The composed <c>views.post</c> passes of the default render graph's root instance, keyed by their row names:
/// a presentation-scope parameter binding's only post-pass write target. The render root attaches the root instance once
/// it is composed; until then, and in a boot shape that composes no presentation (headless), every write is refused, so a
/// parameter binding's write there is a harmless no-op. Each pass keeps its row's config with every field a binding has
/// written, and a write rebinds that whole config.</summary>
public sealed class WorldPostPasses {
    // Each composed pass's live config, by pass name: its row's config with every field a binding has written.
    private readonly Dictionary<string, JsonObject> m_live = new(comparer: StringComparer.Ordinal);

    private Func<WorldRootGraph?>? m_graph;
    // The graph m_live was taken from: a live views.post change recomposes the root, and the kept configs follow it.
    private WorldRootGraph? m_liveGraph;
    private Func<ShaderPipelineRenderNode?>? m_root;

    /// <summary>Attaches the root instance the default render graph's post passes record in.</summary>
    /// <param name="graph">Reads the composed default graph the root runs now, whose <see cref="WorldRootGraph.Post"/> rows
    /// each pass starts from: when it changes, as a live <c>views.post</c> edit recomposes it, every kept config starts
    /// again from the new rows. <see langword="null"/> when the document names its own root.</param>
    /// <param name="root">Reads the synthesized root instance's node, which is <see langword="null"/> while the graph
    /// draws nothing over the world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="root"/> is
    /// <see langword="null"/>.</exception>
    public void Attach(Func<WorldRootGraph?> graph, Func<ShaderPipelineRenderNode?> root) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: root);

        m_graph = graph;
        m_liveGraph = null;
        m_root = root;
        Follow();
    }

    // Starts every kept config again from the running graph's rows when the root has been recomposed since.
    private void Follow() {
        var graph = m_graph?.Invoke();

        if (
            (m_liveGraph is not null) &&
            ReferenceEquals(
                objA: graph,
                objB: m_liveGraph
            )
        ) {
            return;
        }

        m_liveGraph = graph;
        m_live.Clear();

        foreach (var pass in (graph?.Post ?? [])) {
            m_live[pass.Name] = ((pass.Config is { ValueKind: JsonValueKind.Object } config)
                ? JsonObject.Create(element: config)!
                : []);
        }
    }

    /// <summary>Updates a declared floating-point config field of one post pass, over the pass's own config; the pass's
    /// frame block carries it from the next frame the root renders.</summary>
    /// <param name="pass">The <c>views.post[].name</c> of the pass.</param>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The value, which must be finite and inside the field's range.</param>
    /// <returns><see langword="true"/> when the root has installed its graph and the pass bound the value;
    /// <see langword="false"/> otherwise, and the caller writes it again later.</returns>
    public bool TrySetConfig(string pass, string field, float value) => (
        (m_root?.Invoke() is { } root) &&
        TrySetConfig(
            field: field,
            pass: pass,
            value: value,
            write: (name, config) => root.TrySetConfig(
                config: config,
                passName: name,
                reason: out _
            )
        )
    );
    /// <summary>Updates a declared floating-point config field of one post pass, over the pass's own config, through a
    /// writer that rebinds the pass's whole config.</summary>
    /// <param name="pass">The <c>views.post[].name</c> of the pass.</param>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The value, which must be finite.</param>
    /// <param name="write">Rebinds one pass's whole config, by pass name, and returns whether it bound.</param>
    /// <returns><see langword="true"/> when the pass bound its config, which it then keeps; <see langword="false"/> when
    /// no graph is attached, the root composed no such pass, the value is not finite, or the pass refused its config,
    /// and its kept config does not change.</returns>
    /// <exception cref="ArgumentException"><paramref name="pass"/> or <paramref name="field"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pass"/>, <paramref name="field"/> or
    /// <paramref name="write"/> is <see langword="null"/>.</exception>
    public bool TrySetConfig(string pass, string field, float value, Func<string, JsonElement, bool> write) {
        ArgumentException.ThrowIfNullOrEmpty(argument: pass);
        ArgumentException.ThrowIfNullOrEmpty(argument: field);
        ArgumentNullException.ThrowIfNull(argument: write);
        Follow();

        if (
            !m_live.TryGetValue(
                key: pass,
                value: out var current
            ) ||
            !float.IsFinite(f: value)
        ) {
            return false;
        }

        var live = ((JsonObject)current.DeepClone());

        live[field] = value;

        using var document = JsonDocument.Parse(json: live.ToJsonString());

        if (!write(
            arg1: pass,
            arg2: document.RootElement.Clone()
        )) {
            return false;
        }

        m_live[pass] = live;

        return true;
    }
}
