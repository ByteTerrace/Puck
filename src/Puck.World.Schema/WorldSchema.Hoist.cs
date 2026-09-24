using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

// The split's hoist: every $ref the exporter emitted is expanded (duplicates the exporter caught and duplicates it
// regenerated end up alike), every repeated shape is grouped by content, and the first member of each group in
// document post-order becomes a common.schema.json def that every member then references.
//
// The expanded document is never materialized as a tree. A node that recurs by identity — a $ref target, or an
// inline export every occurrence of which is one shared node (see ExportNested) — expands the same way wherever the
// cycle cuts made inside it get the same answers from its ancestry, so its expansion is memoized against exactly
// the ancestry facts those cuts consulted, and a repeated expansion shares the first one's node. The result is a
// DAG whose unfolding is the expanded tree; grouping, occurrence counting, and def naming walk the DAG once per
// node, and only the reduced output — small, because every repeated shape in it is a $ref — is built as JSON.
public static partial class WorldSchema {
    private readonly record struct GroupKey(int Id, int Length);
    // One object or array of the expanded document. Shared by every position its expansion recurs at, so its derived
    // facts — group key, occurrence count — are computed once for all of them. A child is null (a JSON null), a
    // nested ExpandedNode, or the exporter's own leaf JsonNode, shared as-is since nothing mutates it.
    private sealed class ExpandedNode {
        public required object?[] Children { get; init; }
        public required bool IsArray { get; init; }
        // The member names when they differ from Source's (a cut marker, or a target with a reference's sibling
        // keywords laid over it).
        public string[]? OwnNames { get; init; }
        // The "$ref" string this object carries, when it carries one: a cut recursive marker, or any other reference
        // node. Such a node is opaque to grouping and reduction, exactly as a reference.
        public string? RefText { get; init; }
        // The exporter object whose members Children expands, index for index.
        public JsonObject? Source { get; init; }
        // The index of the exporter $ref target this node is an expansion of, or -1.
        public int Target { get; init; } = -1;
        public int CandidateKeyId { get; set; } = -1;
        public bool Collected { get; set; }
        public bool IsMember { get; set; }
        public int KeyId { get; set; } = -1;
        public int KeyLength { get; set; }
        public bool Mirrored { get; set; }
        public byte Multiplicity { get; set; }
        public Type? Type { get; init; }

        public bool HasMember(string name) =>
            (IndexOf(name: name) >= 0);
        public int IndexOf(string name) =>
            ((OwnNames is not null)
                ? Array.IndexOf(
                    array: OwnNames,
                    value: name
                )
                : (Source?.IndexOf(propertyName: name) ?? -1)
            );
        public object? Member(string name) {
            var index = IndexOf(name: name);

            return ((index >= 0)
                ? Children[index]
                : null
            );
        }
        public string NameAt(int index) =>
            (OwnNames?[index] ?? Source!.GetAt(index: index).Key);
    }
    // A memoized expansion of one recurring node: valid wherever the ancestry answers every probe in Probes the way
    // it did when the expansion was made (Answers holds the probes that were present).
    private sealed record ExpansionMemo(ulong[]? Probes, ulong[]? Answers, object Node);
    // A group's def: its name, the member whose reduced content it holds, and its type-level description.
    private sealed record DefGroup(string Name, ExpandedNode First, string? Summary);
    // Interns int sequences (a composite group key's signature) without allocating on a hit.
    private sealed class SignatureComparer : IEqualityComparer<int[]>, IAlternateEqualityComparer<ReadOnlySpan<int>, int[]> {
        public static SignatureComparer Instance { get; } = new();

        public int[] Create(ReadOnlySpan<int> alternate) =>
            alternate.ToArray();
        public bool Equals(int[]? x, int[]? y) =>
            ((x is not null) && (y is not null) && x.AsSpan().SequenceEqual(other: y));
        public bool Equals(ReadOnlySpan<int> alternate, int[] other) =>
            alternate.SequenceEqual(other: other);
        public int GetHashCode(int[] obj) =>
            GetHashCode(alternate: obj.AsSpan());
        public int GetHashCode(ReadOnlySpan<int> alternate) {
            var hash = new HashCode();

            hash.AddBytes(value: MemoryMarshal.AsBytes(span: alternate));

            return hash.ToHashCode();
        }
    }
    private sealed class HoistPass {
        private const int ArrayTag = -2;
        private const int ObjectTag = -1;

        private readonly List<DefGroup> m_defs = [];
        private readonly Dictionary<int, DefGroup> m_groups = [];
        private readonly Dictionary<string, GroupKey> m_leafByString = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, GroupKey> m_leafByText = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_markerKeyByPointer = new(comparer: StringComparer.Ordinal);

        private readonly List<JsonNode> m_inlineRoots;
        // The memoized expansions of every node that recurs by identity: an exporter $ref target (slot = its target
        // index) or an inline export (slot = target count + its root index).
        private readonly ExpansionMemo[][] m_memo;

