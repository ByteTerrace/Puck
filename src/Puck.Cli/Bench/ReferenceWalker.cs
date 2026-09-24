namespace Puck.Cli.Bench;

/// <summary>One instruction form's published service on one scheduling model.</summary>
/// <param name="Latency">The largest relevant operand latency, in cycles.</param>
/// <param name="ReciprocalThroughput">The reciprocal throughput, in cycles per issue.</param>
internal readonly record struct ReferenceMeasurement(long Latency, decimal ReciprocalThroughput) {
    /// <summary>Gets the serialized service cost <c>q = max(1, ceil(L), ceil(T))</c>.</summary>
    public long Service => Math.Max(
        val1: 1L,
        val2: Math.Max(
            val1: Latency,
            val2: decimal.ToInt64(d: decimal.Ceiling(d: ReciprocalThroughput))
        )
    );
}
/// <summary>What pricing one symbol's maximum permitted path reached: the summed service, and the reasons the walk
/// could not establish a bound. A price with any reason is unresolved evidence, never a number to use.</summary>
/// <param name="Cycles">The maximum sum of instruction service over the symbol's permitted paths.</param>
/// <param name="Issues">Why the walk could not establish that sum, ordered; empty when it did.</param>
internal sealed record ReferencePrice(long Cycles, IReadOnlyList<string> Issues);
/// <summary>Walks a reference lowering's control-flow graphs and prices each kernel's maximum permitted path as the
/// sum of instruction service over it. The walk is integer arithmetic throughout.</summary>
/// <remarks>Nothing here invents a number: an instruction the scheduling model does not serve, a helper body absent
/// from the object, an indirect exit with no candidate target, a recursive call cycle, or a loop with no declared
/// source-contract bound each make the kernel unresolved evidence for that model.</remarks>
internal sealed class ReferenceWalker {
    private readonly ReferenceArchitecture m_architecture;
    private readonly Func<string, IReadOnlyList<ReferenceInstruction>> m_disassemble;

    private readonly Dictionary<string, ReferenceGraph?> m_graphs = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> m_traces = new(comparer: StringComparer.Ordinal);

    /// <summary>Creates a walker over one reference lowering.</summary>
    /// <param name="architecture">The lowering the symbols were compiled for.</param>
    /// <param name="disassemble">Returns one symbol's instructions, or an empty list when the object carries no body
    /// for it.</param>
    public ReferenceWalker(ReferenceArchitecture architecture, Func<string, IReadOnlyList<ReferenceInstruction>> disassemble) {
        m_architecture = architecture;
        m_disassemble = disassemble;
    }

