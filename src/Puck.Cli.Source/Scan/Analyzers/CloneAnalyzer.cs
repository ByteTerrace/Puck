using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// The duplicate-code detector — the refactor-target sibling of the other analyzers. Clusters
// structurally identical code, four passes deep, each leaving what an earlier pass already holds:
//   type       — a whole class, struct or record body (its members, not its modifiers, name or base
//                list), so a helper type pasted into several files clusters even when each of its
//                members is under the token floor alone.
//   unit       — a whole callable body (method / ctor / dtor / operator / conversion / property-or-
//                indexer-or-event accessor / local function), block- or expression-bodied.
//   block      — a nested `{...}` (an if/else/loop/using/lock/try/switch-section body) of >=
//                --min-statements statements, reported ONLY when its enclosing unit is NOT itself a
//                clone (partial copy-paste) and only at the OUTERMOST duplicated nesting level, so a
//                method's inner blocks never double-count.
//   expression — an outermost expression of >= --min-expression-tokens tokens outside every clone
//                above: a formula copied into callers whose surrounding statements differ.
// Each body is fingerprinted twice over its token stream (trivia-, whitespace- and comment-insensitive):
// a STRUCTURAL hash that abstracts every identifier and literal to a placeholder (so renamed-variable /
// changed-constant copies — Type-2 clones — still collide) and an EXACT hash over the raw token text
// (verbatim Type-1 copies). Clustering is on the structural hash; `exactCount` reports how many of a
// cluster's members are byte-identical, so triage sees verbatim vs renamed at a glance. Clusters are
// gated by token weight (--min-tokens) — two one-line getters share a fingerprint but are not a refactor
// target.
//
// Members are `abiTainted`-tagged when they live in a marshalling/ABI context (a VulkanNative* file, the
// Vulkan/Bindings tree, or a Vk*-named type), because that duplication is a deliberate ABI mirror rather
// than a cleanup target — the tag lets a triage pass separate contract from copy-paste. Detection is
// purely syntactic (no semantic model). One JSONL record per cluster; --grouped emits a per-cluster
// work-list (one chunk = one cluster) for a fan-out triage; like its siblings it hand-writes its json.
// Output is fully deterministic (files, members, and clusters are all ordered) so re-runs diff cleanly.
internal sealed class CloneAnalyzer : ISourceAnalyzer {
    // Fingerprint sentinels from the C0 control range: neither byte can occur in C# token text, so a
    // separator never splits a token and a placeholder never collides with real source.
    private const char PlaceholderPrefix = ((char)0x02);
    private const char TokenSeparator = ((char)0x01);

    private static readonly string CharacterMark = $"{PlaceholderPrefix}C";
    private static readonly string IdentifierMark = $"{PlaceholderPrefix}I";
    private static readonly string InterpolationMark = $"{PlaceholderPrefix}T";
    private static readonly string NumberMark = $"{PlaceholderPrefix}N";
    private static readonly string StringMark = $"{PlaceholderPrefix}S";

