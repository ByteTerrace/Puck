using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Analyzers;
using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// The comment-weakness report: the same non-XML inline-comment corpus as CommentAnalyzer, each comment bucketed by
// Puck.Analyzers.CommentSmellClassifier, the classification CommentSmellAnalyzer ratchets in every build.
// Orthogonally: every comment carries blockSize, the run of consecutive comment lines it belongs to,
// because the dominant rot is a BLOCK property no per-line rule can see — a 92-line essay is 92
// individually innocent lines. And any comment that NAMES a cross-artifact referent is resolved against
// the corpus, in two tiers that must not be conflated: a FILE referent (shader or document) that
// resolves nowhere is PROVABLE staleness, while an UPPER_SNAKE symbol absent from our sources is only
// ADVISORY — this corpus holds our code alone, so an external SDK or hardware constant is
// indistinguishable from a define that vanished, and a sweep that read the two as one deleted correct
// spec citations. Substring resolution biases toward "exists". Hand-writes its json like its siblings.
internal sealed class CommentSmellScan : ISourceAnalyzer {
    // Cross-artifact referents a stale comment would dangle: a shader file name, or an UPPER_SNAKE
    // define (>= one underscore, so single words like NOTE/RED and acronyms like RGBA never register).
    // `hlsli` precedes `hlsl` in the alternation because this tree's HLSL include extension is the one a
    // "KEEP IN SYNC with <x>.hlsli" comment cites; it must stay in step with ShaderExtensions.
    private static readonly Regex ShaderFilePattern = new(
        options: RegexOptions.Compiled,
        pattern: @"\b[\w-]+\.(?:glsl|comp|frag|vert|hlsli|hlsl)\b"
    );
    // A cited markdown document, with or without its directory. Resolved by FILE NAME against docs/, so
    // a retired plan still cited from code reports dangling.
    private static readonly Regex DocumentFilePattern = new(
        options: RegexOptions.Compiled,
        pattern: @"\b(?:[\w./-]*/)?[\w.-]+\.md\b"
    );
    private static readonly Regex SymbolPattern = new(
        options: RegexOptions.Compiled,
        pattern: @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b"
    );
    // The shader sources whose text and file names resolve a comment's cross-artifact referents. KEEP IN
    // STEP with ShaderFilePattern: an extension the pattern cites but this set omits would be reported
    // dangling on every citation.
    private static readonly string[] ShaderExtensions = [".glsl", ".comp", ".frag", ".vert", ".hlsl", ".hlsli"];

    // The run length at which a comment stops being an annotation and becomes an essay. Not a limit the
    // scan enforces — the threshold that puts a block on the audit's work list.
    private const int EssayLineCount = 6;

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