        private readonly Dictionary<JsonNode, int> m_memoSlots = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, (int Id, int EscapedLength)> m_names = new(comparer: StringComparer.Ordinal);

        private readonly ulong[] m_presence;

        private readonly Dictionary<int, int> m_rawKeyCounts = [];
        private readonly Dictionary<int[], int> m_signatures = new(comparer: SignatureComparer.Instance);
        private readonly Dictionary<string, int> m_targetByPath = new(comparer: StringComparer.Ordinal);

        private readonly string?[] m_targetDefNames;
        private readonly JsonNode[] m_targetNodes;
        private readonly string[] m_targetPaths;
        private readonly int[] m_targetTypeBits;

        private readonly Dictionary<Type, int> m_typeBits = [];

        private readonly Dictionary<JsonNode, Type> m_typesByNode;

        private readonly HashSet<string> m_usedNames = new(comparer: StringComparer.Ordinal);

        private int m_nextKeyId;

        public HoistPass(JsonObject merged, ExportRun run) {
            var typesByNode = run.TypesByNode;

            m_inlineRoots = run.InlineRoots;
            m_typesByNode = typesByNode;

            // Every $ref target the exporter's OWN output carried, before any expansion: each one needs a def in the
            // split output no matter how often its content repeats, since a recursive shape's self-reference points
            // at exactly one of them.
            var targetPaths = new HashSet<string>(comparer: StringComparer.Ordinal);

            CollectRefTargets(
                node: merged,
                targets: targetPaths,
                visitedRoots: []
            );

            var nodes = new List<JsonNode>();

            foreach (var path in targetPaths) {
                if (ResolvePointer(
                    pointer: path,
                    root: merged
                ) is not { } node) {
                    continue;
                }

                if (!m_memoSlots.TryGetValue(
                    key: node,
                    value: out var index
                )) {
                    index = nodes.Count;
                    m_memoSlots[node] = index;
                    nodes.Add(item: node);
                }

                m_targetByPath[path] = index;
            }

            m_targetNodes = [.. nodes];
            m_targetDefNames = new string?[m_targetNodes.Length];
            m_targetTypeBits = new int[m_targetNodes.Length];
            m_memo = new ExpansionMemo[(m_targetNodes.Length + m_inlineRoots.Count)][];

            for (var index = 0; (index < m_inlineRoots.Count); index++) {
                m_memo[(m_targetNodes.Length + index)] = [];
                m_memoSlots.TryAdd(
                    key: m_inlineRoots[index],
                    value: (m_targetNodes.Length + index)
                );
            }

            // A cycle cut asks two ancestry questions of a target — is the target node itself, or any node of its
            // CLR type, being expanded — so those are the only ancestry facts that need tracking: one bit per
            // target node, then one per distinct target type.
            for (var index = 0; (index < m_targetNodes.Length); index++) {
                m_memo[index] = [];
                m_targetTypeBits[index] = -1;

                if (typesByNode.TryGetValue(
                    key: m_targetNodes[index],
                    value: out var type
                )) {
                    if (!m_typeBits.TryGetValue(
                        key: type,
                        value: out var bit
                    )) {
                        bit = (m_targetNodes.Length + m_typeBits.Count);
                        m_typeBits[type] = bit;
                    }

                    m_targetTypeBits[index] = bit;
                }
            }

            m_targetPaths = new string[m_targetNodes.Length];

            foreach (var (path, index) in m_targetByPath) {
                // Group keys spell a recursive marker by its target's TYPE name, so two clones of one recursive
                // shape reached through different expansion paths group together.
                m_markerKeyByPointer[path] = (typesByNode.TryGetValue(
                    key: m_targetNodes[index],
                    value: out var type
                )
                    ? $"cycle:{FriendlyTypeName(type: type)}"
                    : path
                );
                m_targetPaths[index] ??= path;
            }

            m_presence = new ulong[(((m_targetNodes.Length + m_typeBits.Count) + 63) / 64)];
        }

        public JsonObject CommonDefs { get; } = new();

