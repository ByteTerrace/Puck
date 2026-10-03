using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Sources;

namespace Puck.Hosting;

/// <summary>A validated set of render-graph instances and the order they render in: every producer an instance reads
/// within a frame comes before it. A read of an instance's own output, or a read marked
/// <see cref="RenderGraphRead.PreviousFrame"/>, takes the producer's previous completed frame, so it orders nothing
/// and may close a loop; a loop of same-frame reads is refused with every instance in it named. Image and buffer reads
/// order alike, and a read whose kind is not what its producer's output carries is refused naming both. An external
/// package may read buffers when its runtime runs a graph fragment; a producer that hands out images is validated by
/// the runtime that resolves its factory. An instance may read its own output and any instance's previous frame, and any instance may read an external producer's
/// previous frame. The set declares how deep its views nest (<see cref="NestingDepth"/>), which the reads never
/// decide.</summary>
public sealed class RenderGraphInstanceSet {
    /// <summary>The nesting depth a set declares when its caller names none.</summary>
    public const int DefaultNestingDepth = 3;
    /// <summary>The deepest nesting a set may declare: past it a set is refused
    /// (<see cref="RenderGraphInstanceRefusalCode.NestingDepthInvalid"/>).</summary>
    public const int MaxNestingDepth = 8;

    private readonly Dictionary<string, int> m_indexByName;

    private RenderGraphInstanceSet(IReadOnlyList<RenderGraphInstance> instances, Dictionary<string, int> indexByName, IReadOnlyList<IReadOnlyList<RenderGraphEdge>> reads, IReadOnlyList<int> order, int nestingDepth) {
        Instances = instances;
        m_indexByName = indexByName;
        NestingDepth = nestingDepth;
        Reads = reads;
        Order = order;
    }

    /// <summary>Gets the instances in declaration order.</summary>
    public IReadOnlyList<RenderGraphInstance> Instances { get; }
    /// <summary>Gets the graph's declared nesting depth, from 0 through <see cref="MaxNestingDepth"/>: the most screens
    /// deep a view of a world shows another view, so a portal seen through a portal renders recursively to this many
    /// levels and a face past it draws its fallback, and the most screens a hit on a rendered source continues through
    /// <see cref="RenderGraphHitWalk"/> when the caller passes it as the limit. It is declared, by the world that composes
    /// the set, and never derived from the reads, so two portals facing each other end at it.</summary>
    public int NestingDepth { get; }
    /// <summary>Gets the render order as indices into <see cref="Instances"/>: every same-frame producer precedes its
    /// consumers, and instances the reads leave unordered keep declaration order.</summary>
    public IReadOnlyList<int> Order { get; }
    /// <summary>Gets each instance's resolved reads, parallel to <see cref="Instances"/>.</summary>
    public IReadOnlyList<IReadOnlyList<RenderGraphEdge>> Reads { get; }

