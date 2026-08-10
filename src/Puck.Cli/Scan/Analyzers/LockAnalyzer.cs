using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// Emits one JSONL record per lock site, each tagged with a `kind`:
//   lock                — a `lock (expr) {...}` statement; text is the lock-object EXPRESSION (the field
//                         that drives lock ordering and the lock-on-this/typeof/public/string smells),
//                         NOT the body.
//   lock-type/semaphore/mutex/rwlock/rwlock-slim/spinlock
//                       — a field/local/using/for/property DECLARATION of an instance primitive (Lock is
//                         System.Threading.Lock, net9+).
//   monitor/interlocked — a Monitor.* / Interlocked.* member call (a static-class call IS the lock site;
//                         there is no instance to anchor on).
//   synchronized-method — a [MethodImpl(MethodImplOptions.Synchronized)] attribute.
// Detection is purely syntactic (no semantic model): a field typed `object` only surfaces through the
// `lock (_gate)` that uses it, and a fully-aliased/using-static primitive may slip through. The axes are
// disjoint, so no node is counted twice (a Lock field and the lock() that uses it are two distinct sites).
internal sealed class LockAnalyzer : ISourceAnalyzer {
    // Static synchronization classes whose member calls are themselves the lock site.
    private static readonly Dictionary<string, string> StaticLockClasses = new(comparer: StringComparer.Ordinal) {
        ["Monitor"] = "monitor",
        ["Interlocked"] = "interlocked",
    };