        // Expands, groups, and reduces merged; returns the reduced document root. CommonDefs holds every def, in
        // creation order, with each recursive marker still carrying its original document pointer until
        // FixupCyclicMarkers repoints it.
        public JsonObject Run(JsonObject merged) {
            var root = ((ExpandedNode)Expand(
                probes: out _,
                source: merged
            ));
            var order = new List<ExpandedNode>();

            Collect(
                node: root,
                order: order
            );

            // Occurrence counts are the expanded TREE's, not the DAG's: a node's count is the sum of its parents'
            // over every edge, capped at two since grouping only asks whether a shape repeats. Reverse post-order
            // visits every parent before its children.
            root.Multiplicity = 1;

            for (var index = (order.Count - 1); (index >= 0); index--) {
                var node = order[index];

                foreach (var child in node.Children) {
                    if (IsDescended(
                        child: child,
                        node: out var descended
                    )) {
                        descended.Multiplicity = ((byte)Math.Min(
                            val1: 2,
                            val2: (descended.Multiplicity + node.Multiplicity)
                        ));
                    }
                }
            }

            foreach (var node in order) {
                if (node.CandidateKeyId >= 0) {
                    m_rawKeyCounts[node.CandidateKeyId] = Math.Min(
                        val1: 2,
                        val2: (m_rawKeyCounts.GetValueOrDefault(key: node.CandidateKeyId) + node.Multiplicity)
                    );
                }
            }

            foreach (var node in order) {
                node.IsMember = ((node.CandidateKeyId >= 0) && ((m_rawKeyCounts[node.CandidateKeyId] >= 2) || (node.Target >= 0)));
            }

            NameDefs(order: order);

            foreach (var group in m_defs) {
                CommonDefs[group.Name] = BuildDef(group: group);
            }

            var reduced = ((JsonObject)Reduce(node: root));

            Mirror(node: root);

            return reduced;
        }
        // The def each exporter $ref target's last expansion (in document order) was hoisted to, by original pointer.
        public Dictionary<string, string> TargetDefNames() {
            var names = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var (path, index) in m_targetByPath) {
                if (m_targetDefNames[index] is { } name) {
                    names[path] = name;
                }
            }

            return names;
        }

        // Every $ref target anywhere in node, regardless of what other keywords sit alongside "$ref" on the same node
        // (see TryGetAbsoluteRefTarget) — walked over the document exactly as the exporter produced it, before any
        // expansion, each inline export once.
        private void CollectRefTargets(JsonNode node, HashSet<string> targets, HashSet<int> visitedRoots) {
            if (TryGetInlineRoot(
                node: node,
                root: out var inlineRoot
            )) {
                if (visitedRoots.Add(item: inlineRoot)) {
                    CollectRefTargets(
                        node: m_inlineRoots[inlineRoot],
                        targets: targets,
                        visitedRoots: visitedRoots
                    );
                }
            } else if (node is JsonObject obj) {
                if (TryGetAbsoluteRefTarget(
                    refObj: obj,
                    target: out var target
                )) {
                    targets.Add(item: target);
                }

                for (var index = 0; (index < obj.Count); index++) {
                    var (key, value) = obj.GetAt(index: index);

                    if (
                        !string.Equals(
                        a: key,
                        b: "$ref",
                        comparisonType: StringComparison.Ordinal
                    ) &&
                        (value is not null)
                    ) {
                        CollectRefTargets(
                            node: value,
                            targets: targets,
                            visitedRoots: visitedRoots
                        );
                    }
                }
            } else if (node is JsonArray arr) {
                for (var index = 0; (index < arr.Count); index++) {
                    if (arr[index] is { } value) {
                        CollectRefTargets(
                            node: value,
                            targets: targets,
                            visitedRoots: visitedRoots
                        );
                    }
                }
            }
        }
        // ---- expansion ------------------------------------------------------------------------------------------

        // Depth-first expansion of every $ref, cutting a genuinely recursive one: its target is being expanded
        // already, or a node of its target's CLR type is. The type test matters because a polymorphic union whose
        // arms each hold the union again is regenerated by the exporter once per recursive member, so each level's
        // refs point at a DIFFERENT node of the same type, and by node alone every level would multiply the
        // expansion by its ref count. A cut ref stays a raw marker for FixupCyclicMarkers to repoint.
        //
        // probes receives the ancestry bits this expansion's cuts consulted beyond what it pushed itself; the result
        // is identical under any ancestry that answers those bits the same way, which is what the memo relies on.
        private object Expand(JsonNode source, out ulong[]? probes) {
            if (TryGetInlineRoot(
                node: source,
                root: out var inlineRoot
            )) {
                return Expand(
                    probes: out probes,
                    source: m_inlineRoots[inlineRoot]
                );
            }

            if (
                (source is JsonObject reference) &&
                TryGetAbsoluteRefTarget(
                refObj: reference,
                target: out var targetPath
            )
            ) {
                return ExpandReference(
                    probes: out probes,
                    reference: reference,
                    targetPath: targetPath
                );
            }

            if (source is not (JsonObject or JsonArray)) {
                probes = null;

                return source;
            }

            var slot = (m_memoSlots.TryGetValue(
                key: source,
                value: out var memoSlot
            )
                ? memoSlot
                : -1
            );
            var target = ((slot < m_targetNodes.Length)
                ? slot
                : -1
            );
            ulong[]? entryPresence = null;

