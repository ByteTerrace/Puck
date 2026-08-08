using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// The comment-WEAKNESS classifier — the same non-XML inline-comment corpus as CommentAnalyzer, but each
// comment is bucketed by the kind of weakness it represents (premise: self-documenting code needs no
// inline comment, so every one is a smell to triage). Buckets, first match wins:
//   sync-coupling      — a prose guardrail over a constraint the compiler can't see (source order:
//                        "load-bearing; do not alphabetize") or can't span (a C#<->shader contract:
//                        "KEEP IN SYNC with <file>"). The design-smell bucket: these mark a missing
//                        single source of truth, not a documentation gap — question the design, don't
//                        just reword the comment.
//   debt-marker        — TODO/FIXME/HACK/XXX/REVISIT/"for now": tracked deferred work.
//   banner-divider     — a run of >=4 rule glyphs: structure, not information.
//   commented-out-code — the body parses as a C# statement (with a real terminator/operator/keyword):
//                        dead code left in a comment.
//   unclassified       — everything else: the work-list a fan-out audit judges by hand (does it lie?
//                        does it earn its keep?). Purely syntactic detection can bucket the first four;
//                        whether a comment LIES is a semantic claim only a reader of the code can settle.
// Orthogonally, any comment that NAMES a cross-artifact referent (a shader file or an UPPER_SNAKE
// define) is resolved against the shader + C# corpus; an unresolved referent is the one slice of "the
// comment lies" a tool can prove. Substring resolution biases toward "exists" (it cries wolf rarely, by
// design). Hand-writes its json like its siblings.
internal sealed class CommentSmellAnalyzer : ISourceAnalyzer {
    // The design smell: prose standing in for a constraint the compiler can't enforce — intra-source
    // order ("load-bearing; do not alphabetize") or a cross-language contract ("keep in sync with
    // <shader>"). `must match/mirror` is gated by a nearby structure word so a plain API fact ("must
    // match the framebuffer") stays out.
    private static readonly Regex SyncPattern = new(
        pattern: @"keep[\s\w]{0,16}in sync|kept in sync|stay(?:s|ing)? in sync|in lockstep|do not (?:alphabetize|reorder|re-order|sort)|load-bearing|same order as|must (?:match|mirror)\b[^.\n]*\b(?:layout|order|struct|block|offset|enum|field|kernel|glsl|shader|push-constant)\b|mirror of the",
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DebtPattern = new(pattern: @"\b(?:TODO|FIXME|HACK|XXX|KLUDGE|REVISIT)\b|\bfor now\b", options: RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BannerPattern = new(pattern: @"[-=*#_~]{4,}", options: RegexOptions.Compiled);

    // A C# code signal: a trailing terminator, a leading statement keyword, or an operator prose rarely
    // carries. The gate keeps an English sentence that happens to parse as a bare expression out of the
    // commented-out-code bucket.
    private static readonly Regex CodeSignalPattern = new(
        pattern: @";\s*$|^\s*(?:return|if|for|foreach|while|var|using|throw|await|public|private|internal|protected)\b|=>|==|!=|&&|\|\|",
        options: RegexOptions.Compiled);

    // Cross-artifact referents a stale comment would dangle: a shader file name, or an UPPER_SNAKE
    // define (>= one underscore, so single words like NOTE/RED and acronyms like RGBA never register).
    // `hlsli` precedes `hlsl` in the alternation because this tree's HLSL include extension is the one a
    // "KEEP IN SYNC with <x>.hlsli" comment cites; it must stay in step with ShaderExtensions.
    private static readonly Regex ShaderFilePattern = new(pattern: @"\b[\w-]+\.(?:glsl|comp|frag|vert|hlsli|hlsl)\b", options: RegexOptions.Compiled);
    private static readonly Regex SymbolPattern = new(pattern: @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b", options: RegexOptions.Compiled);

    // The shader sources whose text and file names resolve a comment's cross-artifact referents. KEEP IN
    // STEP with ShaderFilePattern: an extension the pattern cites but this set omits would be reported
    // dangling on every citation.
    private static readonly string[] ShaderExtensions = [".glsl", ".comp", ".frag", ".vert", ".hlsl", ".hlsli"];

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var haystack = BuildReferentHaystack(corpus: corpus, shaderRoot: options.ShaderRoot, shaderFileNames: out var shaderFileNames);
        var jsonl = new StringBuilder();
        var byFile = new Dictionary<string, List<(int Line, string Text)>>();
        var bucketCounts = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
        var unresolved = 0;

        foreach (var parsed in corpus.Files) {
            foreach (var trivia in parsed.Root.DescendantTrivia()) {
                var kind = trivia.Kind();
                var isSingle = (kind == SyntaxKind.SingleLineCommentTrivia);

                if (!isSingle && (kind != SyntaxKind.MultiLineCommentTrivia)) {
                    continue;
                }

                var text = trivia.ToString().Trim();
                var body = StripMarkers(text: text);
                var bucket = Classify(body: body);

                bucketCounts[bucket] = (bucketCounts.GetValueOrDefault(key: bucket) + 1);

                var references = ResolveReferences(body: body, haystack: haystack, shaderFileNames: shaderFileNames, anyUnresolved: out var anyUnresolved);

                if (anyUnresolved) {
                    unresolved++;
                }

                var (startLine, endLine) = ScanJsonl.LineRange(location: trivia.GetLocation());

                jsonl.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: parsed.Relative)).Append(value: ',')
                    .Append(value: "\"line\":").Append(value: startLine).Append(value: ',')
                    .Append(value: "\"endLine\":").Append(value: endLine).Append(value: ',')
                    .Append(value: "\"kind\":").Append(value: (isSingle ? "\"single\"" : "\"multi\"")).Append(value: ',')
                    .Append(value: "\"bucket\":").Append(value: ScanJsonl.JsonString(value: bucket)).Append(value: ',')
                    .Append(value: "\"text\":").Append(value: ScanJsonl.JsonString(value: text));

                if (references.Length > 0) {
                    jsonl.Append(value: ",\"references\":[").Append(value: references).Append(value: ']');
                }

                jsonl.Append(value: "}\n");

                if (!byFile.TryGetValue(key: parsed.Relative, value: out var lines)) {
                    lines = [];
                    byFile[parsed.Relative] = lines;
                }

                lines.Add(item: (startLine, text));
            }
        }

        var total = bucketCounts.Values.Sum();

        Console.Error.WriteLine(
            value: $"scan[comment-smells]: {total} inline comments classified across {byFile.Count} files (of {corpus.FileCount} scanned).");

        foreach (var (bucket, count) in bucketCounts.OrderByDescending(keySelector: static pair => pair.Value)) {
            Console.Error.WriteLine(value: $"{count,5}  {bucket}");
        }

        Console.Error.WriteLine(value: $"{unresolved,5}  (comments with an UNRESOLVED cross-artifact referent — provable staleness)");

        return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile: byFile, maxPerChunk: options.MaxPerChunk));
    }

    private static string Classify(string body) {
        if (SyncPattern.IsMatch(input: body)) {
            return "sync-coupling";
        }

        if (DebtPattern.IsMatch(input: body)) {
            return "debt-marker";
        }

        if (BannerPattern.IsMatch(input: body)) {
            return "banner-divider";
        }

        if (LooksLikeCode(body: body)) {
            return "commented-out-code";
        }

        return "unclassified";
    }

    // True when the body parses as a C# statement with no errors AND carries a real code signal — so
    // dead code registers but an English sentence that happens to parse as a bare expression statement
    // does not.
    private static bool LooksLikeCode(string body) {
        if ((body.Length == 0) || !CodeSignalPattern.IsMatch(input: body)) {
            return false;
        }

        return !SyntaxFactory.ParseStatement(text: body).GetDiagnostics().Any(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));
    }

    // The comment text without its // or /* */ delimiters, so the classifiers see only the prose/code.
    private static string StripMarkers(string text) {
        var trimmed = text;

        if (trimmed.StartsWith(value: "//", comparisonType: StringComparison.Ordinal)) {
            trimmed = trimmed[2..];
        } else if (trimmed.StartsWith(value: "/*", comparisonType: StringComparison.Ordinal)) {
            trimmed = trimmed[2..];

            if (trimmed.EndsWith(value: "*/", comparisonType: StringComparison.Ordinal)) {
                trimmed = trimmed[..^2];
            }
        }

        return trimmed.Trim();
    }

    // One text blob of every scanned .cs file (COMMENTS REMOVED) plus every shader source, and the set
    // of shader file names — what a "does this referent still exist?" check resolves against (substring
    // existence, not exact symbol binding). Comments are excluded because the probe would otherwise
    // resolve a cited define against the very comment that cites it, making the detector a tautology.
    private static string BuildReferentHaystack(SourceCorpus corpus, string shaderRoot, out HashSet<string> shaderFileNames) {
        var builder = new StringBuilder();

        shaderFileNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in corpus.Files) {
            AppendWithoutComments(builder: builder, parsed: parsed);
            builder.Append(value: '\n');
        }

        if (Directory.Exists(path: shaderRoot)) {
            foreach (var path in Directory.EnumerateFiles(path: shaderRoot, searchPattern: "*.*", searchOption: SearchOption.AllDirectories)) {
                if (ShaderExtensions.Contains(value: Path.GetExtension(path: path).ToLowerInvariant(), comparer: StringComparer.Ordinal)) {
                    shaderFileNames.Add(item: Path.GetFileName(path: path));
                    builder.Append(value: File.ReadAllText(path: path)).Append(value: '\n');
                }
            }
        }

        return builder.ToString();
    }

    // The file's text with every comment span elided — code, string literals and #directives survive,
    // prose does not.
    private static void AppendWithoutComments(StringBuilder builder, ParsedFile parsed) {
        var text = parsed.Text;
        var cursor = 0;

        foreach (var trivia in parsed.Root.DescendantTrivia()) {
            if (!IsComment(trivia: trivia)) {
                continue;
            }

            var span = trivia.Span;

            if (span.Start < cursor) {
                continue;
            }

            builder.Append(value: text, startIndex: cursor, count: (span.Start - cursor));
            cursor = span.End;
        }

        builder.Append(value: text, startIndex: cursor, count: (text.Length - cursor));
    }
    private static bool IsComment(SyntaxTrivia trivia) => (trivia.Kind() is
        SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia);

    // The cross-artifact referents named in the body, each tagged resolved/dangling, as the JSON array
    // body (no brackets); sets anyUnresolved when one dangles.
    private static string ResolveReferences(string body, string haystack, HashSet<string> shaderFileNames, out bool anyUnresolved) {
        anyUnresolved = false;

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var parts = new List<string>();

        foreach (Match match in ShaderFilePattern.Matches(input: body)) {
            if (!seen.Add(item: match.Value)) {
                continue;
            }

            var resolved = shaderFileNames.Contains(item: match.Value);

            anyUnresolved |= !resolved;
            parts.Add(item: Reference(token: match.Value, kind: "file", resolved: resolved));
        }

        foreach (Match match in SymbolPattern.Matches(input: body)) {
            if (!seen.Add(item: match.Value)) {
                continue;
            }

            var resolved = haystack.Contains(value: match.Value, comparisonType: StringComparison.Ordinal);

            anyUnresolved |= !resolved;
            parts.Add(item: Reference(token: match.Value, kind: "symbol", resolved: resolved));
        }

        return string.Join(separator: ",", values: parts);
    }
    private static string Reference(string token, string kind, bool resolved) =>
        $"{{\"token\":{ScanJsonl.JsonString(value: token)},\"kind\":\"{kind}\",\"resolved\":{(resolved ? "true" : "false")}}}";
}