    /// <summary>Returns every symbol reachable from one symbol's calls and tail calls, itself included.</summary>
    /// <param name="symbol">The symbol to close over.</param>
    public IReadOnlySet<string> Closure(string symbol) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        Close(
            seen: seen,
            symbol: symbol
        );
        return seen;
    }
    /// <summary>Returns every instruction form that appears anywhere in one symbol's call closure.</summary>
    /// <param name="symbol">The symbol to collect forms from.</param>
    public IReadOnlySet<string> Forms(string symbol) {
        var forms = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var name in Closure(symbol: symbol)) {
            if (m_graphs.GetValueOrDefault(key: name) is not { } graph) { continue; }
            foreach (var body in graph.Blocks.Values) {
                foreach (var row in body) { forms.Add(item: row.Form); }
            }
        }
        return forms;
    }
    /// <summary>Reads one symbol's control-flow graph, and every graph its calls and tail calls reach, into this
    /// walker. Returns null when the object carries no body for the symbol.</summary>
    /// <param name="symbol">The symbol to read.</param>
    public ReferenceGraph? Graph(string symbol) => OnDeepStack(work: () => Read(symbol: symbol));
    /// <summary>Prices one symbol's maximum permitted path under one scheduling model's published service.</summary>
    /// <param name="symbol">The symbol to price.</param>
    /// <param name="service">The published service of each instruction form the model serves.</param>
    /// <param name="loopBounds">The declared source-contract iteration bound of each symbol carrying a loop, keyed by
    /// symbol. A symbol whose graph is cyclic and which is absent here is unresolved evidence.</param>
    public ReferencePrice Price(string symbol, IReadOnlyDictionary<string, ReferenceMeasurement> service, IReadOnlyDictionary<string, long> loopBounds) => OnDeepStack(work: () => Price(
        loopBounds: loopBounds,
        memo: new(comparer: StringComparer.Ordinal),
        service: service,
        stack: [],
        symbol: symbol
    ));

    // The descent is as deep as the lowering's longest call chain, which a rooted runtime makes thousands of frames,
    // so it runs on a thread sized for the lowering rather than for this process's callers.
    private static T OnDeepStack<T>(Func<T> work) {
        var result = default(T)!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? fault = null;
        var thread = new Thread(
            maxStackSize: (1 << 30),
            start: () => {
                try { result = work(); } catch (Exception exception) { fault = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: exception); }
            }
        );

        thread.Start();
        thread.Join();
        fault?.Throw();
        return result;
    }
    private ReferenceGraph? Read(string symbol) {
        if (m_graphs.TryGetValue(
            key: symbol,
            value: out var known
        )) {
            return known;
        }
        m_graphs[symbol] = null;

        var rows = m_disassemble(arg: symbol);

        if (rows.Count == 0) { return null; }

        var graph = ReferenceLowering.Build(
            architecture: m_architecture,
            rows: rows
        );

        m_graphs[symbol] = graph;
        foreach (var name in Outgoing(graph: graph)) { _ = Read(symbol: name); }
        return graph;
    }

    /// <summary>Returns the instruction forms of the maximum path the last pricing of one symbol chose, in path
    /// order, trap instructions excluded. Empty until that symbol has been priced.</summary>
    /// <param name="symbol">The symbol whose path to read.</param>
    public IReadOnlyList<string> Trace(string symbol) => m_traces.GetValueOrDefault(
        defaultValue: [],
        key: symbol
    );

    private void Close(string symbol, HashSet<string> seen) {
        if (!seen.Add(item: symbol)) { return; }
        if (m_graphs.GetValueOrDefault(key: symbol) is not { } graph) { return; }
        foreach (var name in Outgoing(graph: graph)) {
            Close(
                seen: seen,
                symbol: name
            );
        }
    }
    private static IEnumerable<string> Outgoing(ReferenceGraph graph) => graph.Tails.Values.Concat(second: graph.Calls.Values
        .SelectMany(selector: static names => names)
        .Where(predicate: static name => !string.Equals(
            a: name,
            b: ReferenceGraph.IndirectCall,
            comparisonType: StringComparison.Ordinal
        )));
    private ReferencePrice Price(
        string symbol,
        IReadOnlyDictionary<string, ReferenceMeasurement> service,
        IReadOnlyDictionary<string, long> loopBounds,
        IReadOnlyList<string> stack,
        Dictionary<string, ReferencePrice> memo
    ) {
        if (memo.TryGetValue(
            key: symbol,
            value: out var known
        )) {
            return known;
        }
        if (stack.Contains(
            comparer: StringComparer.Ordinal,
            value: symbol
        )) {
            return new(
                Cycles: 0L,
                Issues: [$"recursive call cycle through {symbol}"]
            );
        }
        if (m_graphs.GetValueOrDefault(key: symbol) is not { } graph) {
            return new(
                Cycles: 0L,
                Issues: [$"no body for {symbol} in the reference object"]
            );
        }

        var issues = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var entryBlock = graph.Order[0];
        var direct = Reach(
            entryBlock: entryBlock,
            extra: new Dictionary<long, IReadOnlyList<long>>(),
            graph: graph
        );
        var arms = new Dictionary<long, IReadOnlyList<long>>();
        var stranded = graph.Unresolved.Where(predicate: direct.Contains).ToArray();

        // A jump table leaves no direct edge. Every block the direct walk cannot reach is a candidate arm of the
        // indirect jump, which is the conservative superset a switch dispatch actually admits.
        if (stranded.Length > 0) {
            IReadOnlyList<long> candidates = [.. graph.Order.Where(predicate: node => (!direct.Contains(item: node) && !IsPadding(
                graph: graph,
                start: node
            )))];

            foreach (var node in stranded) {
                arms.Add(
                    key: node,
                    value: candidates
                );
            }
        }

        var reachable = Reach(
            entryBlock: entryBlock,
            extra: arms,
            graph: graph
        );
        var edges = new Dictionary<long, IReadOnlyList<long>>();

        foreach (var node in reachable) {
            edges.Add(
                key: node,
                value: (IsThrowing(
                    graph: graph,
                    start: node
                )
                    ? []
                    : [.. graph.Edges.GetValueOrDefault(
                        defaultValue: [],
                        key: node
                    ).Concat(second: arms.GetValueOrDefault(
                        defaultValue: [],
                        key: node
                    )).Where(predicate: reachable.Contains)])
            );
        }

        var order = graph.Order.Where(predicate: reachable.Contains).ToArray();
        var unresolvedExits = graph.Unresolved.Count(predicate: node => (reachable.Contains(item: node) && (arms.GetValueOrDefault(key: node) is not { Count: > 0 })));

        if (unresolvedExits > 0) { issues.Add(item: $"{symbol}: {unresolvedExits} indirect exit(s) with no candidate target in the body"); }

        var weight = new Dictionary<long, long>();

        foreach (var start in order) {
            var total = 0L;
            var served = true;

            foreach (var row in graph.Blocks[start]) {
                if (ReferenceLowering.Classify(
                    architecture: m_architecture,
                    mnemonic: row.Mnemonic
                ) == ReferenceFlow.Trap) {
                    continue;
                }
                if (!service.TryGetValue(
                    key: row.Form,
                    value: out var measurement
                )) {
                    issues.Add(item: $"{symbol}: no published service for '{row.Form}'");
                    served = false;
                    break;
                }
                total += measurement.Service;
            }
            if (!served) {
                weight.Add(
                    key: start,
                    value: 0L
                );
                continue;
            }
            if (!IsThrowing(
                graph: graph,
                start: start
            )) {
                foreach (var name in graph.Calls.GetValueOrDefault(
                    defaultValue: [],
                    key: start
                )) {
                    if (string.Equals(
                        a: name,
                        b: ReferenceGraph.IndirectCall,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        issues.Add(item: $"{symbol}: indirect call with no resolvable target");
                        continue;
                    }
                    total += Charge(
                        issues: issues,
                        loopBounds: loopBounds,
                        memo: memo,
                        name: name,
                        service: service,
                        stack: stack,
                        symbol: symbol
                    );
                }
                if (graph.Tails.TryGetValue(
                    key: start,
                    value: out var tail
                )) {
                    total += Charge(
                        issues: issues,
                        loopBounds: loopBounds,
                        memo: memo,
                        name: tail,
                        service: service,
                        stack: stack,
                        symbol: symbol
                    );
                }
            }
            weight.Add(
                key: start,
                value: total
            );
        }

        var components = StronglyConnected(
            edges: edges,
            nodes: order
        );
        var owner = new Dictionary<long, int>();

        for (var position = 0; (position < components.Count); ++position) {
            foreach (var node in components[position]) {
                owner.Add(
                    key: node,
                    value: position
                );
            }
        }

        var best = new long[components.Count];
        var chosen = new int[components.Count];

        for (var position = 0; (position < components.Count); ++position) {
            var component = components[position];
            var cyclic = ((component.Count > 1) || component.Any(predicate: node => edges[node].Any(predicate: other => (other == node))));
            var repeats = 1L;

            if (cyclic) {
                if (loopBounds.TryGetValue(
                    key: symbol,
                    value: out var declared
                )) {
                    repeats = declared;
                } else {
                    issues.Add(item: $"{symbol}: loop with no declared source-contract iteration bound");
                }
            }

            var successors = component
                .SelectMany(selector: node => edges[node])
                .Select(selector: node => owner[node])
                .Where(predicate: other => (other != position))
                .Distinct()
                .Order()
                .ToArray();
            var top = 0L;
            var follow = -1;

            // Tarjan emits a component only after every component it reaches, so successors are already solved.
            foreach (var other in successors) {
                if ((other > position) || ((top > best[other]) || ((top == best[other]) && (follow >= other)))) { continue; }
                follow = other;
                top = best[other];
            }
            best[position] = ((repeats * component.Sum(selector: node => weight[node])) + top);
            chosen[position] = follow;
        }

        var entry = owner[entryBlock];
        var trace = new List<string>();

        for (var position = entry; (position >= 0); position = chosen[position]) {
            foreach (var node in components[position].Order()) {
                foreach (var row in graph.Blocks[node]) {
                    if (ReferenceLowering.Classify(
                        architecture: m_architecture,
                        mnemonic: row.Mnemonic
                    ) == ReferenceFlow.Trap) {
                        continue;
                    }
                    trace.Add(item: row.Form);
                }
            }
        }
        m_traces[symbol] = trace;

        var priced = new ReferencePrice(
            Cycles: best[entry],
            Issues: [.. issues]
        );

        memo[symbol] = priced;
        return priced;
    }
    private long Charge(
        string symbol,
        string name,
        IReadOnlyDictionary<string, ReferenceMeasurement> service,
        IReadOnlyDictionary<string, long> loopBounds,
        IReadOnlyList<string> stack,
        Dictionary<string, ReferencePrice> memo,
        SortedSet<string> issues
    ) {
        var inner = Price(
            loopBounds: loopBounds,
            memo: memo,
            service: service,
            stack: [.. stack, symbol],
            symbol: name
        );

        issues.UnionWith(other: inner.Issues);
        return inner.Cycles;
    }
    private bool IsPadding(ReferenceGraph graph, long start) => graph.Blocks[start].All(predicate: row => (ReferenceLowering.Classify(
        architecture: m_architecture,
        mnemonic: row.Mnemonic
    ) == ReferenceFlow.Trap));
    private static bool IsThrowing(ReferenceGraph graph, long start) => ReferenceLowering.ContinuesIntoAThrow(names: graph.Calls.GetValueOrDefault(
        defaultValue: [],
        key: start
    ));
    // A block that calls a managed throw is terminal: reaching the call site is charged, and what the throw does
    // after it is a named exclusion.
    private static HashSet<long> Reach(ReferenceGraph graph, long entryBlock, IReadOnlyDictionary<long, IReadOnlyList<long>> extra) {
        var pending = new Stack<long>();
        var seen = new HashSet<long>();

        pending.Push(item: entryBlock);
        while (pending.TryPop(result: out var node)) {
            if (!seen.Add(item: node)) { continue; }
            if (IsThrowing(
                graph: graph,
                start: node
            )) {
                continue;
            }
            foreach (var next in graph.Edges.GetValueOrDefault(
                defaultValue: [],
                key: node
            )) {
                pending.Push(item: next);
            }
            foreach (var next in extra.GetValueOrDefault(
                defaultValue: [],
                key: node
            )) {
                pending.Push(item: next);
            }
        }
        return seen;
    }
    /// <summary>Returns the strongly connected components of one graph in reverse topological order, so a component
    /// is emitted only after every component it reaches.</summary>
    private static IReadOnlyList<IReadOnlyList<long>> StronglyConnected(IReadOnlyList<long> nodes, IReadOnlyDictionary<long, IReadOnlyList<long>> edges) {
        var index = new Dictionary<long, int>();
        var low = new Dictionary<long, int>();
        var on = new HashSet<long>();
        var stack = new List<long>();
        var result = new List<IReadOnlyList<long>>();
        var counter = 0;

        foreach (var root in nodes) {
            if (index.ContainsKey(key: root)) { continue; }

            var work = new List<(long Node, int Cursor)> { (root, 0) };

            index.Add(
                key: root,
                value: counter
            );
            low.Add(
                key: root,
                value: counter
            );
            ++counter;
            stack.Add(item: root);
            on.Add(item: root);
            while (work.Count > 0) {
                var (node, cursor) = work[^1];
                var children = edges.GetValueOrDefault(
                    defaultValue: [],
                    key: node
                );
                var advanced = false;

                while (cursor < children.Count) {
                    var child = children[cursor];

                    ++cursor;
                    work[^1] = (node, cursor);
                    if (!index.ContainsKey(key: child)) {
                        index.Add(
                            key: child,
                            value: counter
                        );
                        low.Add(
                            key: child,
                            value: counter
                        );
                        ++counter;
                        stack.Add(item: child);
                        on.Add(item: child);
                        work.Add(item: (child, 0));
                        advanced = true;
                        break;
                    }
                    if (on.Contains(item: child)) {
                        low[node] = Math.Min(
                            val1: low[node],
                            val2: index[child]
                        );
                    }
                }
                if (advanced) { continue; }
                work.RemoveAt(index: (work.Count - 1));
                if (work.Count > 0) {
                    var parent = work[^1].Node;

                    low[parent] = Math.Min(
                        val1: low[parent],
                        val2: low[node]
                    );
                }
                if (low[node] != index[node]) { continue; }

                var component = new List<long>();

                while (true) {
                    var member = stack[^1];

                    stack.RemoveAt(index: (stack.Count - 1));
                    on.Remove(item: member);
                    component.Add(item: member);
                    if (member == node) { break; }
                }
                result.Add(item: component);
            }
        }
        return result;
    }
}