            if (slot >= 0) {
                foreach (var memo in m_memo[slot]) {
                    if (Answers(
                        answers: memo.Answers,
                        probes: memo.Probes
                    )) {
                        probes = memo.Probes;

                        return memo.Node;
                    }
                }

                entryPresence = ((ulong[])m_presence.Clone());
            }

            var type = (m_typesByNode.TryGetValue(
                key: source,
                value: out var ownType
            )
                ? ownType
                : null
            );
            var pushedNode = ((target >= 0) && Push(bit: target));
            var typeBit = (((type is not null) && m_typeBits.TryGetValue(
                key: type,
                value: out var bit
            ))
                ? bit
                : -1
            );
            var pushedType = ((typeBit >= 0) && Push(bit: typeBit));
            ulong[]? consulted = null;
            ExpandedNode result;

            if (source is JsonObject obj) {
                var children = new object?[obj.Count];
                string? refText = null;

                for (var index = 0; (index < children.Length); index++) {
                    var (name, value) = obj.GetAt(index: index);

                    children[index] = ExpandChild(
                        consulted: ref consulted,
                        value: value
                    );

                    if (
                        (value is JsonValue refValue) &&
                        string.Equals(
                        a: name,
                        b: "$ref",
                        comparisonType: StringComparison.Ordinal
                    ) &&
                        refValue.TryGetValue<string>(value: out var text)
                    ) {
                        refText = text;
                    }
                }

                result = new ExpandedNode {
                    Children = children,
                    IsArray = false,
                    RefText = refText,
                    Source = obj,
                    Target = target,
                    Type = type,
                };
            } else {
                var arr = ((JsonArray)source);
                var children = new object?[arr.Count];

                for (var index = 0; (index < children.Length); index++) {
                    children[index] = ExpandChild(
                        consulted: ref consulted,
                        value: arr[index]
                    );
                }

                result = new ExpandedNode {
                    Children = children,
                    IsArray = true,
                    Target = target,
                    Type = type,
                };
            }

            if (pushedNode) {
                Pop(
                    bit: target,
                    consulted: consulted
                );
            }

            if (pushedType) {
                Pop(
                    bit: typeBit,
                    consulted: consulted
                );
            }

            probes = consulted;

            if (slot >= 0) {
                m_memo[slot] = [
                    .. m_memo[slot],
                    new ExpansionMemo(
                        Answers: Mask(
                            bits: entryPresence!,
                            mask: consulted
                        ),
                        Node: result,
                        Probes: consulted
                    ),
                ];
            }

            return result;
        }
        private object? ExpandChild(JsonNode? value, ref ulong[]? consulted) {
            if (value is null) {
                return null;
            }

            var child = Expand(
                probes: out var probes,
                source: value
            );

            Union(
                bits: probes,
                into: ref consulted
            );

            return child;
        }
        // A reference's sibling keywords (e.g. an optional property's own "default") belong to this occurrence, not
        // the shared target: a cut keeps them beside the raw marker, and an expansion lays them over the target's
        // own members, an occurrence-specific keyword replacing the target's in place.
        private object ExpandReference(JsonObject reference, string targetPath, out ulong[]? probes) {
            if (!m_targetByPath.TryGetValue(
                key: targetPath,
                value: out var target
            )) {
                throw new InvalidOperationException(message: $"schema: $ref '{targetPath}' does not resolve within the generated document.");
            }

            ulong[]? consulted = null;
            var typeBit = m_targetTypeBits[target];

            SetBit(
                bit: target,
                bits: ref consulted
            );

            if (typeBit >= 0) {
                SetBit(
                    bit: typeBit,
                    bits: ref consulted
                );
            }

            var names = new List<string>();
            var children = new List<object?>();

            if (
                IsPresent(bit: target) ||
                ((typeBit >= 0) && IsPresent(bit: typeBit))
            ) {
                names.Add(item: "$ref");
                children.Add(item: JsonValue.Create(value: targetPath));

                for (var index = 0; (index < reference.Count); index++) {
                    var (name, value) = reference.GetAt(index: index);

                    if (!string.Equals(
                        a: name,
                        b: "$ref",
                        comparisonType: StringComparison.Ordinal
                    )) {
                        names.Add(item: name);
                        children.Add(item: ExpandChild(
                            consulted: ref consulted,
                            value: value
                        ));
                    }
                }

                probes = consulted;

                return new ExpandedNode {
                    Children = [.. children],
                    IsArray = false,
                    OwnNames = [.. names],
                    RefText = targetPath,
                };
            }

            var expanded = Expand(
                probes: out var targetProbes,
                source: m_targetNodes[target]
            );

            Union(
                bits: targetProbes,
                into: ref consulted
            );

            if (
                (reference.Count == 1) ||
                (expanded is not ExpandedNode { IsArray: false } expandedObject)
            ) {
                probes = consulted;

                return expanded;
            }

            for (var index = 0; (index < expandedObject.Children.Length); index++) {
                names.Add(item: expandedObject.NameAt(index: index));
            }

