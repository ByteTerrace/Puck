using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Puck.Hosting;

/// <summary>A validated set of render-graph instances and the order they render in: every producer an instance reads
/// within a frame comes before it. A read of an instance's own output, or a read marked
/// <see cref="RenderGraphRead.PreviousFrame"/>, takes the producer's previous completed frame, so it orders nothing
/// and may close a loop; a loop of same-frame reads is refused with every instance in it named. Image and buffer reads
/// order alike, and a read whose kind is not what its producer's output carries is refused naming both. An external
/// producer (<see cref="RenderGraphInstanceKind.External"/>) reads nothing and keeps no history, so a declared read of
/// its own, and another instance's previous-frame read of it, are each refused by name.</summary>
public sealed class RenderGraphInstanceSet {
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
    /// <summary>Gets the graph's nesting depth: the most same-frame reads chained one after another, such as 2 for a
    /// main view showing a screen that shows a nested world, and 0 when no instance reads another within the frame. A
    /// hit on a rendered source continues into the producer's camera at most this many times through
    /// <see cref="RenderGraphHitWalk"/> when the caller passes it as the limit.</summary>
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
    // The longest chain of same-frame reads, counted in reads, walked in render order so every producer's depth is
    // final before a consumer reads it.
    private static int LongestSameFrameChain(IReadOnlyList<IReadOnlyList<RenderGraphEdge>> reads, int[] order) {
        var depth = new int[reads.Count];
        var deepest = 0;

        foreach (var consumer in order) {
            foreach (var edge in reads[consumer]) {
                if (!edge.PreviousFrame) {
                    depth[consumer] = Math.Max(
                        val1: depth[consumer],
                        val2: (depth[edge.Producer] + 1)
                    );
                }
            }

            deepest = Math.Max(
                val1: deepest,
                val2: depth[consumer]
            );
        }

        return deepest;
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
    /// <param name="refusal">The first refusal, when this returns <see langword="false"/>. Shape refusals are reported
    /// in declaration order before any cycle.</param>
    /// <returns><see langword="true"/> when the instances form a valid set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instances"/> or one of its entries is
    /// <see langword="null"/>.</exception>
    public static bool TryCreate(IReadOnlyList<RenderGraphInstance> instances, [NotNullWhen(returnValue: true)] out RenderGraphInstanceSet? set, [NotNullWhen(returnValue: false)] out RenderGraphInstanceRefusal? refusal) {
        ArgumentNullException.ThrowIfNull(argument: instances);

        set = null;
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
            if (
                (instance.Kind == RenderGraphInstanceKind.External) &&
                (instance.Reads is { Count: > 0 })
            ) {
                refusal = Refuse(
                    RenderGraphInstanceRefusalCode.ExternalReads,
                    $"Render-graph instance '{instance.Name}' is the external producer '{instance.ExternalPackage}', which renders through its own submissions and reads no instance, but it declares {instance.Reads.Count} reads.",
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
                // An external producer keeps no history: it hands out its latest completed output, which its next
                // render overwrites, so a previous-frame read would sample the frame it is producing.
                if (
                    read.PreviousFrame &&
                    (instances[producer].Kind == RenderGraphInstanceKind.External)
                ) {
                    refusal = Refuse(
                        RenderGraphInstanceRefusalCode.ExternalPreviousFrame,
                        $"Render-graph instance '{instance.Name}' reads the previous frame of '{read.Producer}', the external producer '{instances[producer].ExternalPackage}', which hands out only its latest completed output.",
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
            nestingDepth: LongestSameFrameChain(
                order: order,
                reads: reads
            ),
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
