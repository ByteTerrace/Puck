using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// The branch-scoped duplicate check: for each type and method a branch adds or changes (--against), the declarations
// elsewhere in the tree that carry the same technique under another name, and the `private`/`internal` helpers whose shape
// is a general technique that has a shared home. `clones` finds token-identical code over the whole tree; most duplicates
// this analyzer exists for are not identical, they are one technique written again (a reciprocal divisor, a Fibonacci-mix
// comparer), so it matches on what a technique cannot rename (TechniqueFeatures: constants, BCL markers, idioms) and on the
// shape of the body with its identifiers abstracted.
//   neighbour — a declaration of the same kind (type, or method/constructor/operator) the changed one resembles, ranked by
//               score = the summed rarity of the technique facts they share + 12 x the Jaccard of their shingled shapes. A
//               fact's rarity is ln((n + 1) / (df + 1)) over the tree, so `Interlocked` (everywhere) weighs little and a
//               golden-ratio constant (twice) weighs much. A candidate shows when its fact score reaches 6 or its shape
//               reaches 0.4; the five best per changed declaration are kept.
//   helper    — a `private`/`internal` changed declaration whose name or markers say divisor, hash, comparer, pool, random,
//               saturating or rounding arithmetic, retry or cache, outside the shared home that category names; the record
//               lists what that home already holds under a matching name.
// A declaration counts as changed when a type's name line, or any line of a method, falls in a line the diff adds; a file
// not yet tracked counts as wholly added. Detection is syntactic: it ranks, it does not decide, and a hit is a lead to read,
// never a verdict. Shape comparison covers declarations of at least 24 tokens within a factor of four in size, since a
// bottom-k sketch of the shingles stands in for the full sets until a candidate is verified.
internal sealed class DuplicateAnalyzer : ISourceAnalyzer {
    private const double MinFeatureScore = 8.0;
    private const double MinShape = 0.4;
    private const double MinSketchEstimate = 0.15;
    private const int MinShapeTokens = 24;
    // A fact held by more declarations than this (`MemoryMarshal`, `Interlocked`) marks no technique worth a lead: it is
    // too common to say two declarations are one.
    private const int PostingCap = 60;
    private const int SketchSize = 24;
    private const double OverlapWeight = 4.0;
    private const double ShapeWeight = 12.0;
    private const int TopPerDeclaration = 5;
    private const int VerifiedPerDeclaration = 20;

    // Members whose names a contract chose, so a name match on one says nothing about technique.
    private static readonly HashSet<string> Overrides = new(comparer: StringComparer.Ordinal) { "CompareTo", "Equals", "GetHashCode", "ToString" };
    private static readonly Regex Simple =new(pattern: "[A-Za-z0-9_]+$", options: RegexOptions.CultureInvariant);

    private static string? MethodName(SyntaxNode node) => node switch {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax => ".ctor",
        OperatorDeclarationSyntax op => $"operator{op.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax => "operator",
        _ => null,
    };
    private static bool HasBody(SyntaxNode node) => node switch {
        MethodDeclarationSyntax method => ((method.Body is not null) || (method.ExpressionBody is not null)),
        ConstructorDeclarationSyntax constructor => ((constructor.Body is not null) || (constructor.ExpressionBody is not null)),
        OperatorDeclarationSyntax op => ((op.Body is not null) || (op.ExpressionBody is not null)),
        ConversionOperatorDeclarationSyntax conversion => ((conversion.Body is not null) || (conversion.ExpressionBody is not null)),
        _ => false,
    };
    // Whether a declaration is reachable only from its assembly or narrower: no public or protected modifier, in a
    // declaration that is not an interface member. An override or an explicit implementation takes its name from a contract
    // the author did not choose, so it is never a helper.
    private static bool IsHelperScope(MemberDeclarationSyntax member) {
        if (
            member.Modifiers.Any(kind: SyntaxKind.PublicKeyword) ||
            member.Modifiers.Any(kind: SyntaxKind.ProtectedKeyword) ||
            member.Modifiers.Any(kind: SyntaxKind.OverrideKeyword) ||
            ((member is MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null }))
        ) {
            return false;
        }