            children.AddRange(collection: expandedObject.Children);

            for (var index = 0; (index < reference.Count); index++) {
                var (name, value) = reference.GetAt(index: index);

                if (string.Equals(
                    a: name,
                    b: "$ref",
                    comparisonType: StringComparison.Ordinal
                )) {
                    continue;
                }

                var child = ExpandChild(
                    consulted: ref consulted,
                    value: value
                );
                var existing = names.IndexOf(item: name);

                if (existing >= 0) {
                    children[existing] = child;
                } else {
                    names.Add(item: name);
                    children.Add(item: child);
                }
            }

            probes = consulted;

            return new ExpandedNode {
                Children = [.. children],
                IsArray = false,
                OwnNames = [.. names],
                RefText = expandedObject.RefText,
                Target = expandedObject.Target,
                Type = expandedObject.Type,
            };
        }
        private bool Answers(ulong[]? probes, ulong[]? answers) {
            if (probes is null) {
                return true;
            }

            for (var index = 0; (index < probes.Length); index++) {
                if ((m_presence[index] & probes[index]) != answers![index]) {
                    return false;
                }
            }

            return true;
        }
        private bool IsPresent(int bit) =>
            ((m_presence[(bit >> 6)] & (1UL << bit)) != 0);
        private ulong[]? Mask(ulong[] bits, ulong[]? mask) {
            if (mask is null) {
                return null;
            }

            var masked = new ulong[m_presence.Length];

            for (var index = 0; (index < masked.Length); index++) {
                masked[index] = bits[index] & mask[index];
            }

            return masked;
        }
        // Leaving a node retracts what it pushed; a probe of that bit from beneath it was answered by this node, not
        // by the ancestry above it, so it no longer counts as consulted.
        private void Pop(int bit, ulong[]? consulted) {
            m_presence[(bit >> 6)] &= ~(1UL << bit);

            if (consulted is not null) {
                consulted[(bit >> 6)] &= ~(1UL << bit);
            }
        }
        private bool Push(int bit) {
            if (IsPresent(bit: bit)) {
                return false;
            }

            m_presence[(bit >> 6)] |= (1UL << bit);

            return true;
        }
        private void SetBit(int bit, ref ulong[]? bits) {
            bits ??= new ulong[m_presence.Length];
            bits[(bit >> 6)] |= (1UL << bit);
        }
        private void Union(ulong[]? bits, ref ulong[]? into) {
            if (bits is null) {
                return;
            }

            into ??= new ulong[m_presence.Length];

            for (var index = 0; (index < bits.Length); index++) {
                into[index] |= bits[index];
            }
        }
        // ---- grouping -------------------------------------------------------------------------------------------

