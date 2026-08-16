using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// The duplicate-code detector — the refactor-target sibling of the other analyzers. Clusters
// structurally identical code, two passes deep:
//   unit  — a whole callable body (method / ctor / dtor / operator / conversion / property-or-indexer-
//           or-event accessor / local function), block- or expression-bodied.
//   block — a nested `{...}` (an if/else/loop/using/lock/try/switch-section body) of >= -MinStatements
//           statements, reported ONLY when its enclosing unit is NOT itself a clone (partial
//           copy-paste) and only at the OUTERMOST duplicated nesting level, so a method's inner blocks
//           never double-count.
// Each body is fingerprinted twice over its token stream (trivia-, whitespace- and comment-insensitive):
// a STRUCTURAL hash that abstracts every identifier and literal to a placeholder (so renamed-variable /
// changed-constant copies — Type-2 clones — still collide) and an EXACT hash over the raw token text
// (verbatim Type-1 copies). Clustering is on the structural hash; `exactCount` reports how many of a
// cluster's members are byte-identical, so triage sees verbatim vs renamed at a glance. Clusters are
// gated by token weight (-MinTokens) — two one-line getters share a fingerprint but are not a refactor
// target.
//
// Members are `abiTainted`-tagged when they live in a marshalling/ABI context (a VulkanNative* file, the
// Vulkan/Bindings tree, or a Vk*-named type), because that duplication is a deliberate ABI mirror rather
// than a cleanup target — the tag lets a triage pass separate contract from copy-paste. Detection is
// purely syntactic (no semantic model). One JSONL record per cluster; -Grouped emits a per-cluster
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

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var trees = corpus.Files.Select(selector: static file => (file.Relative, file.Root)).ToList();

        var unitBodies = new HashSet<SyntaxNode>();
        var units = CollectUnits(trees: trees, unitBodies: unitBodies);
        var unitClusters = units
            .Where(predicate: member => (member.Weight >= options.MinTokens))
            .GroupBy(keySelector: static member => member.Structural, comparer: StringComparer.Ordinal)
            .Where(predicate: static group => (group.Count() >= 2))
            .Select(selector: static group => group.ToList())
            .ToList();

        var clusteredBodies = BodySpansByFile(clusters: unitClusters);
        var blockClusters = (options.IncludeBlocks
            ? CollectBlockClusters(trees: trees, unitBodies: unitBodies, clusteredBodies: clusteredBodies, minStatements: options.MinStatements, minTokens: options.MinTokens)
            : []);

        var clusters = new List<Cluster>();

        foreach (var group in unitClusters) {
            clusters.Add(item: BuildCluster(kind: "unit", members: group));
        }

        foreach (var group in blockClusters) {
            clusters.Add(item: BuildCluster(kind: "block", members: group));
        }

        clusters = OrderAndNumber(clusters: clusters);

        PrintSummary(clusters: clusters, minTokens: options.MinTokens, minStatements: options.MinStatements, includeBlocks: options.IncludeBlocks, filesScanned: corpus.FileCount);

        return (BuildJsonl(clusters: clusters), BuildCloneGroups(clusters: clusters, maxPerChunk: options.MaxPerChunk));
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

                units.Add(item: CreateMember(body: body, exact: exact, node: node, relative: relative, structural: structural, weight: weight));
            }
        }

        return units;
    }
    // The body spans of clustered units, per file — a block inside one of these is redundant with the
    // unit clone, so the block pass skips it.
    private static Dictionary<string, List<(int Start, int End)>> BodySpansByFile(List<List<Member>> clusters) {
        var spansByFile = new Dictionary<string, List<(int Start, int End)>>(comparer: StringComparer.Ordinal);

        foreach (var cluster in clusters) {
            foreach (var member in cluster) {
                if (!spansByFile.TryGetValue(key: member.File, value: out var spans)) {
                    spans = [];
                    spansByFile[member.File] = spans;
                }

                spans.Add(item: (member.BodyStart, member.BodyEnd));
            }
        }

        return spansByFile;
    }
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
                if (unitBodies.Contains(item: block) || (block.Statements.Count < minStatements)) {
                    continue;
                }

                var start = block.Span.Start;
                var end = block.Span.End;

                if ((bodies is not null) && bodies.Any(predicate: span => ((span.Start <= start) && (end <= span.End)))) {
                    continue;
                }

                var (structural, exact, weight) = Fingerprint(body: block);

                if (weight < minTokens) {
                    continue;
                }

                candidates.Add(item: (block, CreateMember(body: block, exact: exact, node: block, relative: relative, structural: structural, weight: weight)));
            }
        }

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
            if ((counts[candidate.Member.Structural] >= 2) && !candidate.Node.Ancestors().Any(predicate: clusteredNodes.Contains)) {
                survivors.Add(item: candidate.Member);
            }
        }

        return survivors
            .GroupBy(keySelector: static member => member.Structural, comparer: StringComparer.Ordinal)
            .Where(predicate: static group => (group.Count() >= 2))
            .Select(selector: static group => group.ToList())
            .ToList();
    }
    // Heaviest clones first (redundant mass = extra copies x weight), deterministically tie-broken, then
    // numbered.
    private static List<Cluster> OrderAndNumber(List<Cluster> clusters) {
        var ordered = clusters
            .OrderByDescending(keySelector: static cluster => ((cluster.Members.Count - 1) * cluster.Weight))
            .ThenByDescending(keySelector: static cluster => cluster.Members.Count)
            .ThenBy(keySelector: static cluster => cluster.Fingerprint, comparer: StringComparer.Ordinal)
            .ToList();

        for (var clusterIndex = 0; (clusterIndex < ordered.Count); clusterIndex++) {
            ordered[clusterIndex] = ordered[clusterIndex] with { Id = clusterIndex };
        }

        return ordered;
    }
    private static string BuildJsonl(List<Cluster> clusters) {
        var jsonl = new StringBuilder();

        foreach (var cluster in clusters) {
            var exactByHash = cluster.Members
                .GroupBy(keySelector: static member => member.Exact, comparer: StringComparer.Ordinal)
                .ToDictionary(keySelector: static group => group.Key, elementSelector: static group => group.Count(), comparer: StringComparer.Ordinal);

            jsonl.Append(value: '{')
                .Append(value: "\"id\":").Append(value: cluster.Id).Append(value: ',')
                .Append(value: "\"kind\":").Append(value: ScanJsonl.JsonString(value: cluster.Kind)).Append(value: ',')
                .Append(value: "\"fingerprint\":").Append(value: ScanJsonl.JsonString(value: cluster.Fingerprint)).Append(value: ',')
                .Append(value: "\"memberCount\":").Append(value: cluster.Members.Count).Append(value: ',')
                .Append(value: "\"exactCount\":").Append(value: cluster.ExactCount).Append(value: ',')
                .Append(value: "\"tokenWeight\":").Append(value: cluster.Weight).Append(value: ',')
                .Append(value: "\"redundantMass\":").Append(value: ((cluster.Members.Count - 1) * cluster.Weight)).Append(value: ',')
                .Append(value: "\"abiTainted\":").Append(value: (cluster.Abi ? "true" : "false")).Append(value: ',')
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
                    .Append(value: "\"exact\":").Append(value: ((exactByHash[member.Exact] >= 2) ? "true" : "false"))
                    .Append(value: '}');
            }

            jsonl.Append(value: "]}\n");
        }

        return jsonl.ToString();
    }
    private static void PrintSummary(List<Cluster> clusters, int minTokens, int minStatements, bool includeBlocks, int filesScanned) {
        var unitClusterCount = clusters.Count(predicate: static cluster => (cluster.Kind == "unit"));
        var blockClusterCount = clusters.Count(predicate: static cluster => (cluster.Kind == "block"));
        var siteCount = clusters.Sum(selector: static cluster => cluster.Members.Count);
        var redundantMass = clusters.Sum(selector: static cluster => ((cluster.Members.Count - 1) * cluster.Weight));
        var abiClusters = clusters.Count(predicate: static cluster => cluster.Abi);

        Console.Error.WriteLine(
            value: $"scan[clones]: {clusters.Count} clone clusters ({unitClusterCount} unit, {blockClusterCount} block; {abiClusters} abi-tainted) over {siteCount} sites; redundant token mass ~{redundantMass} (minTokens={minTokens}, minStatements={minStatements}, blocks={(includeBlocks ? "on" : "off")}, files scanned={filesScanned}).");

        foreach (var cluster in clusters.Take(count: 30)) {
            var fileCount = cluster.Members.Select(selector: static member => member.File).Distinct(comparer: StringComparer.Ordinal).Count();
            var abiTag = (cluster.Abi ? "[abi] " : "");
            var exactTag = ((cluster.ExactCount == cluster.Members.Count) ? "exact" : $"{cluster.ExactCount}/{cluster.Members.Count} exact");

            Console.Error.WriteLine(
                value: $"{((cluster.Members.Count - 1) * cluster.Weight),7}  x{cluster.Members.Count} w{cluster.Weight} {abiTag}{cluster.Kind} ({exactTag}) {cluster.Label} [{fileCount} file{((fileCount == 1) ? "" : "s")}]");
        }
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
                    .Append(value: "\"abiTainted\":").Append(value: (cluster.Abi ? "true" : "false")).Append(value: ',')
                    .Append(value: "\"label\":").Append(value: ScanJsonl.JsonString(value: cluster.Label)).Append(value: ',')
                    .Append(value: "\"members\":[");

                var end = Math.Min(val1: (offset + maxPerChunk), val2: cluster.Members.Count);

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
            var byFile = string.CompareOrdinal(strA: left.File, strB: right.File);

            return ((byFile != 0) ? byFile : left.StartLine.CompareTo(value: right.StartLine));
        });

        var exactCount = members
            .GroupBy(keySelector: static member => member.Exact, comparer: StringComparer.Ordinal)
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
            Members: members);
    }
    private static Member CreateMember(string relative, SyntaxNode node, SyntaxNode body, string structural, string exact, int weight) {
        var (startLine, endLine) = ScanJsonl.LineRange(location: node.GetLocation());
        var (typeName, memberName) = Describe(node: node);
        var unit = ((typeName.Length == 0) ? memberName : $"{typeName}.{memberName}");

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
            Abi: IsAbi(relative: relative, typeName: typeName));
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
    private static bool IsUnitNode(SyntaxNode node) => (node
        is MethodDeclarationSyntax
        or ConstructorDeclarationSyntax
        or DestructorDeclarationSyntax
        or OperatorDeclarationSyntax
        or ConversionOperatorDeclarationSyntax
        or AccessorDeclarationSyntax
        or LocalFunctionStatementSyntax);
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
                var ownerName = ((owner is null) ? "" : Describe(node: owner).Member);
                var line = (block.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

                return (typeName, $"{ownerName} block@{line}");
            default:
                return (typeName, "");
        }
    }
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
    // True when a member lives in a marshalling/ABI context, so triage can separate deliberate binding
    // mirrors from real copy-paste.
    private static bool IsAbi(string relative, string typeName) =>
        (relative.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "VulkanNative")
        || relative.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Vulkan/Bindings")
        || typeName.StartsWith(comparisonType: StringComparison.Ordinal, value: "Vk"));
    // Two hashes over the body's token stream (trivia excluded, so whitespace and comments never
    // matter): STRUCTURAL abstracts identifiers and literals to a placeholder (Type-2 clones collide),
    // EXACT keeps the raw token text (Type-1).
    private static (string Structural, string Exact, int Weight) Fingerprint(SyntaxNode body) {
        var structural = new StringBuilder();
        var exact = new StringBuilder();
        var weight = 0;

        foreach (var token in body.DescendantTokens()) {
            structural.Append(value: CanonicalToken(token: token)).Append(value: TokenSeparator);
            exact.Append(value: token.Text).Append(value: TokenSeparator);
            weight++;
        }

        return (Hash(value: structural.ToString()), Hash(value: exact.ToString()), weight);
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
    private static string Hash(string value) =>
        Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: value)));

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