    private static RenderGraphInstanceRefusal Refuse(RenderGraphInstanceRefusalCode code, string message, params string[] instances) => new(
        Code: code,
        Instances: instances,
        Message: message
    );
    // Why an instance is not a well-formed source, or carries settings without being one; null when it is either a
    // well-formed source or no source at all. A source's cadence paces it, so it states no refresh of its own.
    private static string? SourceRefusal(RenderGraphInstance instance) {
        if (!instance.IsSource) {
            return ((instance.Settings is null)
                ? null
                : "carries settings, but only a source instance (package 'source.<producer id>') carries settings");
        }
        if (!ImageSourceProducerRegistry<IImageSourceProducer>.IsValidId(id: instance.SourceProducer)) {
            return $"is a source of package '{instance.ExternalPackage}', which names no producer id";
        }
        if (instance.Refresh != RenderGraphRefresh.EveryFrame) {
            return $"is a source of package '{instance.ExternalPackage}' with refresh divisor {instance.Refresh.Divisor} and hertz {instance.Refresh.Hertz}; a source is paced by its producer's cadence and refreshes on every frame it allows";
        }

        return ((instance.Output == ShaderPipelineResourceKind.Image)
            ? null
            : $"is a source of package '{instance.ExternalPackage}' whose output is {instance.Output}; a source's output is an image");
    }
    private static string[]? FindCycle(IReadOnlyList<RenderGraphInstance> instances, IReadOnlyList<IReadOnlyList<RenderGraphEdge>> reads) {
        var state = new byte[instances.Count];
        var stack = new List<int>();

        for (var start = 0; (start < instances.Count); start++) {
            if (
                (state[start] == 0) &&
                Visit(consumer: start)
            ) {
                var closing = stack[^1];
                var first = stack.IndexOf(item: closing);

                return [.. stack.Skip(count: first).Take(count: ((stack.Count - first) - 1)).Select(selector: index => instances[index].Name)];
            }
        }

        return null;

        bool Visit(int consumer) {
            state[consumer] = 1;
            stack.Add(item: consumer);

            foreach (var edge in reads[consumer]) {
                if (edge.PreviousFrame) {
                    continue;
                }
                if (state[edge.Producer] == 1) {
                    stack.Add(item: edge.Producer);

                    return true;
                }
                if (
                    (state[edge.Producer] == 0) &&
                    Visit(consumer: edge.Producer)
                ) {
                    return true;
                }
            }

            stack.RemoveAt(index: (stack.Count - 1));
            state[consumer] = 2;

            return false;
        }
    }
    private static int[] RenderOrder(IReadOnlyList<IReadOnlyList<RenderGraphEdge>> reads) {
        var count = reads.Count;
        var remaining = new int[count];
        var consumers = new List<int>[count];

        for (var index = 0; (index < count); index++) {
            consumers[index] = [];
        }
        for (var consumer = 0; (consumer < count); consumer++) {
            foreach (var edge in reads[consumer]) {
                if (!edge.PreviousFrame) {
                    remaining[consumer]++;
                    consumers[edge.Producer].Add(item: consumer);
                }
            }
        }

        var ready = new SortedSet<int>();
        var order = new int[count];
        var placed = 0;

        for (var index = 0; (index < count); index++) {
            if (remaining[index] == 0) {
                ready.Add(item: index);
            }
        }
        while (ready.Count != 0) {
            var next = ready.Min;

            ready.Remove(item: next);
            order[placed++] = next;

            foreach (var consumer in consumers[next]) {
                if (--remaining[consumer] == 0) {
                    ready.Add(item: consumer);
                }
            }
        }

        return order;
    }

