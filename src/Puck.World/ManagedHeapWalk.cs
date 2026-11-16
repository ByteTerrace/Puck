using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Puck.World;

/// <summary>
/// A suspended walk of this process's managed heap: the runtime's own heap dump, the technique <c>dotnet-gcdump</c>
/// uses. An EventPipe session this process opens on itself through its diagnostics port enables the runtime provider's
/// heap-collect and heap-dump keywords, which make the runtime run one blocking full collection with every managed
/// thread suspended and, during it, write each live object (its address, size, type and the objects it refers to) and
/// each root to the event stream. The walk reads the stream back and names the large object heap's tenants: each object
/// of at least the large object threshold (85,000 bytes) counts in the totals, and each of at least a stated size is
/// named by its type and the first holder found (the type of an object referring to it, or the kind of root that keeps
/// it), grouped by both. The collection compacts nothing it would not otherwise, and its pause is the walk's suspension;
/// reading the stream takes seconds on a heap of a few gigabytes, so it is taken on request, never per frame.
/// </summary>
internal static class ManagedHeapWalk {
    /// <summary>The runtime's large object threshold, in bytes: an object at least this large is allocated on the large
    /// object heap (the default of <c>GCLOHThreshold</c>, which Puck does not set).</summary>
    public const long LargeObjectBytes = 85_000;

    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>Walks the heap.</summary>
    /// <param name="minimumBytes">The smallest object whose tenant is named; smaller large objects count only in the
    /// totals.</param>
    /// <returns>The walk.</returns>
    /// <exception cref="InvalidOperationException">The runtime wrote no heap dump.</exception>
    public static HeapWalk Run(long minimumBytes) {
        var keywords = (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.GCHeapCollect | ClrTraceEventParser.Keywords.GCHeapDump | ClrTraceEventParser.Keywords.GCHeapAndTypeNames | ClrTraceEventParser.Keywords.Type);
        var client = new DiagnosticsClient(processId: Environment.ProcessId);
        using var session = client.StartEventPipeSession(providers: [new EventPipeProvider(eventLevel: EventLevel.Verbose, keywords: keywords, name: RuntimeProvider)], requestRundown: false, circularBufferMB: 1024);
        using var source = new EventPipeEventSource(stream: session.EventStream);

        var types = new Dictionary<ulong, string>();
        // Every node in the order the dump wrote it, with the index of its first edge; edges follow their nodes in order.
        var nodes = new List<(ulong Address, ulong Size, ulong Type, int FirstEdge, int Edges)>();
        var edges = new List<ulong>();
        var roots = new Dictionary<ulong, string>();
        var dumpCollection = -1;
        var done = false;

        source.Clr.GCStart += data => {
            if ((dumpCollection < 0) && (data.Depth == 2) && (data.Reason == GCReason.Induced)) { dumpCollection = data.Count; }
        };
        source.Clr.GCStop += data => {
            if (!done && (data.Count == dumpCollection)) {
                done = true;
                // Stopping from the reading thread would wait on itself; the stream ends once the session stops.
                _ = Task.Run(action: session.Stop);
            }
        };
        source.Clr.TypeBulkType += data => {
            for (var index = 0; (index < data.Count); index++) {
                var type = data.Values(index: index);

                types[type.TypeID] = type.TypeName;
            }
        };
        source.Clr.GCBulkNode += data => {
            for (var index = 0; (index < data.Count); index++) {
                var node = data.Values(index: index);
                var first = (nodes.Count == 0) ? 0 : (nodes[^1].FirstEdge + nodes[^1].Edges);

                nodes.Add(item: (node.Address, node.Size, node.TypeID, first, checked((int)node.EdgeCount)));
            }
        };
        source.Clr.GCBulkEdge += data => {
            for (var index = 0; (index < data.Count); index++) { edges.Add(item: data.Values(index: index).Target); }
        };
        source.Clr.GCBulkRootEdge += data => {
            for (var index = 0; (index < data.Count); index++) {
                var root = data.Values(index: index);

                _ = roots.TryAdd(key: root.RootedNodeAddress, value: $"root:{root.GCRootKind}");
            }
        };
        source.Process();

        if (!done) { throw new InvalidOperationException(message: "The runtime wrote no heap dump: the session ended before its collection finished."); }

        long objects = 0, bytes = 0;
        var large = new Dictionary<ulong, (string Type, ulong Size)>();

        foreach (var node in nodes) {
            if (((long)node.Size) < LargeObjectBytes) { continue; }
            objects++;
            bytes += ((long)node.Size);
            if (((long)node.Size) >= minimumBytes) { large[node.Address] = ((types.TryGetValue(key: node.Type, value: out var name) ? name : "?"), node.Size); }
        }

        var holders = new Dictionary<ulong, string>();

        if (large.Count != 0) {
            foreach (var node in nodes) {
                for (var edge = node.FirstEdge; (edge < Math.Min(val1: (node.FirstEdge + node.Edges), val2: edges.Count)); edge++) {
                    var target = edges[edge];

                    if (large.ContainsKey(key: target) && !holders.ContainsKey(key: target)) {
                        holders[target] = (types.TryGetValue(key: node.Type, value: out var name) ? name : "?");
                    }
                }
            }
        }

        var tenants = large
            .GroupBy(keySelector: entry => (entry.Value.Type, Holder: (holders.TryGetValue(key: entry.Key, value: out var holder) ? holder : (roots.TryGetValue(key: entry.Key, value: out var root) ? root : "unreached"))))
            .Select(selector: static group => new HeapTenant(Bytes: group.Sum(selector: static entry => ((long)entry.Value.Size)), Holder: group.Key.Holder, Objects: group.Count(), Type: group.Key.Type))
            .OrderByDescending(keySelector: static tenant => tenant.Bytes)
            .ThenBy(keySelector: static tenant => tenant.Type, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static tenant => tenant.Holder, comparer: StringComparer.Ordinal)
            .ToArray();

        return new(Bytes: bytes, Collection: dumpCollection, Nodes: nodes.Count, Objects: objects, Tenants: tenants);
    }
}
/// <summary>The large objects as one heap dump saw them, in bytes.</summary>
/// <param name="Objects">The live objects of at least <see cref="ManagedHeapWalk.LargeObjectBytes"/>.</param>
/// <param name="Bytes">Their bytes.</param>
/// <param name="Nodes">The live objects the dump wrote, of every size.</param>
/// <param name="Collection">The index of the collection that wrote the dump.</param>
/// <param name="Tenants">The tenants of objects at least the walk's minimum size, largest first.</param>
internal sealed record HeapWalk(long Objects, long Bytes, int Nodes, int Collection, IReadOnlyList<HeapTenant> Tenants);
/// <summary>One tenant of the large object heap: the objects of one type one holder keeps.</summary>
/// <param name="Type">The objects' type.</param>
/// <param name="Holder">The first holder the walk found: the type of an object referring to them, a <c>root:</c> kind,
/// or <c>unreached</c>.</param>
/// <param name="Objects">The objects.</param>
/// <param name="Bytes">Their bytes.</param>
internal sealed record HeapTenant(string Type, string Holder, int Objects, long Bytes);
