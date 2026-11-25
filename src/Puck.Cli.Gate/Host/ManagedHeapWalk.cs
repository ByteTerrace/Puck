using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Puck.Cli.Host;

/// <summary>
/// A suspended walk of another .NET process's managed heap: the runtime's own heap dump, the technique <c>dotnet-gcdump</c>
/// uses. An EventPipe session this process opens on the target through its diagnostics port enables the runtime provider's
/// heap-collect and heap-dump keywords, which make the runtime run one blocking full collection with every managed
/// thread suspended and, during it, write each live object (its address, size, type and the objects it refers to) and
/// each root to the event stream. The walk reads the stream back and names the large object heap's tenants: each object
/// of at least the large object threshold (85,000 bytes) counts in the totals, and each of at least a stated size is
/// named by its type and the first holder found (the type of an object referring to it, or the kind of root that keeps
/// it), grouped by both. The collection compacts nothing it would not otherwise, and its pause is the walk's suspension;
/// reading the stream takes seconds on a heap of a few gigabytes. The walk runs in the reading process, so the target
/// pays only the collection, and no engine process carries the trace-parsing package.
/// </summary>
public static class ManagedHeapWalk {
    /// <summary>The runtime's large object threshold, in bytes: an object at least this large is allocated on the large
    /// object heap (the default of <c>GCLOHThreshold</c>, which Puck does not set).</summary>
    public const long LargeObjectBytes = 85_000;

    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    // The most referrers a small-object group's chain names; its links mark a root or an unreached end with these
    // values, which no type id takes.
    private const int SmallDepth = 3;
    private const ulong RootLink = ulong.MaxValue;
    private const ulong UnreachedLink = (ulong.MaxValue - 1UL);