        return (member.Parent is not InterfaceDeclarationSyntax);
    }
    // The Jaccard similarity of two ascending sets of shape hashes, read over the smallest `limit` distinct hashes of their
    // union: of two bottom-k sketches (limit SketchSize) an estimate of the full sets' similarity, of two full sets
    // (limit int.MaxValue) exact.
    private static double Similarity(ulong[] left, ulong[] right, int limit) {
        int leftAt = 0, rightAt = 0, taken = 0, shared = 0;

        while ((taken < limit) && ((leftAt < left.Length) || (rightAt < right.Length))) {
            if (rightAt == right.Length) {
                leftAt++;
            } else if (leftAt == left.Length) {
                rightAt++;
            } else if (left[leftAt] == right[rightAt]) {
                leftAt++;
                rightAt++;
                shared++;
            } else if (left[leftAt] < right[rightAt]) {
                leftAt++;
            } else {
                rightAt++;
            }

            taken++;
        }

        return ((taken == 0)
            ? 0.0
            : (((double)shared) / taken));
    }
    private static bool Overlaps(Declaration left, Declaration right) =>
        (string.Equals(a: left.File, b: right.File, comparisonType: StringComparison.Ordinal) && (left.StartLine <= right.EndLine) && (right.StartLine <= left.EndLine));
    private static bool InAdded(List<CliGitDiff.LineRange>? ranges, int start, int end) =>
        ((ranges is not null) && ranges.Any(predicate: range => ((range.Start <= end) && (start <= range.End))));
    // Every type and body-carrying method of one file, with its technique facts and shape sketch.
    private static List<Declaration> Declarations(ParsedFile file) {
        var found = new List<Declaration>();

        foreach (var node in file.Root.DescendantNodes()) {
            Kind kind;
            string unit;
            int start;

            if (node is TypeDeclarationSyntax { Members.Count: > 0 } type) {
                kind = Kind.Type;
                unit = ScanSyntax.TypePath(node: type);
                start = (type.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            } else if (HasBody(node: node)) {
                kind = Kind.Method;
                unit = $"{ScanSyntax.TypePath(node: node)}.{MethodName(node: node)}";
                start = (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            } else {
                continue;
            }

            var (_, endLine) = ScanJsonl.LineRange(location: node.GetLocation());
            var shingles = TechniqueFeatures.Shingles(
                node: node,
                tokenCount: out var tokenCount
            );
            var features = TechniqueFeatures.Features(node: node);

            // Too small to carry a shape and no technique fact: nothing could match it, and unless its name says
            // technique it is not a helper to flag.
            if (
                (features is null) &&
                (tokenCount < MinShapeTokens) &&
                !TechniqueFeatures.Categories.Any(predicate: category => category.Names.IsMatch(input: SimpleName(unit: unit)))
            ) {
                continue;
            }

            found.Add(item: new Declaration(
                EndLine: endLine,
                Features: features,
                File: file.Relative,
                HelperScope: IsHelperScope(member: (MemberDeclarationSyntax)node),
                Kind: kind,
                Node: node,
                Sketch: ((tokenCount >= MinShapeTokens)
                    ? shingles.Take(count: SketchSize).ToArray()
                    : null),
                StartLine: start,
                TokenCount: tokenCount,
                Unit: unit
            ));
        }

        return found;
    }
    private static string SimpleName(string unit) => Simple.Match(input: unit).Value;
    // Whether the shape sets of two declarations are comparable by sketch: both carry one, within a factor of four in size.
    private static bool Comparable(Declaration left, Declaration right) =>
        ((left.Sketch is not null) && (right.Sketch is not null) && (left.TokenCount <= (4 * right.TokenCount)) && (right.TokenCount <= (4 * left.TokenCount)));

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var branch = options.Branch!;
        var perFile = new List<Declaration>[corpus.Files.Count];

        Parallel.For(
            body: index => {
                var file = corpus.Files[index];

                perFile[index] = (CloneAnalyzer.IsGenerated(relative: file.Relative, root: file.Root)
                    ? []
                    : Declarations(file: file));
            },
            fromInclusive: 0,
            toExclusive: corpus.Files.Count
        );

        var declarations = perFile.SelectMany(selector: static list => list).ToList();
        var postings = new Dictionary<string, List<int>>[2] {
            new(comparer: StringComparer.Ordinal),
            new(comparer: StringComparer.Ordinal),
        };
        var counts = new int[2];
        var sketched = new List<int>[2] { [], [] };

        for (var at = 0; (at < declarations.Count); at++) {
            var declaration = declarations[at];

            counts[((int)declaration.Kind)]++;

            if (declaration.Sketch is not null) {
                sketched[((int)declaration.Kind)].Add(item: at);
            }

            if (declaration.Features is null) {
                continue;
            }

            foreach (var feature in declaration.Features) {
                if (!postings[((int)declaration.Kind)].TryGetValue(key: feature, value: out var posting)) {
                    posting = [];
                    postings[((int)declaration.Kind)][feature] = posting;
                }

                posting.Add(item: at);
            }
        }

        // The declarations the branch wrote: a type whose name line is added, a method any of whose lines is.
        var subjects = new List<int>();

        for (var at = 0; (at < declarations.Count); at++) {
            var declaration = declarations[at];

            _ = branch.Added.TryGetValue(
                key: (branch.PathPrefix + declaration.File),
                value: out var ranges
            );

            if (InAdded(
                end: ((declaration.Kind == Kind.Type) ? declaration.StartLine : declaration.EndLine),
                ranges: ranges,
                start: declaration.StartLine
            )) {
                subjects.Add(item: at);
            }
        }

        var subjectSet = subjects.ToHashSet();
        var everything = new List<Neighbour>[subjects.Count];

        Parallel.For(
            body: index => everything[index] = Neighbours(
                counts: counts,
                declarations: declarations,
                postings: postings,
                sketched: sketched,
                subject: subjects[index]
            ),
            fromInclusive: 0,
            toExclusive: subjects.Count
        );

        // A pair of two changed declarations is found from both sides; it is reported once, from the earlier.
        var seen = new HashSet<(int, int)>();
        var ranked = new List<Neighbour>();

        foreach (var neighbour in everything.SelectMany(selector: static list => list.Take(count: TopPerDeclaration))) {
            if (
                subjectSet.Contains(item: neighbour.Other) &&
                !seen.Add(item: (Math.Min(val1: neighbour.Subject, val2: neighbour.Other), Math.Max(val1: neighbour.Subject, val2: neighbour.Other)))
            ) {
                continue;
            }

            ranked.Add(item: neighbour);
        }

        ranked = [.. ranked
            .OrderByDescending(keySelector: static neighbour => neighbour.Score)
            .ThenBy(keySelector: neighbour => declarations[neighbour.Subject].File, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: neighbour => declarations[neighbour.Subject].StartLine)
            .ThenBy(keySelector: neighbour => declarations[neighbour.Other].File, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: neighbour => declarations[neighbour.Other].StartLine)];

        var sites = new ScanSites();
        var digestLines = new List<string>();
        var rank = 0;

        foreach (var neighbour in ranked) {
            var subject = declarations[neighbour.Subject];
            var other = declarations[neighbour.Other];
            var reasons = new List<string>();

            if (neighbour.Shape >= 0.1) {
                reasons.Add(item: $"shape {neighbour.Shape.ToString(format: "0.00", provider: CultureInfo.InvariantCulture)}");
            }

            reasons.AddRange(collection: neighbour.Shared);
            rank++;

            var scoreText = neighbour.Score.ToString(format: "0.0", provider: CultureInfo.InvariantCulture);
            var text = $"{subject.Unit} resembles {other.Unit} ({other.File}:{other.StartLine}) score {scoreText}: {string.Join(separator: ", ", values: reasons)}";

            sites.Add(
                endLine: subject.EndLine,
                extra: $"\"rank\":{rank},\"score\":{scoreText},\"other\":{{\"file\":{ScanJsonl.JsonString(value: other.File)},\"line\":{other.StartLine},\"endLine\":{other.EndLine},\"unit\":{ScanJsonl.JsonString(value: other.Unit)}}},\"reasons\":[{string.Join(separator: ",", values: reasons.Select(selector: static reason => ScanJsonl.JsonString(value: reason)))}]",
                file: subject.File,
                kind: "neighbour",
                line: subject.StartLine,
                text: text
            );

            if (digestLines.Count < 30) {
                digestLines.Add(item: $"{scoreText,6}  {subject.File}:{subject.StartLine} {subject.Unit}  ~  {other.File}:{other.StartLine} {other.Unit}  [{string.Join(separator: ", ", values: reasons)}]");
            }
        }

        var helpers = 0;

        for (var index = 0; (index < subjects.Count); index++) {
            var subject = declarations[subjects[index]];

            if (!subject.HelperScope) {
                continue;
            }

            // Every technique the helper's name or markers claim, outside the home that technique has.
            var claims = new List<(TechniqueFeatures.HelperCategory Category, string Why)>();

            foreach (var category in TechniqueFeatures.Categories) {
                var byName = category.Names.IsMatch(input: SimpleName(unit: subject.Unit));
                var marked = category.Markers.Where(predicate: marker => ((subject.Features is not null) && subject.Features.Contains(item: marker))).ToList();

                if ((!byName && (marked.Count == 0)) || TechniqueFeatures.InHome(category: category, relative: subject.File)) {
                    continue;
                }

                claims.Add(item: (category, (byName
                    ? $"its name says {category.Name}"
                    : $"it uses {string.Join(separator: ", ", values: marked.Select(selector: static marker => marker[(marker.IndexOf(value: ':') + 1)..]))} ({category.Name})")));
            }

            if (claims.Count == 0) {
                continue;
            }

            // What the first claim's home already holds: the neighbours of the helper that live there, nearest first, then
            // declarations there under a matching name (an interface override is named by its contract, so it matches nothing).
            var home = claims[0].Category;
            var holders = Enumerable.Range(start: 0, count: subjects.Count)
                .Where(predicate: other => ((other == index) || Overlaps(left: subject, right: declarations[subjects[other]])))
                .SelectMany(selector: other => everything[other])
                .OrderByDescending(keySelector: static neighbour => neighbour.Score)
                .Where(predicate: neighbour => TechniqueFeatures.InHome(category: home, relative: declarations[neighbour.Other].File))
                .Select(selector: static neighbour => neighbour.Other)
                .Distinct()
                .Take(count: 3)
                .ToList();

            holders.AddRange(collection: declarations
                .Select(selector: (declaration, at) => (declaration, at))
                .Where(predicate: pair => (
                    TechniqueFeatures.InHome(category: home, relative: pair.declaration.File) &&
                    home.Names.IsMatch(input: SimpleName(unit: pair.declaration.Unit)) &&
                    !Overrides.Contains(item: SimpleName(unit: pair.declaration.Unit)) &&
                    !holders.Contains(item: pair.at)
                ))
                .OrderBy(keySelector: static pair => pair.declaration.Kind)
                .Select(selector: static pair => pair.at)
                .Take(count: 3));

            var holds = ((holders.Count == 0)
                ? string.Empty
                : $"; {home.Home} already holds {string.Join(separator: ", ", values: holders.Select(selector: at => $"{declarations[at].Unit} ({declarations[at].File}:{declarations[at].StartLine})"))}");

            helpers++;
            sites.Add(
                endLine: subject.EndLine,
                extra: $"\"categories\":[{string.Join(separator: ",", values: claims.Select(selector: static claim => ScanJsonl.JsonString(value: claim.Category.Name)))}],\"home\":{ScanJsonl.JsonString(value: home.Home)}",
                file: subject.File,
                kind: "helper",
                line: subject.StartLine,
                text: $"{subject.Unit} is a private or internal helper: {string.Join(separator: "; ", values: claims.Select(selector: static claim => claim.Why))}. Search {home.Home} before keeping it{holds}"
            );
        }

        var types = subjects.Count(predicate: at => (declarations[at].Kind == Kind.Type));

        Console.Error.WriteLine(value: $"scan[duplicates]: {subjects.Count} changed declarations ({types} types, {(subjects.Count - types)} methods) against {branch.Against} through {branch.Head}; {ranked.Count} neighbours, {helpers} helper flags (of {declarations.Count} declarations in {corpus.FileCount} files).");

        foreach (var line in digestLines) {
            Console.Error.WriteLine(value: line);
        }

        return sites.Finish(maxPerChunk: options.MaxPerChunk);
    }
    private static List<Neighbour> Neighbours(int subject, List<Declaration> declarations, Dictionary<string, List<int>>[] postings, List<int>[] sketched, int[] counts) {
        var self = declarations[subject];
        var kind = ((int)self.Kind);
        var scores = new Dictionary<int, (double Facts, List<string> Shared)>();

        if (self.Features is not null) {
            foreach (var feature in self.Features.Order(comparer: StringComparer.Ordinal)) {
                if (
                    !postings[kind].TryGetValue(key: feature, value: out var posting) ||
                    (posting.Count > PostingCap)
                ) {
                    continue;
                }

                var rarity = Math.Log(d: (((double)(counts[kind] + 1)) / (posting.Count + 1)));
                var rarityText = rarity.ToString(format: "0.0", provider: CultureInfo.InvariantCulture);

                foreach (var at in posting) {
                    if ((at == subject) || Overlaps(left: self, right: declarations[at])) {
                        continue;
                    }

                    if (!scores.TryGetValue(key: at, value: out var entry)) {
                        entry = (0.0, new List<string>());
                    }

                    entry.Shared.Add(item: $"shares {feature[(feature.IndexOf(value: ':') + 1)..]} ({rarityText})");
                    scores[at] = (entry.Facts + rarity, entry.Shared);
                }
            }
        }

        var estimates = new Dictionary<int, double>();

        if (self.Sketch is not null) {
            foreach (var at in sketched[kind]) {
                var other = declarations[at];

                if ((at == subject) || !Comparable(left: self, right: other) || Overlaps(left: self, right: other)) {
                    continue;
                }

                var estimate = Similarity(left: self.Sketch, limit: SketchSize, right: other.Sketch!);

                if (estimate >= MinSketchEstimate) {
                    estimates[at] = estimate;
                }
            }
        }

        // The sketch is an estimate; the best candidates by it, and every one sharing a fact, are verified on the exact sets.
        var verified = new Dictionary<int, double>();

        if (self.Sketch is not null) {
            var own = TechniqueFeatures.Shingles(node: self.Node, tokenCount: out _);

            foreach (var at in estimates.OrderByDescending(keySelector: static pair => pair.Value).ThenBy(keySelector: static pair => pair.Key).Take(count: VerifiedPerDeclaration).Select(selector: static pair => pair.Key)) {
                verified[at] = Similarity(
                    left: own,
                    limit: int.MaxValue,
                    right: TechniqueFeatures.Shingles(node: declarations[at].Node, tokenCount: out _)
                );
            }

            foreach (var at in scores.Keys.Order()) {
                if (
                    !verified.ContainsKey(key: at) &&
                    Comparable(left: self, right: declarations[at]) &&
                    (Similarity(left: self.Sketch, limit: SketchSize, right: declarations[at].Sketch!) >= MinSketchEstimate)
                ) {
                    verified[at] = Similarity(
                        left: own,
                        limit: int.MaxValue,
                        right: TechniqueFeatures.Shingles(node: declarations[at].Node, tokenCount: out _)
                    );
                }
            }
        }

        var results = new List<Neighbour>();

        foreach (var at in scores.Keys.Union(second: verified.Keys).Order()) {
            var (facts, shared) = scores.GetValueOrDefault(key: at, defaultValue: (0.0, new List<string>()));
            var shape = verified.GetValueOrDefault(key: at);

            if ((facts < MinFeatureScore) && (shape < MinShape)) {
                continue;
            }

            // Of two candidates that share a fact, the one whose whole set of facts is closer is the likelier copy.
            var union = ((self.Features?.Count ?? 0) + (declarations[at].Features?.Count ?? 0) - shared.Count);
            var overlap = ((union > 0)
                ? (((double)shared.Count) / union)
                : 0.0);

            results.Add(item: new Neighbour(
                Other: at,
                Score: (facts + (ShapeWeight * shape) + (OverlapWeight * overlap)),
                Shape: shape,
                Shared: shared,
                Subject: subject
            ));
        }

        return [.. results
            .OrderByDescending(keySelector: static neighbour => neighbour.Score)
            .ThenBy(keySelector: static neighbour => neighbour.Other)];
    }

    private enum Kind { Type = 0, Method = 1 }
    private sealed record Declaration(string File, int StartLine, int EndLine, string Unit, Kind Kind, SyntaxNode Node, HashSet<string>? Features, ulong[]? Sketch, int TokenCount, bool HelperScope);
    private sealed record Neighbour(int Subject, int Other, double Score, double Shape, List<string> Shared);
}