        // Post-order over the DAG, each node once, descending exactly where the hoist does (never beneath a
        // reference node). A candidate is a named shape (IsHoistCandidate) long enough that a $ref saves
        // bytes; an exporter $ref target is a candidate regardless, since its recursive markers need a def.
        private void Collect(ExpandedNode node, List<ExpandedNode> order) {
            node.Collected = true;

            foreach (var child in node.Children) {
                if (
                    IsDescended(
                    child: child,
                    node: out var descended
                ) &&
                    !descended.Collected
                ) {
                    Collect(
                        node: descended,
                        order: order
                    );
                }
            }

            order.Add(item: node);

            if (node.IsArray) {
                return;
            }

            var forced = (node.Target >= 0);

            if (
                !forced &&
                !IsHoistCandidate(node: node)
            ) {
                return;
            }

            var key = MembersKey(
                node: node,
                withoutAnnotations: true
            );

            if (
                !forced &&
                (key.Length < HoistMinimumLength)
            ) {
                return;
            }

            node.CandidateKeyId = key.Id;
        }
        // A hoist candidate is a genuine named SHAPE — an object type (has "properties"), an enum, or a $type union
        // (has "anyOf") — never a bare leaf (a plain {"type":"string"} with a coincidentally-matching description
        // isn't a shared concept worth a name).
        private static bool IsHoistCandidate(ExpandedNode node) =>
            (node.HasMember(name: "properties") || node.HasMember(name: "enum") || node.HasMember(name: "anyOf"));
        private static bool IsDescended(object? child, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpandedNode? node) {
            node = ((child is ExpandedNode { RefText: null } descended)
                ? descended
                : null
            );

            return (node is not null);
        }
        // A candidate's group key: its content with the occurrence annotations removed and every recursion-involved
        // shape spelled canonically. "description" and "default" on the candidate's ROOT come from the property site
        // where the type is USED, so two occurrences of one shared shape differ exactly there — the hoist moves both
        // keywords to the $ref site instead, and the key must be equally blind. A recursive shape appears CUT at a
        // path-dependent depth (one occurrence keeps a raw marker where another carries a full extra unrolling), so
        // both a marker and an inline forced-def subtree collapse to the same cycle:{TypeName} token — the spelling
        // the reduced content converges to after its own hoist. Inner property annotations stay in the key: they
        // come from the shape's own declaration and are part of it.
        //
        // The key is hash-consed rather than spelled: each node's key is an id interned from its kind and its
        // children's ids, so keying every candidate is linear in the DAG. Two nodes share an id exactly when their
        // compact-JSON key spellings are equal; the spelling's exact length rides beside the id for
        // HoistMinimumLength, and the spelling itself is never materialized.
        private GroupKey KeyOf(object? child) {
            if (child is not ExpandedNode node) {
                return ((child is JsonNode leaf)
                    ? LeafKey(leaf: leaf)
                    : LeafText(text: "null")
                );
            }

            if (node.KeyId >= 0) {
                return new GroupKey(
                    Id: node.KeyId,
                    Length: node.KeyLength
                );
            }

            var key = (node.IsArray
                ? ItemsKey(node: node)
                : ((Token(node: node) is { } token)
                    ? TokenKey(
                        node: node,
                        token: token
                    )
                    : MembersKey(
                        node: node,
                        withoutAnnotations: false
                    ))
            );

            node.KeyId = key.Id;
            node.KeyLength = key.Length;

            return key;
        }
        private GroupKey ItemsKey(ExpandedNode node) {
            var count = node.Children.Length;
            var signature = ArrayPool<int>.Shared.Rent(minimumLength: (count + 1));
            var length = (2 + Math.Max(
                val1: 0,
                val2: (count - 1)
            ));

            signature[0] = ArrayTag;

            for (var index = 0; (index < count); index++) {
                var item = KeyOf(child: node.Children[index]);

                signature[(index + 1)] = item.Id;
                length += item.Length;
            }

            var key = Intern(
                length: length,
                signature: signature.AsSpan(
                    length: (count + 1),
                    start: 0
                )
            );

            ArrayPool<int>.Shared.Return(array: signature);

            return key;
        }
        // A leaf keys by its compact spelling; the common spellings are found without writing the leaf out.
        private GroupKey LeafKey(JsonNode leaf) =>
            leaf.GetValueKind() switch {
                JsonValueKind.String when ((JsonValue)leaf).TryGetValue<string>(value: out var text) => StringKey(value: text),
                JsonValueKind.True => LeafText(text: "true"),
                JsonValueKind.False => LeafText(text: "false"),
                _ => LeafText(text: CompactSerialize(node: leaf)),
            };
        private GroupKey LeafText(string text) {
            if (!m_leafByText.TryGetValue(
                key: text,
                value: out var key
            )) {
                key = new GroupKey(
                    Id: m_nextKeyId++,
                    Length: text.Length
                );
                m_leafByText[text] = key;
            }

            return key;
        }
        private GroupKey MembersKey(ExpandedNode node, bool withoutAnnotations) {
            var count = node.Children.Length;
            var signature = ArrayPool<int>.Shared.Rent(minimumLength: ((2 * count) + 1));
            var used = 1;
            var length = 2;

            signature[0] = ObjectTag;

            for (var index = 0; (index < count); index++) {
                var name = node.NameAt(index: index);

                if (
                    withoutAnnotations &&
                    (name is "description" or "default")
                ) {
                    continue;
                }

                AppendMember(
                    length: ref length,
                    name: name,
                    signature: signature,
                    used: ref used,
                    value: KeyOf(child: node.Children[index])
                );
            }

            var key = Intern(
                length: length,
                signature: signature.AsSpan(
                    length: used,
                    start: 0
                )
            );

            ArrayPool<int>.Shared.Return(array: signature);

            return key;
        }
        private GroupKey StringKey(string value) {
            if (!m_leafByString.TryGetValue(
                key: value,
                value: out var key
            )) {
                key = LeafText(text: CompactSerialize(node: JsonValue.Create(value: value)));
                m_leafByString[value] = key;
            }

            return key;
        }
        // A node below a candidate's root that stands for a shape hoisted elsewhere: a $ref marker spells its target
        // canonically, and an inline expansion of an exporter $ref target collapses to its type's cycle token.
        private string? Token(ExpandedNode node) {
            if (node.RefText is { } pointer) {
                return (m_markerKeyByPointer.TryGetValue(
                    key: pointer,
                    value: out var markerKey
                )
                    ? markerKey
                    : pointer
                );
            }

            if (node.Target >= 0) {
                return ((node.Type is { } type)
                    ? $"cycle:{FriendlyTypeName(type: type)}"
                    : m_targetPaths[node.Target]
                );
            }

            return null;
        }
        // The site annotations stay beside the token, raw — they are what the collapsed subtree's own hoist will
        // leave beside its $ref, so two parents unify exactly when their reduced contents will.
        private GroupKey TokenKey(ExpandedNode node, string token) {
            var signature = new int[7];
            var used = 1;
            var length = 2;

            signature[0] = ObjectTag;
            AppendMember(
                length: ref length,
                name: "$ref",
                signature: signature,
                used: ref used,
                value: StringKey(value: token)
            );

            if (node.Member(name: "description") is JsonValue description) {
                AppendMember(
                    length: ref length,
                    name: "description",
                    signature: signature,
                    used: ref used,
                    value: LeafKey(leaf: description)
                );
            }

            if (node.HasMember(name: "default")) {
                // The default is keyed by its whole compact spelling, never by its structure.
                AppendMember(
                    length: ref length,
                    name: "default",
                    signature: signature,
                    used: ref used,
                    value: ((node.Member(name: "default") is ExpandedNode siteDefault)
                        ? LeafText(text: CompactSerialize(node: Materialize(child: siteDefault)!))
                        : KeyOf(child: node.Member(name: "default")))
                );
            }

            return Intern(
                length: length,
                signature: signature.AsSpan(
                    length: used,
                    start: 0
                )
            );
        }
        // One "name":value member: the name as the compact writer escapes it, the value by id.
        private void AppendMember(int[] signature, ref int used, ref int length, string name, GroupKey value) {
            if (!m_names.TryGetValue(
                key: name,
                value: out var interned
            )) {
                interned = (m_names.Count, JsonEncodedText.Encode(value: name).Value.Length);
                m_names[name] = interned;
            }

            if (used > 1) {
                length++;
            }

            signature[used++] = interned.Id;
            signature[used++] = value.Id;
            length += ((interned.EscapedLength + 3) + value.Length);
        }
        private GroupKey Intern(ReadOnlySpan<int> signature, int length) {
            var lookup = m_signatures.GetAlternateLookup<ReadOnlySpan<int>>();

            if (!lookup.TryGetValue(
                key: signature,
                value: out var id
            )) {
                id = m_nextKeyId++;
                lookup[signature] = id;
            }

            return new GroupKey(
                Id: id,
                Length: length
            );
        }
        // ---- reduction ------------------------------------------------------------------------------------------

