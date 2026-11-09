using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// Lists the integer fields of the simulation projects that are incremented to mint an identity and that no
// checkpoint, snapshot or codec member reads: a process-local counter naming simulation state, which a restored
// or replayed run mints differently from the run it reproduces. Each record is one field, at its declaration, for a
// person to judge:
//   mint — an increment whose value is handed on (`var id = m_next++`, `return ++m_serial`, `Id: m_next++`, an
//          `Interlocked.Increment(ref x)` result), the counter handing out the identity it names; a field named as a
//          tally, depth or cursor (`m_count`, `m_depth`) is left out, as is an increment that indexes an array fill.
//   bump — a statement-level increment of a field whose name says identity (id, generation, serial, sequence,
//          next, handle, ticket, token, nonce); a tally, and a version or epoch a cache compares, are not listed.
// A field counts as captured when a member of the same type (by name, across partial files) whose name says it
// persists (Capture, Checkpoint, Snapshot, Encode, Serialize, Save, Persist, Export, Import, Restore, Decode, Write)
// mentions it, a file of that type named for persistence (Checkpoint, Codec, Persistence, Snapshot) does, or the type
// writes it on another instance (`restored.m_next = carried`), the shape of a resuming factory.
// Detection is syntactic: it matches names, not symbols, so a field captured through a helper it is passed to, or
// one whose identity never reaches simulation state, is a false positive to triage, and a counter a codec writes
// under another name is a false negative.
internal sealed class IdentityCounterAnalyzer : ISourceAnalyzer {
    private static readonly HashSet<string> IntegerTypes = new(comparer: StringComparer.Ordinal) {
        "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint",
        "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
    };
    // A version, revision or epoch a cache compares in-process names no identity, so only these names list a bump.
    private static readonly Regex IdentityName = new(pattern: "(?i)(id$|ids$|generation|serial|sequence|^(m_)?next|handle|ticket|token|nonce|mint)");
    private static readonly Regex PersistFile = new(pattern: "(Checkpoint|Codec|Persistence|Snapshot)");
    // A field named as a tally, a depth or a cursor counts or walks; it names nothing, however its value is used.
    private static readonly Regex TallyName = new(pattern: "(?i)(count|counts|depth|index|cursor|pending|length|size|total|hits|misses)$");
    private static readonly Regex PersistMember = new(pattern: "(Capture|Checkpoint|Snapshot|Encode|Serialize|Save|Persist|Export|Import|Restore|Decode|Write)");