    /// <summary>Validates <paramref name="instances"/> and orders them.</summary>
    /// <param name="instances">The instances, in declaration order.</param>
    /// <param name="set">The validated set, when this returns <see langword="true"/>.</param>
    /// <param name="refusal">The first refusal, when this returns <see langword="false"/>. A nesting depth outside its
    /// range is reported first, then shape refusals in declaration order, then any cycle.</param>
    /// <param name="nestingDepth">The set's declared nesting depth (<see cref="NestingDepth"/>), from 0 through
    /// <see cref="MaxNestingDepth"/>.</param>
    /// <returns><see langword="true"/> when the instances form a valid set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instances"/> or one of its entries is
    /// <see langword="null"/>.</exception>
    public static bool TryCreate(IReadOnlyList<RenderGraphInstance> instances, [NotNullWhen(returnValue: true)] out RenderGraphInstanceSet? set, [NotNullWhen(returnValue: false)] out RenderGraphInstanceRefusal? refusal, int nestingDepth = DefaultNestingDepth) {
        ArgumentNullException.ThrowIfNull(argument: instances);

        set = null;

        if (
            (nestingDepth < 0) ||
            (nestingDepth > MaxNestingDepth)
        ) {
            refusal = Refuse(
                code: RenderGraphInstanceRefusalCode.NestingDepthInvalid,
                message: $"Render-graph instances nest {nestingDepth} deep; a set nests from 0 through {MaxNestingDepth} deep."
            );

            return false;
        }

        var indexByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < instances.Count); index++) {
            var instance = instances[index];

            ArgumentNullException.ThrowIfNull(argument: instance);

            if (string.IsNullOrWhiteSpace(value: instance.Name)) {
                refusal = Refuse(
                    code: RenderGraphInstanceRefusalCode.NameMissing,
                    message: $"Render-graph instance {index} has no name."
                );

                return false;
            }
            if (!indexByName.TryAdd(
                key: instance.Name,
                value: index
            )) {
                refusal = Refuse(
                    RenderGraphInstanceRefusalCode.NameDuplicated,
                    $"Render-graph instance '{instance.Name}' is declared more than once.",
                    instance.Name
                );

                return false;
            }
            if ((instance.OutputExtent is { } extent) && ((extent.Width <= 0) || (extent.Height <= 0) || instance.IsSource || (instance.Output != ShaderPipelineResourceKind.Image))) {
                refusal = Refuse(RenderGraphInstanceRefusalCode.ExtentInvalid, $"Render-graph instance '{instance.Name}' has invalid authored output extent {extent.Width}x{extent.Height}; only a non-source image instance may declare positive dimensions.", instance.Name);
                return false;
            }
            if (!instance.Refresh.IsValid) {
                refusal = Refuse(
                    RenderGraphInstanceRefusalCode.RefreshInvalid,
                    $"Render-graph instance '{instance.Name}' refreshes with divisor {instance.Refresh.Divisor} and hertz {instance.Refresh.Hertz}; exactly one must be positive.",
                    instance.Name
                );

                return false;
            }
            if (instance.Passes < 1) {
                refusal = Refuse(
                    RenderGraphInstanceRefusalCode.PassesInvalid,
                    $"Render-graph instance '{instance.Name}' records {instance.Passes} passes; a render records at least one.",
                    instance.Name
                );

                return false;
            }
            if (SourceRefusal(instance: instance) is { } sourceRefusal) {
                refusal = Refuse(
                    RenderGraphInstanceRefusalCode.SourceDeclaration,
                    $"Render-graph instance '{instance.Name}' {sourceRefusal}.",
                    instance.Name
                );

                return false;
            }
        }

        var reads = new IReadOnlyList<RenderGraphEdge>[instances.Count];

        for (var consumer = 0; (consumer < instances.Count); consumer++) {
            var instance = instances[consumer];
            var declared = (instance.Reads ?? []);
            var edges = new RenderGraphEdge[declared.Count];
            var producers = new HashSet<int>();

            for (var index = 0; (index < declared.Count); index++) {
                var read = declared[index];

                if (!indexByName.TryGetValue(
                    key: (read.Producer ?? string.Empty),
                    value: out var producer
                )) {
                    refusal = Refuse(
                        RenderGraphInstanceRefusalCode.ProducerUnknown,
                        $"Render-graph instance '{instance.Name}' reads '{read.Producer}', which names no instance.",
                        instance.Name
                    );

                    return false;
                }
                if (!producers.Add(item: producer)) {
                    refusal = Refuse(
                        RenderGraphInstanceRefusalCode.ReadDuplicated,
                        $"Render-graph instance '{instance.Name}' reads '{read.Producer}' more than once.",
                        instance.Name,
                        read.Producer!
                    );

                    return false;
                }
                if (read.Kind != instances[producer].Output) {
                    refusal = Refuse(
                        RenderGraphInstanceRefusalCode.KindMismatch,
                        $"Render-graph instance '{instance.Name}' reads '{read.Producer}' as {read.Kind}, but its output is {instances[producer].Output}.",
                        instance.Name,
                        read.Producer!
                    );

                    return false;
                }

                edges[index] = new RenderGraphEdge(
                    Kind: read.Kind,
                    PreviousFrame: (read.PreviousFrame || (producer == consumer)),
                    Producer: producer
                );
            }

            reads[consumer] = Array.AsReadOnly(array: edges);
        }

        if (FindCycle(
            instances: instances,
            reads: reads
        ) is { } cycle) {
            refusal = Refuse(
                RenderGraphInstanceRefusalCode.SameFrameCycle,
                $"Render-graph instances read each other within one frame: {string.Join(
                    separator: " -> ",
                    values: cycle.Append(element: cycle[0])
                )}; mark one read previousFrame to take the producer's last completed frame.",
                cycle
            );

            return false;
        }

        var order = RenderOrder(reads: reads);

        refusal = null;
        set = new RenderGraphInstanceSet(
            indexByName: indexByName,
            instances: new ReadOnlyCollection<RenderGraphInstance>(list: [.. instances]),
            nestingDepth: nestingDepth,
            order: Array.AsReadOnly(array: order),
            reads: Array.AsReadOnly(array: reads)
        );

        return true;
    }
    /// <summary>Finds an instance by name.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>Its index in <see cref="Instances"/>, or -1 when no instance has that name.</returns>
    public int IndexOf(string name) => (m_indexByName.TryGetValue(
        key: name,
        value: out var index
    )
        ? index
        : -1
    );
}
/// <summary>A resolved read between two instances of a <see cref="RenderGraphInstanceSet"/>.</summary>
/// <param name="Producer">The index of the instance read.</param>
/// <param name="PreviousFrame">Whether the read takes the producer's previous completed frame: declared so, or a read
/// of the reader's own output.</param>
/// <param name="Kind">What the read carries, which is what the producer's output carries.</param>
public readonly record struct RenderGraphEdge(int Producer, bool PreviousFrame, ShaderPipelineResourceKind Kind = ShaderPipelineResourceKind.Image);