        // The hoist reduces the expanded tree post-order: a group member is replaced by a $defs placeholder, and the
        // first member of its group in that order creates the def, whose content is the member with its own children
        // already reduced. Membership was decided up front from each candidate's RAW content, so the only order-
        // dependent choice is which member comes first — and so which def name each group takes. Collect's order is
        // the DAG's post-order with every repeated node skipped, and a repeated node's later occurrences find every
        // group beneath it already named, so naming groups along that order reproduces the tree walk's names.
        private void NameDefs(List<ExpandedNode> order) {
            foreach (var node in order) {
                if (
                    !node.IsMember ||
                    m_groups.ContainsKey(key: node.CandidateKeyId)
                ) {
                    continue;
                }

                var group = new DefGroup(
                    First: node,
                    Name: ChooseDefName(
                        allowsNull: AllowsNull(node: node),
                        type: node.Type,
                        usedNames: m_usedNames
                    ),
                    Summary: DefDescription(node: node)
                );

                m_groups[node.CandidateKeyId] = group;
                m_defs.Add(item: group);
            }
        }
        // The def content: the first member's shape without its occurrence annotations, which ride each $ref site
        // instead (see KeyOf), led by the type-level <summary> — the doc that is true at every site — when the type
        // declares one.
        private JsonObject BuildDef(DefGroup group) {
            var node = group.First;
            var content = new JsonObject();

            if (group.Summary is { } summary) {
                content["description"] = summary;
            }

            for (var index = 0; (index < node.Children.Length); index++) {
                var name = node.NameAt(index: index);

                if (name is not ("description" or "default")) {
                    content[name] = ReduceChild(child: node.Children[index]);
                }
            }

            return content;
        }
        private static string? DefDescription(ExpandedNode node) =>
            (((node.Type is { } defType) && (XmlDocIndex.Value is { } index))
                ? TypeSummary(
                    index: index,
                    type: defType
                )
                : null
            );
        // The reduced JSON of one expanded node: a member becomes its placeholder, a reference node stays whole, and
        // anything else is rebuilt from its reduced children.
        private JsonNode Reduce(ExpandedNode node) {
            if (node.IsArray) {
                var arr = new JsonArray();

                foreach (var child in node.Children) {
                    arr.Add(item: ReduceChild(child: child));
                }

                return arr;
            }

            if (node.RefText is not null) {
                return Materialize(child: node)!;
            }

            if (node.IsMember) {
                return Placeholder(node: node);
            }

            var obj = new JsonObject();

            for (var index = 0; (index < node.Children.Length); index++) {
                obj[node.NameAt(index: index)] = ReduceChild(child: node.Children[index]);
            }

            return obj;
        }
        private JsonNode? ReduceChild(object? child) =>
            child switch {
                ExpandedNode node => Reduce(node: node),
                JsonNode leaf => leaf.DeepClone(),
                _ => null,
            };
        // The occurrence annotations, re-sited beside the reference (draft 2020-12 keeps keywords beside "$ref"
        // meaningful). A site description matching the def's own is dropped — it was the type-summary fallback,
        // already stated once on the def.
        private JsonObject Placeholder(ExpandedNode node) {
            var group = m_groups[node.CandidateKeyId];
            var placeholder = new JsonObject { ["$ref"] = $"$defs/{group.Name}" };

            if (
                (node.Member(name: "description") is JsonValue description) &&
                !((group.Summary is { } summary) && JsonNode.DeepEquals(
                node1: JsonValue.Create(value: summary),
                node2: description
            ))
            ) {
                placeholder["description"] = description.DeepClone();
            }

            if (node.HasMember(name: "default")) {
                placeholder["default"] = ReduceChild(child: node.Member(name: "default"));
            }

            return placeholder;
        }
        // Whether the member's reduced content admits null: a "null" type, or a type/enum/anyOf/oneOf entry that is
        // null, "null", or an arm typed "null". An arm that is itself a member reduces to a placeholder, which
        // carries no "type" of its own.
        private static bool AllowsNull(ExpandedNode node) {
            if (IsNullText(child: node.Member(name: "type"))) {
                return true;
            }

            foreach (var key in ((ReadOnlySpan<string>)["type", "enum", "anyOf", "oneOf"])) {
                if (node.Member(name: key) is not ExpandedNode { IsArray: true } values) {
                    continue;
                }

                foreach (var value in values.Children) {
                    if (
                        (value is null) ||
                        IsNullText(child: value) ||
                        ((value is ExpandedNode { IsArray: false, IsMember: false } arm) && IsNullText(child: arm.Member(name: "type")))
                    ) {
                        return true;
                    }
                }
            }

            return false;
        }
        private static bool IsNullText(object? child) =>
            ((child is JsonValue value) &&
            value.TryGetValue<string>(value: out var text) &&
            string.Equals(
                a: text,
                b: "null",
                comparisonType: StringComparison.Ordinal
            ));
        // Reverse post-order over the DAG, each node once: the first member expansion of a target seen this way is
        // the LAST one the tree walk reaches, whose def a leftover recursive marker pointing at that target names.
        private void Mirror(ExpandedNode node) {
            node.Mirrored = true;

            if (
                node.IsMember &&
                (node.Target >= 0)
            ) {
                m_targetDefNames[node.Target] ??= m_groups[node.CandidateKeyId].Name;
            }

            for (var index = (node.Children.Length - 1); (index >= 0); index--) {
                if (
                    IsDescended(
                    child: node.Children[index],
                    node: out var descended
                ) &&
                    !descended.Mirrored
                ) {
                    Mirror(node: descended);
                }
            }
        }
        private static JsonNode? Materialize(object? child) {
            switch (child) {
                case ExpandedNode { IsArray: true } node:
                    var arr = new JsonArray();

                    foreach (var item in node.Children) {
                        arr.Add(item: Materialize(child: item));
                    }

                    return arr;
                case ExpandedNode node:
                    var obj = new JsonObject();

                    for (var index = 0; (index < node.Children.Length); index++) {
                        obj[node.NameAt(index: index)] = Materialize(child: node.Children[index]);
                    }

                    return obj;
                case JsonNode leaf:
                    return leaf.DeepClone();
                default:
                    return null;
            }
        }
    }

    // A document-absolute JSON pointer ("#/a/b/0") resolved against root; null when it names nothing. A pointer into
    // an inline export would name one occurrence of a node every occurrence shares, so it is refused.
    private static JsonNode? ResolvePointer(JsonObject root, string pointer) {
        JsonNode? cursor = root;

        foreach (var escaped in pointer[2..].Split(separator: '/')) {
            var segment = escaped.Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "/",
                oldValue: "~1"
            ).Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "~",
                oldValue: "~0"
            );

            cursor = cursor switch {
                JsonObject obj => (obj.TryGetPropertyValue(
                    jsonNode: out var member,
                    propertyName: segment
                )
                    ? member
                    : null),
                JsonArray arr => ((int.TryParse(
                    provider: System.Globalization.CultureInfo.InvariantCulture,
                    result: out var index,
                    s: segment,
                    style: System.Globalization.NumberStyles.None
                ) && (index < arr.Count))
                    ? arr[index]
                    : null),
                _ => null,
            };

            if (cursor is null) {
                return null;
            }

            if (TryGetInlineRoot(
                node: cursor,
                root: out _
            )) {
                throw new InvalidOperationException(message: $"schema: $ref '{pointer}' points into an inline export.");
            }
        }

        return cursor;
    }
}