            builder.Append(
                value: text,
                startIndex: cursor,
                count: (span.Start - cursor)
            );
            cursor = span.End;
        }

        builder.Append(
            value: text,
            startIndex: cursor,
            count: (text.Length - cursor)
        );
    }
    // Every markdown file name in the repository, which is what a comment's cited document path resolves
    // against. Name-only, matching the shader probe: a comment citing a moved-but-live document is not
    // the failure being hunted — a citation to a document that exists NOWHERE is.
    private static HashSet<string> BuildDocumentNames(string repositoryRoot) {
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        if (
            (repositoryRoot.Length == 0) ||
            !Directory.Exists(path: repositoryRoot)
        ) {
            return names;
        }

        foreach (var path in (FileWalk.Enumerate(
            verb: "scan",
            roots: [repositoryRoot],
            include: [],
            exclude: [],
            extension: ".md"
        ) ?? [])) {
            names.Add(item: Path.GetFileName(path: path));
        }

        return names;
    }
    // One text blob of every scanned .cs file (COMMENTS REMOVED) plus every shader source, and the set
    // of shader file names — what a "does this referent still exist?" check resolves against (substring
    // existence, not exact symbol binding). Comments are excluded because the probe would otherwise
    // resolve a cited define against the very comment that cites it, making the detector a tautology.
    private static string BuildReferentHaystack(SourceCorpus corpus, string shaderRoot, out HashSet<string> shaderFileNames) {
        var builder = new StringBuilder();

        shaderFileNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in corpus.Files) {
            AppendWithoutComments(
                builder: builder,
                parsed: parsed
            );
            builder.Append(value: '\n');
        }

        if (Directory.Exists(path: shaderRoot)) {
            foreach (var path in (FileWalk.Enumerate(
                admit: static path => ShaderExtensions.Contains(
                    comparer: StringComparer.Ordinal,
                    value: Path.GetExtension(path: path).ToLowerInvariant()
                ),
                exclude: [],
                include: [],
                roots: [shaderRoot],
                verb: "scan"
            ) ?? [])) {
                shaderFileNames.Add(item: Path.GetFileName(path: path));
                builder.Append(value: File.ReadAllText(path: path)).Append(value: '\n');
            }
        }

        return builder.ToString();
    }
    // The comment trivia of one file in source order, as the tuple the block pass and the emitter share.
    private static List<(int StartLine, int EndLine, bool IsSingle, string Text)> CollectComments(ParsedFile parsed) {
        var comments = new List<(int, int, bool, string)>();

        foreach (var trivia in parsed.Root.DescendantTrivia()) {
            if (!CommentSmellClassifier.IsInlineComment(trivia: trivia)) {
                continue;
            }

            var isSingle = trivia.IsKind(kind: SyntaxKind.SingleLineCommentTrivia);

            var (startLine, endLine) = ScanJsonl.LineRange(location: trivia.GetLocation());

            comments.Add(item: (startLine, endLine, isSingle, trivia.ToString().Trim()));
        }

        return comments;
    }
    private static bool IsComment(SyntaxTrivia trivia) => (trivia.Kind() is
        SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia);
    // Per-comment run length: consecutive comments with no gap between them are one block, and every
    // member reports the WHOLE block's line count. A multi-line /* */ comment counts its own span.
    private static int[] MeasureBlocks(List<(int StartLine, int EndLine, bool IsSingle, string Text)> comments) {
        var sizes = new int[comments.Count];

        if (comments.Count == 0) {
            return sizes;
        }

        var start = 0;

        for (var index = 1; (index <= comments.Count); index++) {
            var breaks = ((index == comments.Count) || (comments[index].StartLine > (comments[(index - 1)].EndLine + 1)));

            if (!breaks) {
                continue;
            }

            var lineCount = ((comments[(index - 1)].EndLine - comments[start].StartLine) + 1);

            for (var member = start; (member < index); member++) {
                sizes[member] = lineCount;
            }

            start = index;
        }

        return sizes;
    }
    private static string Reference(string token, string kind, bool resolved) =>
        $"{{\"token\":{ScanJsonl.JsonString(value: token)},\"kind\":\"{kind}\",\"resolved\":{(resolved
            ? "true"
            : "false")}}}";
    // The cross-artifact referents named in the body, each tagged resolved/dangling, as the JSON array
    // body (no brackets); sets anyUnresolved when one dangles.
    private static string ResolveReferences(string body, string haystack, HashSet<string> shaderFileNames, HashSet<string> docNames, out bool anyUnresolved, out bool anySymbolUnresolved) {
        anyUnresolved = false;
        anySymbolUnresolved = false;

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var parts = new List<string>();

        if (docNames.Count > 0) {
            foreach (Match match in DocumentFilePattern.Matches(input: body)) {
                if (!seen.Add(item: match.Value)) {
                    continue;
                }

                var resolvedDoc = docNames.Contains(item: Path.GetFileName(path: match.Value));

                anyUnresolved |= !resolvedDoc;
                parts.Add(item: Reference(
                    token: match.Value,
                    kind: "doc",
                    resolved: resolvedDoc
                ));
            }
        }

        foreach (Match match in ShaderFilePattern.Matches(input: body)) {
            if (!seen.Add(item: match.Value)) {
                continue;
            }

            // `<name>.comp` is this tree's established shorthand for the `<name>.comp.hlsl` source, so
            // the bare form must resolve or every shader citation reports dangling.
            var resolved = (shaderFileNames.Contains(item: match.Value) || shaderFileNames.Contains(item: (match.Value + ".hlsl")));

            anyUnresolved |= !resolved;
            parts.Add(item: Reference(
                token: match.Value,
                kind: "file",
                resolved: resolved
            ));
        }

        foreach (Match match in SymbolPattern.Matches(input: body)) {
            if (!seen.Add(item: match.Value)) {
                continue;
            }

            var resolved = haystack.Contains(
                value: match.Value,
                comparisonType: StringComparison.Ordinal
            );

            // ADVISORY, never provable: this corpus holds only OUR sources, so an external SDK or
            // hardware constant (VK_…, XINPUT_…, a disassembly's OBJECT_LENGTH) is indistinguishable
            // from a define that vanished. Reported apart from file referents for that reason — a
            // sweep that treated these as staleness deleted correct spec citations.
            anySymbolUnresolved |= !resolved;
            parts.Add(item: Reference(
                token: match.Value,
                kind: "symbol",
                resolved: resolved
            ));
        }

        return string.Join(
            separator: ",",
            values: parts
        );
    }

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var haystack = BuildReferentHaystack(
            corpus: corpus,
            shaderRoot: options.ShaderRoot,
            shaderFileNames: out var shaderFileNames
        );
        var jsonl = new StringBuilder();
        var byFile = new Dictionary<string, List<(int Line, string Text)>>();
        var bucketCounts = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
        var unresolved = 0;
        var symbolAdvisories = 0;

        var docNames = BuildDocumentNames(repositoryRoot: options.RepositoryRoot);
        var essayBlocks = 0;
        var essayLines = 0;

        foreach (var parsed in corpus.Files) {
            var comments = CollectComments(parsed: parsed);

            // Block sizes first: a run's length is a property of the whole run, so no record can be
            // emitted until the run it belongs to has ended.
            var blockSizes = MeasureBlocks(comments: comments);

            for (var index = 0; (index < comments.Count); index++) {
                var (startLine, endLine, isSingle, text) = comments[index];
                var body = CommentSmellClassifier.Body(text: text);
                var bucket = CommentSmellClassifier.Classify(body: body);
                var blockSize = blockSizes[index];

                bucketCounts[bucket] = (bucketCounts.GetValueOrDefault(key: bucket) + 1);

                if (blockSize >= EssayLineCount) {
                    essayLines++;

                    if (
                        (index == 0) ||
                        (blockSizes[(index - 1)] != blockSize) ||
                        ((comments[(index - 1)].EndLine + 1) != startLine)
                    ) {
                        essayBlocks++;
                    }
                }

                var references = ResolveReferences(
                    anySymbolUnresolved: out var anySymbolUnresolved,
                    anyUnresolved: out var anyUnresolved,
                    body: body,
                    docNames: docNames,
                    haystack: haystack,
                    shaderFileNames: shaderFileNames
                );

                if (anyUnresolved) {
                    unresolved++;
                }

                if (anySymbolUnresolved) {
                    symbolAdvisories++;
                }

                jsonl.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: parsed.Relative)).Append(value: ',')
                    .Append(value: "\"line\":").Append(value: startLine).Append(value: ',')
                    .Append(value: "\"endLine\":").Append(value: endLine).Append(value: ',')
                    .Append(value: "\"kind\":").Append(value: (isSingle
                    ? "\"single\""
                    : "\"multi\"")).Append(value: ',')
                    .Append(value: "\"bucket\":").Append(value: ScanJsonl.JsonString(value: bucket)).Append(value: ',')
                    .Append(value: "\"blockSize\":").Append(value: blockSize).Append(value: ',')
                    .Append(value: "\"text\":").Append(value: ScanJsonl.JsonString(value: text));

                if (references.Length > 0) {
                    jsonl.Append(value: ",\"references\":[").Append(value: references).Append(value: ']');
                }

                jsonl.Append(value: "}\n");

                if (!byFile.TryGetValue(
                    key: parsed.Relative,
                    value: out var lines
                )) {
                    lines = [];
                    byFile[parsed.Relative] = lines;
                }

                lines.Add(item: (startLine, text));
            }
        }

        var total = bucketCounts.Values.Sum();

        Console.Error.WriteLine(value: $"scan[comment-smells]: {total} inline comments classified across {byFile.Count} files (of {corpus.FileCount} scanned).");

        foreach (var (bucket, count) in bucketCounts.OrderByDescending(keySelector: static pair => pair.Value)) {
            Console.Error.WriteLine(value: $"{count,5}  {bucket}");
        }

        Console.Error.WriteLine(value: $"{unresolved,5}  (comments citing a FILE that resolves nowhere — provable staleness)");
        Console.Error.WriteLine(value: $"{symbolAdvisories,5}  (comments citing an UPPER_SNAKE symbol absent from our sources — ADVISORY: an external SDK or hardware constant looks identical)");
        Console.Error.WriteLine(value: $"{essayLines,5}  lines in {essayBlocks} blocks of {EssayLineCount}+ consecutive comment lines");

        return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(
            byFile: byFile,
            maxPerChunk: options.MaxPerChunk
        ));
    }
}