    private static string AccessorName(AccessorDeclarationSyntax accessor) {
        var owner = accessor.Ancestors().FirstOrDefault(predicate: static node => (node is BasePropertyDeclarationSyntax));
        var ownerName = owner switch {
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            IndexerDeclarationSyntax => "this[]",
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            _ => ""
        };

        return $"{ownerName}.{accessor.Keyword.ValueText}";
    }
    // The body spans of clustered units, per file — a block inside one of these is redundant with the
    // unit clone, so the block pass skips it.
    private static Dictionary<string, List<(int Start, int End)>> BodySpansByFile(IEnumerable<List<Member>> clusters) {
        var spansByFile = new Dictionary<string, List<(int Start, int End)>>(comparer: StringComparer.Ordinal);

        foreach (var cluster in clusters) {
            foreach (var member in cluster) {
                if (!spansByFile.TryGetValue(
                    key: member.File,
                    value: out var spans
                )) {
                    spans = [];
                    spansByFile[member.File] = spans;
                }

                spans.Add(item: (member.BodyStart, member.BodyEnd));
            }
        }

        return spansByFile;
    }
    // One chunk per cluster (split if a cluster has more than maxPerChunk members), so a fan-out triage
    // spends one agent per duplication cluster.
    private static string BuildCloneGroups(List<Cluster> clusters, int maxPerChunk) {
        var builder = new StringBuilder(value: "[");
        var firstChunk = true;

        foreach (var cluster in clusters) {
            var chunkCount = (((cluster.Members.Count + maxPerChunk) - 1) / maxPerChunk);

            for (var offset = 0; (offset < cluster.Members.Count); offset += maxPerChunk) {
                if (!firstChunk) {
                    builder.Append(value: ',');
                }

                firstChunk = false;
                builder.Append(value: '{')
                    .Append(value: "\"cluster\":").Append(value: cluster.Id).Append(value: ',')
                    .Append(value: "\"kind\":").Append(value: ScanJsonl.JsonString(value: cluster.Kind)).Append(value: ',')
                    .Append(value: "\"chunk\":").Append(value: (offset / maxPerChunk)).Append(value: ',')
                    .Append(value: "\"chunks\":").Append(value: chunkCount).Append(value: ',')
                    .Append(value: "\"abiTainted\":").Append(value: (cluster.Abi
                    ? "true"
                    : "false")).Append(value: ',')
                    .Append(value: "\"label\":").Append(value: ScanJsonl.JsonString(value: cluster.Label)).Append(value: ',')
                    .Append(value: "\"members\":[");

                var end = Math.Min(
                    val1: (offset + maxPerChunk),
                    val2: cluster.Members.Count
                );

                for (var memberIndex = offset; (memberIndex < end); memberIndex++) {
                    if (memberIndex > offset) {
                        builder.Append(value: ',');
                    }

                    var member = cluster.Members[memberIndex];

                    builder.Append(value: '{')
                        .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: member.File)).Append(value: ',')
                        .Append(value: "\"line\":").Append(value: member.StartLine).Append(value: ',')
                        .Append(value: "\"endLine\":").Append(value: member.EndLine).Append(value: ',')
                        .Append(value: "\"unit\":").Append(value: ScanJsonl.JsonString(value: member.Unit))
                        .Append(value: '}');
                }

                builder.Append(value: "]}");
            }
        }

        return builder.Append(value: ']').ToString();
    }
    private static Cluster BuildCluster(string kind, List<Member> members) {
        members.Sort(comparison: static (left, right) => {
            var byFile = string.CompareOrdinal(
                strA: left.File,
                strB: right.File
            );

            return ((byFile != 0)
                ? byFile
                : left.StartLine.CompareTo(value: right.StartLine)
            );
        });

        var exactCount = members
            .GroupBy(
            keySelector: static member => member.Exact,
            comparer: StringComparer.Ordinal
        )
            .Max(selector: static group => group.Count());
        var abi = members.Any(predicate: static member => member.Abi);

        return new Cluster(
            Id: -1,
            Kind: kind,
            Fingerprint: members[0].Structural,
            Weight: members[0].Weight,
            ExactCount: exactCount,
            Abi: abi,
            Label: members[0].Unit,
            Members: members
        );
    }
    private static string BuildJsonl(List<Cluster> clusters) {
        var jsonl = new StringBuilder();

        foreach (var cluster in clusters) {
            var exactByHash = cluster.Members
                .GroupBy(
                keySelector: static member => member.Exact,
                comparer: StringComparer.Ordinal
            )
                .ToDictionary(
                keySelector: static group => group.Key,
                elementSelector: static group => group.Count(),
                comparer: StringComparer.Ordinal
            );

            jsonl.Append(value: '{')
                .Append(value: "\"id\":").Append(value: cluster.Id).Append(value: ',')
                .Append(value: "\"kind\":").Append(value: ScanJsonl.JsonString(value: cluster.Kind)).Append(value: ',')
                .Append(value: "\"fingerprint\":").Append(value: ScanJsonl.JsonString(value: cluster.Fingerprint)).Append(value: ',')
                .Append(value: "\"memberCount\":").Append(value: cluster.Members.Count).Append(value: ',')
                .Append(value: "\"exactCount\":").Append(value: cluster.ExactCount).Append(value: ',')
                .Append(value: "\"tokenWeight\":").Append(value: cluster.Weight).Append(value: ',')
                .Append(value: "\"redundantMass\":").Append(value: ((cluster.Members.Count - 1) * cluster.Weight)).Append(value: ',')
                .Append(value: "\"abiTainted\":").Append(value: (cluster.Abi
                ? "true"
                : "false")).Append(value: ',')
                .Append(value: "\"label\":").Append(value: ScanJsonl.JsonString(value: cluster.Label)).Append(value: ',')
                .Append(value: "\"members\":[");

            for (var memberIndex = 0; (memberIndex < cluster.Members.Count); memberIndex++) {
                if (memberIndex > 0) {
                    jsonl.Append(value: ',');
                }

                var member = cluster.Members[memberIndex];

                jsonl.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: member.File)).Append(value: ',')
                    .Append(value: "\"line\":").Append(value: member.StartLine).Append(value: ',')
                    .Append(value: "\"endLine\":").Append(value: member.EndLine).Append(value: ',')
                    .Append(value: "\"unit\":").Append(value: ScanJsonl.JsonString(value: member.Unit)).Append(value: ',')
                    .Append(value: "\"exact\":").Append(value: ((exactByHash[member.Exact] >= 2)
                    ? "true"
                    : "false"))
                    .Append(value: '}');
            }

            jsonl.Append(value: "]}\n");
        }

        return jsonl.ToString();
    }
    // Structural canonicalization: identifiers and literals collapse to a kind marker (the placeholder
    // prefix keeps a marker from ever colliding with literal token text); keywords, punctuation and
    // operators keep their text, as they ARE the structure that must match.
    private static string CanonicalToken(SyntaxToken token) => token.Kind() switch {
        SyntaxKind.IdentifierToken => IdentifierMark,
        SyntaxKind.NumericLiteralToken => NumberMark,
        SyntaxKind.StringLiteralToken => StringMark,
        SyntaxKind.Utf8StringLiteralToken => StringMark,
        SyntaxKind.SingleLineRawStringLiteralToken => StringMark,
        SyntaxKind.MultiLineRawStringLiteralToken => StringMark,
        SyntaxKind.Utf8SingleLineRawStringLiteralToken => StringMark,
        SyntaxKind.Utf8MultiLineRawStringLiteralToken => StringMark,
        SyntaxKind.CharacterLiteralToken => CharacterMark,
        SyntaxKind.InterpolatedStringTextToken => InterpolationMark,
        _ => token.Text
    };
    // Pass 2: nested blocks of >= minStatements statements that are neither a unit body nor inside a
    // clustered unit, kept only at the OUTERMOST duplicated level so a duplicated outer block never
    // re-reports its inner blocks.
    private static List<List<Member>> CollectBlockClusters(
        List<(string Relative, SyntaxNode Root)> trees,
        HashSet<SyntaxNode> unitBodies,
        Dictionary<string, List<(int Start, int End)>> clusteredBodies,
        int minStatements,
        int minTokens
    ) {
        var candidates = new List<(SyntaxNode Node, Member Member)>();

        foreach (var (relative, treeRoot) in trees) {
            var bodies = clusteredBodies.GetValueOrDefault(key: relative);

            foreach (var block in treeRoot.DescendantNodes().OfType<BlockSyntax>()) {
                if (
                    unitBodies.Contains(item: block) ||
                    (block.Statements.Count < minStatements) ||
                    Inside(spans: bodies, node: block)
                ) {
                    continue;
                }

                var (structural, exact, weight) = Fingerprint(body: block);

                if (weight < minTokens) {
                    continue;
                }

                candidates.Add(item: (block, CreateMember(
                    body: block,
                    exact: exact,
                    node: block,
                    relative: relative,
                    structural: structural,
                    weight: weight
                )));
            }
        }

        return ClusterOutermost(candidates: candidates);
    }
    // Pass 3: outermost expressions of >= minTokens tokens outside every clustered type, unit and block. A formula
    // copied into callers whose surrounding statements differ (a sample position spelled again in each test that
    // needs it) clusters here, where neither the unit nor the block pass can see it.
    private static List<List<Member>> CollectExpressionClusters(
        List<(string Relative, SyntaxNode Root)> trees,
        Dictionary<string, List<(int Start, int End)>> clusteredBodies,
        int minTokens
    ) {
        var candidates = new List<(SyntaxNode Node, Member Member)>();
        // A token is at least one character, so a span shorter than the floor cannot carry it; the check saves walking
        // the tokens of every small expression.
        var minLength = minTokens;

        foreach (var (relative, treeRoot) in trees) {
            var bodies = clusteredBodies.GetValueOrDefault(key: relative);

            foreach (var expression in treeRoot.DescendantNodes(descendIntoChildren: node => !Inside(spans: bodies, node: node)).OfType<ExpressionSyntax>()) {
                if ((expression.Span.Length < minLength) || (expression.Parent is ExpressionSyntax)) {
                    continue;
                }

                var (structural, exact, weight) = Fingerprint(body: expression);

                if (weight < minTokens) {
                    continue;
                }

                // A table of literals has the structure of every other table its size, so data clusters only with an
                // exact copy of itself (the same tap set pasted into five files), never with an unrelated table.
                candidates.Add(item: (expression, CreateMember(
                    body: expression,
                    exact: exact,
                    node: expression,
                    relative: relative,
                    structural: (IsData(expression: expression) ? exact : structural),
                    weight: weight
                )));
            }
        }

        return ClusterOutermost(candidates: candidates);
    }
    // Pass 0: whole type bodies (the members between a class, struct or record's braces, without its modifiers or
    // base list), so a small helper type pasted into several files, each of whose members is under the token floor
    // alone, clusters as one copy.
    private static List<List<Member>> CollectTypeClusters(List<(string Relative, SyntaxNode Root)> trees, int minTokens) {
        var candidates = new List<(SyntaxNode Node, Member Member)>();

        foreach (var (relative, treeRoot) in trees) {
            foreach (var type in treeRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()) {
                if (type.Members.Count == 0) {
                    continue;
                }

                var (structural, exact, weight) = Fingerprint(body: type, members: type.Members);

                if (weight < minTokens) {
                    continue;
                }

                candidates.Add(item: (type, CreateMember(
                    body: type,
                    exact: exact,
                    node: type,
                    relative: relative,
                    structural: structural,
                    weight: weight
                )));
            }
        }

        return ClusterOutermost(candidates: candidates);
    }
    // Groups candidates by structural fingerprint and keeps a cluster member only when no ancestor of it is itself a
    // clustered candidate, so a duplicated outer node never re-reports its duplicated insides.
    private static List<List<Member>> ClusterOutermost(List<(SyntaxNode Node, Member Member)> candidates) {
        var counts = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var candidate in candidates) {
            counts[candidate.Member.Structural] = (counts.GetValueOrDefault(key: candidate.Member.Structural) + 1);
        }

        var clusteredNodes = new HashSet<SyntaxNode>();

        foreach (var candidate in candidates) {
            if (counts[candidate.Member.Structural] >= 2) {
                clusteredNodes.Add(item: candidate.Node);
            }
        }

        var survivors = new List<Member>();

        foreach (var candidate in candidates) {
            if (
                (counts[candidate.Member.Structural] >= 2) &&
                !candidate.Node.Ancestors().Any(predicate: clusteredNodes.Contains)
            ) {
                survivors.Add(item: candidate.Member);
            }
        }

        return survivors
            .GroupBy(
            keySelector: static member => member.Structural,
            comparer: StringComparer.Ordinal
        )
            .Where(predicate: static group => (group.Count() >= 2))
            .Select(selector: static group => group.ToList())
            .ToList();
    }
    // Pass 1: every callable body, fingerprinted. unitBodies records the block bodies so the block pass
    // can skip them (a unit body is the unit pass's job).
    private static List<Member> CollectUnits(List<(string Relative, SyntaxNode Root)> trees, HashSet<SyntaxNode> unitBodies) {
        var units = new List<Member>();

        foreach (var (relative, treeRoot) in trees) {
            foreach (var node in treeRoot.DescendantNodes()) {
                var body = UnitBody(node: node);

                if (body is null) {
                    continue;
                }

                if (body is BlockSyntax) {
                    unitBodies.Add(item: body);
                }

                var (structural, exact, weight) = Fingerprint(body: body);

                units.Add(item: CreateMember(
                    body: body,
                    exact: exact,
                    node: node,
                    relative: relative,
                    structural: structural,
                    weight: weight
                ));
            }
        }

        return units;
    }
    private static Member CreateMember(string relative, SyntaxNode node, SyntaxNode body, string structural, string exact, int weight) {
        var (startLine, endLine) = ScanJsonl.LineRange(location: node.GetLocation());
        var (typeName, memberName) = Describe(node: node);
        var unit = ((typeName.Length == 0)
            ? memberName
            : $"{typeName}.{memberName}"
        );

        return new Member(
            File: relative,
            StartLine: startLine,
            EndLine: endLine,
            BodyStart: body.Span.Start,
            BodyEnd: body.Span.End,
            Structural: structural,
            Exact: exact,
            Weight: weight,
            Unit: unit,
            Abi: IsAbi(
                relative: relative,
                typeName: typeName
            )
        );
    }
    // A human label for a node: (enclosing type, member). For a block it is the enclosing callable's
    // member name plus the block's line, so two block clones are distinguishable in the report.
    private static (string Type, string Member) Describe(SyntaxNode node) {
        var typeName = (node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "");

        switch (node) {
            case MethodDeclarationSyntax method:
                return (typeName, method.Identifier.ValueText);
            case ConstructorDeclarationSyntax:
                return (typeName, ".ctor");
            case DestructorDeclarationSyntax:
                return (typeName, "~ctor");
            case OperatorDeclarationSyntax op:
                return (typeName, $"operator{op.OperatorToken.ValueText}");
            case ConversionOperatorDeclarationSyntax:
                return (typeName, "operator");
            case AccessorDeclarationSyntax accessor:
                return (typeName, AccessorName(accessor: accessor));
            case LocalFunctionStatementSyntax local:
                return (typeName, $"local:{local.Identifier.ValueText}");
            case BlockSyntax block:
                var owner = block.Ancestors().FirstOrDefault(predicate: IsUnitNode);
                var ownerName = ((owner is null)
                    ? ""
                    : Describe(node: owner).Member
                );
                var line = (block.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

                return (typeName, $"{ownerName} block@{line}");
            case TypeDeclarationSyntax type:
                return (typeName, $"type:{type.Identifier.ValueText}");
            case ExpressionSyntax expression:
                var holder = expression.Ancestors().FirstOrDefault(predicate: IsUnitNode);
                var holderName = ((holder is null)
                    ? ""
                    : Describe(node: holder).Member
                );

                return (typeName, $"{holderName} expr@{(expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1)}");
            default:
                return (typeName, "");
        }
    }
    // Two hashes over the body's token stream (trivia excluded, so whitespace and comments never
    // matter): STRUCTURAL abstracts identifiers and literals to a placeholder (Type-2 clones collide),
    // EXACT keeps the raw token text (Type-1).
    private static (string Structural, string Exact, int Weight) Fingerprint(SyntaxNode body) =>
        Fingerprint(body: body, members: null);
    // A type is fingerprinted over its members alone, so two copies that differ only in modifiers, name or base list
    // (`unsafe`, a qualified interface name) still collide.
    private static (string Structural, string Exact, int Weight) Fingerprint(SyntaxNode body, SyntaxList<MemberDeclarationSyntax>? members) {
        var structural = new StringBuilder();
        var exact = new StringBuilder();
        var weight = 0;
        var tokens = ((members is { } list)
            ? list.SelectMany(selector: static member => member.DescendantTokens())
            : body.DescendantTokens());

        foreach (var token in tokens) {
            structural.Append(value: CanonicalToken(token: token)).Append(value: TokenSeparator);
            exact.Append(value: token.Text).Append(value: TokenSeparator);
            weight++;
        }

        return (Hash(value: structural.ToString()), Hash(value: exact.ToString()), weight);
    }
    private static string Hash(string value) =>
        Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: value)));
    // True when a member lives in a marshalling/ABI context, so triage can separate deliberate binding
    // mirrors from real copy-paste.
    private static bool IsAbi(string relative, string typeName) =>
        (relative.Contains(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "VulkanNative"
        )
        || relative.Contains(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "Vulkan/Bindings"
        )
        || typeName.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Vk"
        ));
    // Whether an expression only spells values: no operator, condition, call or indexing anywhere in it.
    private static bool IsData(ExpressionSyntax expression) =>
        !expression.DescendantNodesAndSelf().Any(predicate: static node => node is BinaryExpressionSyntax or ConditionalExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or AssignmentExpressionSyntax or LambdaExpressionSyntax);
    // Whether a file is generator output, whose repetition is its generator's to remove.
    internal static bool IsGenerated(string relative, SyntaxNode root) =>
        relative.EndsWith(value: ".generated.cs", comparisonType: StringComparison.OrdinalIgnoreCase) ||
        relative.EndsWith(value: ".g.cs", comparisonType: StringComparison.OrdinalIgnoreCase) ||
        relative.EndsWith(value: ".designer.cs", comparisonType: StringComparison.OrdinalIgnoreCase) ||
        root.GetLeadingTrivia().ToString().Contains(value: "<auto-generated", comparisonType: StringComparison.Ordinal);
    // Whether a node lies within one of a file's clustered spans, so a later pass leaves it to the clone that holds it.
    private static bool Inside(List<(int Start, int End)>? spans, SyntaxNode node) =>
        ((spans is not null) && spans.Any(predicate: span => ((span.Start <= node.Span.Start) && (node.Span.End <= span.End))));
    private static bool IsUnitNode(SyntaxNode node) => (node
        is MethodDeclarationSyntax
        or ConstructorDeclarationSyntax
        or DestructorDeclarationSyntax
        or OperatorDeclarationSyntax
        or ConversionOperatorDeclarationSyntax
        or AccessorDeclarationSyntax
        or LocalFunctionStatementSyntax);
    // Heaviest clones first (redundant mass = extra copies x weight), deterministically tie-broken, then
    // numbered.
    private static List<Cluster> OrderAndNumber(List<Cluster> clusters) {
        var ordered = clusters
            .OrderByDescending(keySelector: static cluster => ((cluster.Members.Count - 1) * cluster.Weight))
            .ThenByDescending(keySelector: static cluster => cluster.Members.Count)
            .ThenBy(
            keySelector: static cluster => cluster.Fingerprint,
            comparer: StringComparer.Ordinal
        )
            .ToList();

        for (var clusterIndex = 0; (clusterIndex < ordered.Count); clusterIndex++) {
            ordered[clusterIndex] = ordered[clusterIndex] with { Id = clusterIndex };
        }

        return ordered;
    }
    private static void PrintSummary(List<Cluster> clusters, int minTokens, int minStatements, int minExpressionTokens, bool includeBlocks, int filesScanned) {
        var byKind = string.Join(
            separator: ", ",
            values: new[] { "type", "unit", "block", "expression" }.Select(selector: kind => $"{clusters.Count(predicate: cluster => (cluster.Kind == kind))} {kind}")
        );
        var siteCount = clusters.Sum(selector: static cluster => cluster.Members.Count);
        var redundantMass = clusters.Sum(selector: static cluster => ((cluster.Members.Count - 1) * cluster.Weight));
        var abiClusters = clusters.Count(predicate: static cluster => cluster.Abi);

        Console.Error.WriteLine(value: $"scan[clones]: {clusters.Count} clone clusters ({byKind}; {abiClusters} abi-tainted) over {siteCount} sites; redundant token mass ~{redundantMass} (minTokens={minTokens}, minStatements={minStatements}, minExpressionTokens={minExpressionTokens}, blocks={(includeBlocks
            ? "on"
            : "off")}, files scanned={filesScanned}).");

        foreach (var cluster in clusters.Take(count: 30)) {
            var fileCount = cluster.Members.Select(selector: static member => member.File).Distinct(comparer: StringComparer.Ordinal).Count();
            var abiTag = (cluster.Abi
                ? "[abi] "
                : ""
            );
            var exactTag = ((cluster.ExactCount == cluster.Members.Count)
                ? "exact"
                : $"{cluster.ExactCount}/{cluster.Members.Count} exact"
            );

            Console.Error.WriteLine(value: $"{((cluster.Members.Count - 1) * cluster.Weight),7}  x{cluster.Members.Count} w{cluster.Weight} {abiTag}{cluster.Kind} ({exactTag}) {cluster.Label} [{fileCount} file{((fileCount == 1)
                ? ""
                : "s")}]");
        }
    }
    // The body to fingerprint for a callable node — its block body, else its expression body, else null
    // (an abstract/partial/extern/interface declaration or an auto-property accessor has no body and is
    // not a clone candidate).
    private static SyntaxNode? UnitBody(SyntaxNode node) => node switch {
        MethodDeclarationSyntax method => (((SyntaxNode?)method.Body) ?? method.ExpressionBody),
        ConstructorDeclarationSyntax constructor => (((SyntaxNode?)constructor.Body) ?? constructor.ExpressionBody),
        DestructorDeclarationSyntax destructor => (((SyntaxNode?)destructor.Body) ?? destructor.ExpressionBody),
        OperatorDeclarationSyntax op => (((SyntaxNode?)op.Body) ?? op.ExpressionBody),
        ConversionOperatorDeclarationSyntax conversion => (((SyntaxNode?)conversion.Body) ?? conversion.ExpressionBody),
        AccessorDeclarationSyntax accessor => (((SyntaxNode?)accessor.Body) ?? accessor.ExpressionBody),
        LocalFunctionStatementSyntax local => (((SyntaxNode?)local.Body) ?? local.ExpressionBody),
        _ => null
    };

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var trees = corpus.Files
            .Where(predicate: static file => !IsGenerated(relative: file.Relative, root: file.Root))
            .Select(selector: static file => (file.Relative, file.Root))
            .ToList();

        // Each pass leaves what an earlier, larger clone already holds: whole types, then callable bodies, then
        // nested blocks, then expressions.
        var typeClusters = CollectTypeClusters(
            minTokens: options.MinTokens,
            trees: trees
        );
        var typeSpans = BodySpansByFile(clusters: typeClusters);
        var unitBodies = new HashSet<SyntaxNode>();
        var units = CollectUnits(
            trees: trees,
            unitBodies: unitBodies
        );
        var unitClusters = units
            .Where(predicate: member => (
                (member.Weight >= options.MinTokens) &&
                !(typeSpans.GetValueOrDefault(key: member.File)?.Any(predicate: span => ((span.Start <= member.BodyStart) && (member.BodyEnd <= span.End))) ?? false)
            ))
            .GroupBy(
            keySelector: static member => member.Structural,
            comparer: StringComparer.Ordinal
        )
            .Where(predicate: static group => (group.Count() >= 2))
            .Select(selector: static group => group.ToList())
            .ToList();

        var clusteredBodies = BodySpansByFile(clusters: typeClusters.Concat(second: unitClusters));
        var blockClusters = (options.IncludeBlocks
            ? CollectBlockClusters(
                trees: trees,
                unitBodies: unitBodies,
                clusteredBodies: clusteredBodies,
                minStatements: options.MinStatements,
                minTokens: options.MinTokens
            )
            : []
        );
        var expressionClusters = ((options.MinExpressionTokens > 0)
            ? CollectExpressionClusters(
                clusteredBodies: BodySpansByFile(clusters: typeClusters.Concat(second: unitClusters).Concat(second: blockClusters)),
                minTokens: options.MinExpressionTokens,
                trees: trees
            )
            : []
        );

        var clusters = new List<Cluster>();

        foreach (var (kind, groups) in new[] { ("type", typeClusters), ("unit", unitClusters), ("block", blockClusters), ("expression", expressionClusters) }) {
            foreach (var group in groups) {
                clusters.Add(item: BuildCluster(
                    kind: kind,
                    members: group
                ));
            }
        }

        clusters = OrderAndNumber(clusters: clusters);

        PrintSummary(
            clusters: clusters,
            minExpressionTokens: options.MinExpressionTokens,
            minTokens: options.MinTokens,
            minStatements: options.MinStatements,
            includeBlocks: options.IncludeBlocks,
            filesScanned: corpus.FileCount
        );

        return (BuildJsonl(clusters: clusters), BuildCloneGroups(
            clusters: clusters,
            maxPerChunk: options.MaxPerChunk
        ));
    }

    private sealed record Cluster(int Id, string Kind, string Fingerprint, int Weight, int ExactCount, bool Abi, string Label, List<Member> Members);
    private sealed record Member(
        string File,
        int StartLine,
        int EndLine,
        int BodyStart,
        int BodyEnd,
        string Structural,
        string Exact,
        int Weight,
        string Unit,
        bool Abi
    );
}