    // Whether a file belongs to a project whose state is simulation state: the state store and rules, the world
    // server and the world schema it resolves sessions with.
    private static bool IsSimulation(string relative) =>
        relative.Split('/', '\\').Any(predicate: static segment => (
            segment.StartsWith(value: "Puck.State", comparisonType: StringComparison.Ordinal) ||
            (segment == "Puck.World.Server") ||
            (segment == "Puck.World.Schema")
        ));
    private static string TypeKey(SyntaxNode node) => ScanSyntax.TypePath(node: node);
    private static string MemberName(SyntaxNode node) => node.Ancestors().FirstOrDefault(predicate: static ancestor => ancestor is MemberDeclarationSyntax and not BaseTypeDeclarationSyntax) switch {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        ConstructorDeclarationSyntax => ".ctor",
        _ => "",
    };
    // The field an increment's operand names on this instance: `m_next`, `this.m_next`, or `Type.s_next`.
    private static string? OperandName(ExpressionSyntax operand) => operand switch {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or IdentifierNameSyntax, Name: var name } => name.Identifier.ValueText,
        _ => null,
    };
    // The field an increment names and whether its value is used, or null when the node increments nothing.
    private static (string Name, bool Used)? Increment(SyntaxNode node) {
        switch (node) {
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression } postfix when OperandName(operand: postfix.Operand) is { } name:
                return (name, Used(expression: postfix));
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression } prefix when OperandName(operand: prefix.Operand) is { } name:
                return (name, Used(expression: prefix));
            case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression, Right: LiteralExpressionSyntax { Token.ValueText: "1" } } assignment when OperandName(operand: assignment.Left) is { } name:
                return (name, Used(expression: assignment));
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Interlocked" }, Name.Identifier.ValueText: "Increment" or "Add" } } invocation
                when (invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } target) && (OperandName(operand: target) is { } name):
                return (name, Used(expression: invocation));
            default:
                return null;
        }
    }
    // Whether the incremented value is handed on as a value: initialized into a local, returned, assigned, or passed
    // (an `Id: m_next++` argument included). An index into an array being filled (`items[m_count++] = x`), a bound
    // check (`if (++m_depth > Max)`) and a statement-level increment hand out no identity.
    private static bool Used(ExpressionSyntax expression) {
        var holder = ((expression.Parent is ParenthesizedExpressionSyntax parenthesized) ? parenthesized.Parent : expression.Parent);

        return holder switch {
            EqualsValueClauseSyntax or ReturnStatementSyntax or ArrowExpressionClauseSyntax => true,
            AssignmentExpressionSyntax assignment => ReferenceEquals(objA: assignment.Right, objB: expression),
            ArgumentSyntax { Parent: ArgumentListSyntax } => true,
            _ => false,
        };
    }

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var fields = new Dictionary<(string Type, string Field), (string File, int Line, string Declared)>();
        var increments = new Dictionary<(string Type, string Field), (int Count, bool Minted, string Text)>();
        var captured = new HashSet<(string Type, string Field)>();
        var simulation = corpus.Files.Where(predicate: static file => IsSimulation(relative: file.Relative)).ToList();

        foreach (var parsed in simulation) {
            var persistFile = PersistFile.IsMatch(input: Path.GetFileName(path: parsed.Relative));

            foreach (var node in parsed.Root.DescendantNodes()) {
                switch (node) {
                    case FieldDeclarationSyntax field when !field.Modifiers.Any(kind: SyntaxKind.ConstKeyword) && IntegerTypes.Contains(item: field.Declaration.Type.ToString().TrimEnd('?')):
                        foreach (var variable in field.Declaration.Variables) {
                            fields[(TypeKey(node: field), variable.Identifier.ValueText)] = (parsed.Relative, ScanJsonl.LineRange(location: variable.GetLocation()).Start, $"{field.Declaration.Type} {variable.Identifier.ValueText}");
                        }

                        break;
                    // A restore writes another instance's field from what was carried (`table.m_next = checkpoint.Next`,
                    // `producer.m_sequence = sequence` in a resuming factory), whatever the member is called.
                    case IdentifierNameSyntax identifier when identifier.Parent is MemberAccessExpressionSyntax { Expression: not ThisExpressionSyntax, Parent: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } restore } access && ReferenceEquals(objA: access.Name, objB: identifier) && ReferenceEquals(objA: restore.Left, objB: access):
                        _ = captured.Add(item: (TypeKey(node: identifier), identifier.Identifier.ValueText));
                        break;
                    case IdentifierNameSyntax identifier when persistFile || PersistMember.IsMatch(input: MemberName(node: identifier)):
                        if (Increment(node: identifier.Parent!) is null) {
                            _ = captured.Add(item: (TypeKey(node: identifier), identifier.Identifier.ValueText));
                        }

                        break;
                }
                if (Increment(node: node) is { } increment) {
                    var key = (TypeKey(node: node), increment.Name);
                    var (count, minted, text) = increments.GetValueOrDefault(key: key);

                    increments[key] = ((count + 1), (minted || increment.Used), ((text is null) ? node.ToString() : text));
                }
            }
        }

        var sites = new ScanSites();
        var capturedCounters = 0;

        foreach (var (key, increment) in increments.OrderBy(keySelector: static pair => pair.Key.Type, comparer: StringComparer.Ordinal).ThenBy(keySelector: static pair => pair.Key.Field, comparer: StringComparer.Ordinal)) {
            if (
                !fields.TryGetValue(key: key, value: out var declaration) ||
                TallyName.IsMatch(input: key.Field) ||
                (!increment.Minted && !IdentityName.IsMatch(input: key.Field))
            ) {
                continue;
            }
            if (captured.Contains(item: key)) {
                capturedCounters++;
                continue;
            }

            sites.Add(
                endLine: declaration.Line,
                file: declaration.File,
                kind: (increment.Minted ? "mint" : "bump"),
                line: declaration.Line,
                text: $"{key.Type}: {declaration.Declared}; {increment.Count} increment(s), e.g. `{increment.Text}`; no checkpoint member of {key.Type} reads it"
            );
        }

        return sites.Finish(
            digest: $"scan[identity-counters]: {sites.Count} uncaptured identity counters ({sites.Breakdown()}) across {sites.FileCount} files; {capturedCounters} captured by a checkpoint member (of {simulation.Count} simulation files of {corpus.FileCount} scanned).",
            maxPerChunk: options.MaxPerChunk
        );
    }
}