    /// <summary>Walks a process's heap.</summary>
    /// <param name="processId">The target process, which must expose its diagnostics port (the default of every .NET
    /// process) to this user.</param>
    /// <param name="minimumBytes">The smallest object whose tenant is named; smaller large objects count only in the
    /// totals.</param>
    /// <param name="depth">How many first referrers to name above each tenant's objects, at least one: the holder, the
    /// holder's holder, and so on.</param>
    /// <returns>The walk.</returns>
    /// <exception cref="InvalidOperationException">The runtime wrote no heap dump.</exception>
    /// <exception cref="ServerNotAvailableException">The process exposes no diagnostics port.</exception>
    public static HeapWalk Run(int processId, long minimumBytes, int depth = 1) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: depth, other: 1);
        var keywords = (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.GCHeapCollect | ClrTraceEventParser.Keywords.GCHeapDump | ClrTraceEventParser.Keywords.GCHeapAndTypeNames | ClrTraceEventParser.Keywords.Type);
        var client = new DiagnosticsClient(processId: processId);
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

        // Each named object's chain of first referrers, one level per pass over the edges: the object holding it, the
        // object holding that, and so on to the depth asked, ending early at a root or an object nothing refers to.
        var chains = large.Keys.ToDictionary(keySelector: static address => address, elementSelector: static _ => new List<string>());
        var reached = large.Keys.ToDictionary(keySelector: static address => address, elementSelector: static address => address);

        for (var level = 0; ((level < depth) && (reached.Count != 0)); level++) {
            var wanted = new Dictionary<ulong, List<ulong>>();

            foreach (var (named, address) in reached) {
                if (!wanted.TryGetValue(key: address, value: out var list)) { wanted[address] = list = []; }
                list.Add(item: named);
            }

            var referrers = new Dictionary<ulong, (ulong Address, string Type)>();

            foreach (var node in nodes) {
                for (var edge = node.FirstEdge; (edge < Math.Min(val1: (node.FirstEdge + node.Edges), val2: edges.Count)); edge++) {
                    var target = edges[edge];

                    if (wanted.ContainsKey(key: target) && !referrers.ContainsKey(key: target)) {
                        referrers[target] = (node.Address, (types.TryGetValue(key: node.Type, value: out var name) ? name : "?"));
                    }
                }
            }

            var next = new Dictionary<ulong, ulong>();

            foreach (var (address, named) in wanted) {
                var found = referrers.TryGetValue(key: address, value: out var referrer);
                var text = (found ? referrer.Type : (roots.TryGetValue(key: address, value: out var root) ? root : "unreached"));

                foreach (var key in named) {
                    chains[key].Add(item: text);
                    if (found) { next[key] = referrer.Address; }
                }
            }
            reached = next;
        }

        var tenants = large
            .GroupBy(keySelector: entry => (entry.Value.Type, Holder: string.Join(separator: " <- ", values: chains[entry.Key])))
            .Select(selector: static group => new HeapTenant(Bytes: group.Sum(selector: static entry => ((long)entry.Value.Size)), Holder: group.Key.Holder, Objects: group.Count(), Type: group.Key.Type) { Largest = group.Max(selector: static entry => ((long)entry.Value.Size)) })
            .OrderByDescending(keySelector: static tenant => tenant.Bytes)
            .ThenBy(keySelector: static tenant => tenant.Type, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static tenant => tenant.Holder, comparer: StringComparer.Ordinal)
            .ToArray();

        // The small objects, which the large tenants miss: every object under the large object threshold, summed by its
        // type and its chain of first referrers to --depth (at most SmallDepth), each link a referrer's type or the root
        // or unreached end, from one pass over the edges. A census of millions of small objects names its tenants this
        // way where a chain of names per object would not fit.
        var index = new Dictionary<ulong, int>(capacity: nodes.Count);

        for (var at = 0; (at < nodes.Count); at++) { _ = index.TryAdd(key: nodes[at].Address, value: at); }

        var firstReferrer = new int[nodes.Count];

        Array.Fill(array: firstReferrer, value: -1);
        for (var at = 0; (at < nodes.Count); at++) {
            var node = nodes[at];

            for (var edge = node.FirstEdge; (edge < Math.Min(val1: (node.FirstEdge + node.Edges), val2: edges.Count)); edge++) {
                if (index.TryGetValue(key: edges[edge], value: out var target) && (firstReferrer[target] < 0) && (target != at)) { firstReferrer[target] = at; }
            }
        }

        long liveBytes = 0, smallBytes = 0;
        var links = Math.Min(val1: depth, val2: SmallDepth);
        var small = new Dictionary<(ulong Type, ulong First, ulong Second, ulong Third), (int Objects, long Bytes)>();
        Span<ulong> chain = stackalloc ulong[SmallDepth];

        for (var at = 0; (at < nodes.Count); at++) {
            var node = nodes[at];

            liveBytes += ((long)node.Size);
            if (((long)node.Size) >= LargeObjectBytes) { continue; }
            smallBytes += ((long)node.Size);
            chain.Clear();

            var cursor = at;

            for (var link = 0; (link < links); link++) {
                var next = firstReferrer[cursor];

                if (next < 0) {
                    chain[link] = (roots.ContainsKey(key: nodes[cursor].Address) ? RootLink : UnreachedLink);
                    break;
                }
                chain[link] = nodes[next].Type;
                cursor = next;
            }

            var key = (node.Type, chain[0], chain[1], chain[2]);

            small[key] = (small.TryGetValue(key: key, value: out var sum) ? ((sum.Objects + 1), (sum.Bytes + ((long)node.Size))) : (1, ((long)node.Size)));
        }

        string TypeName(ulong type) => (types.TryGetValue(key: type, value: out var name) ? name : "?");
        string LinkName(ulong link) => link switch { RootLink => "root", UnreachedLink => "unreached", _ => TypeName(type: link) };
        string ChainName((ulong Type, ulong First, ulong Second, ulong Third) key) =>
            string.Join(separator: " <- ", values: new[] { key.First, key.Second, key.Third }.TakeWhile(predicate: static link => (link != 0UL)).Select(selector: LinkName));

        var smallTenants = small
            .Where(predicate: entry => (entry.Value.Bytes >= minimumBytes))
            .Select(selector: entry => new HeapTenant(
                Bytes: entry.Value.Bytes,
                Holder: ChainName(key: entry.Key),
                Objects: entry.Value.Objects,
                Type: TypeName(type: entry.Key.Type)
            ))
            .OrderByDescending(keySelector: static tenant => tenant.Bytes)
            .ThenBy(keySelector: static tenant => tenant.Type, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static tenant => tenant.Holder, comparer: StringComparer.Ordinal)
            .ToArray();

        return new(Bytes: bytes, Collection: dumpCollection, Nodes: nodes.Count, Objects: objects, Tenants: tenants) { LiveBytes = liveBytes, SmallBytes = smallBytes, SmallTenants = smallTenants };
    }
}
/// <summary>The large objects as one heap dump saw them, in bytes.</summary>
/// <param name="Objects">The live objects of at least <see cref="ManagedHeapWalk.LargeObjectBytes"/>.</param>
/// <param name="Bytes">Their bytes.</param>
/// <param name="Nodes">The live objects the dump wrote, of every size.</param>
/// <param name="Collection">The index of the collection that wrote the dump.</param>
/// <param name="Tenants">The tenants of objects at least the walk's minimum size, largest first.</param>
public sealed record HeapWalk(long Objects, long Bytes, int Nodes, int Collection, IReadOnlyList<HeapTenant> Tenants) {
    /// <summary>Gets the bytes of every live object the dump wrote, large and small.</summary>
    public long LiveBytes { get; init; }
    /// <summary>Gets the bytes of the live objects under <see cref="ManagedHeapWalk.LargeObjectBytes"/>.</summary>
    public long SmallBytes { get; init; }
    /// <summary>Gets the small objects summed by type and chain of first referrers to the walk's depth, at most three (each
    /// link a referrer's type, or <c>root</c> or <c>unreached</c> where the chain ends), each group of at least the walk's
    /// minimum size, largest first.</summary>
    public IReadOnlyList<HeapTenant> SmallTenants { get; init; } = [];
}
/// <summary>One tenant of the large object heap: the objects of one type one holder keeps.</summary>
/// <param name="Type">The objects' type.</param>
/// <param name="Holder">The first holder the walk found: the type of an object referring to them, a <c>root:</c> kind,
/// or <c>unreached</c>.</param>
/// <param name="Objects">The objects.</param>
/// <param name="Bytes">Their bytes.</param>
public sealed record HeapTenant(string Type, string Holder, int Objects, long Bytes) {
    /// <summary>Gets the largest object's bytes.</summary>
    public long Largest { get; init; }
}