    // Instance synchronization types, recorded where they are DECLARED.
    private static readonly Dictionary<string, string> InstanceLockTypes = new(comparer: StringComparer.Ordinal) {
        ["Lock"] = "lock-type",
        ["SemaphoreSlim"] = "semaphore",
        ["Mutex"] = "mutex",
        ["ReaderWriterLock"] = "rwlock",
        ["ReaderWriterLockSlim"] = "rwlock-slim",
        ["SpinLock"] = "spinlock",
    };
    private static readonly Regex WhitespaceRun = new(pattern: "\\s+");

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var jsonl = new StringBuilder();
        var perFile = new Dictionary<string, int>();
        var byFile = new Dictionary<string, List<(int Line, string Text)>>();
        var kindCounts = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var parsed in corpus.Files) {
            var relative = parsed.Relative;

            foreach (var node in parsed.Root.DescendantNodes()) {
                if (Classify(node: node) is not { } site) {
                    continue;
                }

                var (kind, text, startLine, endLine) = site;

                jsonl.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: relative)).Append(value: ',')
                    .Append(value: "\"line\":").Append(value: startLine).Append(value: ',')
                    .Append(value: "\"endLine\":").Append(value: endLine).Append(value: ',')
                    .Append(value: "\"kind\":").Append(value: ScanJsonl.JsonString(value: kind)).Append(value: ',')
                    .Append(value: "\"text\":").Append(value: ScanJsonl.JsonString(value: text))
                    .Append(value: "}\n");

                kindCounts[kind] = (kindCounts.GetValueOrDefault(key: kind) + 1);
                perFile[relative] = (perFile.GetValueOrDefault(key: relative) + 1);

                if (!byFile.TryGetValue(key: relative, value: out var sites)) {
                    sites = [];
                    byFile[relative] = sites;
                }

                sites.Add(item: (startLine, text));
            }
        }

        var total = kindCounts.Values.Sum();
        var breakdown = ((total == 0) ? "none" : string.Join(separator: ", ", values: kindCounts.Select(selector: static pair => $"{pair.Value} {pair.Key}")));

        Console.Error.WriteLine(value: $"scan[locks]: {total} lock sites ({breakdown}) across {perFile.Count} files (of {corpus.FileCount} scanned).");

        foreach (var line in ScanJsonl.TopFiles(perFile: perFile)) {
            Console.Error.WriteLine(value: line);
        }

        return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile: byFile, maxPerChunk: options.MaxPerChunk));
    }

    // Pure-syntax classification of one node into a lock site, or null for anything that is not one. The
    // cases are mutually exclusive node shapes, so a node is recorded at most once.
    private static (string Kind, string Text, int StartLine, int EndLine)? Classify(SyntaxNode node) {
        switch (node) {
            // `lock (expr) {...}` — record only the header span and the lock-object expression; the body
            // would bloat the record and the target is the signal.
            case LockStatementSyntax lockStatement: {
                    var (start, end) = ScanJsonl.LineRange(start: lockStatement.LockKeyword.GetLocation(), end: lockStatement.CloseParenToken.GetLocation());

                    return ("lock", Condense(text: lockStatement.Expression.ToString()), start, end);
                }

            // Monitor.Enter(...) / Interlocked.Increment(...), including a qualified receiver like
            // System.Threading.Monitor.Enter (rightmost name is matched).
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } invocation
                when ((ReceiverTypeName(expression: memberAccess.Expression) is { } receiver)
                    && StaticLockClasses.TryGetValue(key: receiver, value: out var staticKind)): {
                    var (start, end) = ScanJsonl.LineRange(location: invocation.GetLocation());

                    return (staticKind, Condense(text: invocation.ToString()), start, end);
                }

            // Field / local / using / for declaration of an instance primitive.
            case VariableDeclarationSyntax variableDeclaration
                when InstanceLockTypes.TryGetValue(key: SimpleTypeName(type: variableDeclaration.Type), value: out var declaredKind): {
                    var (start, end) = ScanJsonl.LineRange(location: variableDeclaration.GetLocation());

                    return (declaredKind, Condense(text: variableDeclaration.ToString()), start, end);
                }

            // Property of an instance primitive (rare; PropertyDeclaration is not a VariableDeclaration,
            // so it needs its own case).
            case PropertyDeclarationSyntax propertyDeclaration
                when InstanceLockTypes.TryGetValue(key: SimpleTypeName(type: propertyDeclaration.Type), value: out var propertyKind): {
                    var (start, end) = ScanJsonl.LineRange(location: propertyDeclaration.GetLocation());

                    return (propertyKind, Condense(text: $"{propertyDeclaration.Type} {propertyDeclaration.Identifier.ValueText}"), start, end);
                }

            // [MethodImpl(MethodImplOptions.Synchronized)] — a whole-method monitor lock.
            case AttributeSyntax attribute
                when ((SimpleTypeName(type: attribute.Name) is "MethodImpl" or "MethodImplAttribute")
                    && attribute.ToString().Contains(value: "Synchronized", comparisonType: StringComparison.Ordinal)): {
                    var (start, end) = ScanJsonl.LineRange(location: attribute.GetLocation());

                    return ("synchronized-method", Condense(text: attribute.ToString()), start, end);
                }

            default:
                return null;
        }
    }

    // Rightmost identifier of an invocation receiver: Monitor -> Monitor, System.Threading.Monitor ->
    // Monitor. Anything else (a call result, an indexer) yields null and is not a static lock class.
    private static string? ReceiverTypeName(ExpressionSyntax expression) => expression switch {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    // Rightmost identifier of a possibly-qualified/nullable type name: System.Threading.Lock -> Lock,
    // SemaphoreSlim? -> SemaphoreSlim. Generics and arrays keep their suffix and simply fail to match the
    // lock-type set, as intended.
    private static string SimpleTypeName(TypeSyntax type) {
        var name = type.ToString();
        var lastDot = name.LastIndexOf(value: '.');

        if (lastDot >= 0) {
            name = name[(lastDot + 1)..];
        }

        return name.TrimEnd('?', ' ');
    }

    // Collapses a node's source text to a trimmed single line and caps the length, so every record is a
    // readable one-liner rather than a wrapped, indented fragment.
    private static string Condense(string text) {
        var condensed = WhitespaceRun.Replace(input: text.Trim(), replacement: " ");

        return ((condensed.Length <= 200) ? condensed : string.Concat(str0: condensed.AsSpan(start: 0, length: 197), str1: "..."));
    }
}
