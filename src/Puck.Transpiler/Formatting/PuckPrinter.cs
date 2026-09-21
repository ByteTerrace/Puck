using System.Globalization;
using System.Text;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Formatting;

/// <summary>How a printed source is laid out.</summary>
public sealed record PuckPrintOptions {
    /// <summary>The options a source prints with when a caller names none.</summary>
    public static readonly PuckPrintOptions Default = new();

    /// <summary>Gets the number of spaces one indentation level costs.</summary>
    public int TabSize { get; init; } = 2;
    /// <summary>Gets a value indicating whether one level indents with spaces rather than with a single tab.</summary>
    public bool InsertSpaces { get; init; } = true;
}
/// <summary>Prints a syntax tree back as <c>.puck</c> source.</summary>
/// <remarks>Printing is the only formatter: <c>puck fmt</c>, the language server's formatting request, and a
/// decompiler's output all parse and then print, so a formatted source can never mean something its author did not
/// write. The layout a node prints in is the printer's own, except for the comments, blank lines, and array or
/// object line breaks the parse carried in on <see cref="SyntaxTrivia"/>.</remarks>
public static class PuckPrinter {
    /// <summary>Returns <paramref name="document"/> printed as source text.</summary>
    /// <param name="document">The tree to print.</param>
    /// <param name="options">The layout to print with, or <see langword="null"/> for the default.</param>
    /// <returns>The printed source, ending in a single newline.</returns>
    public static string Print(DocumentNode document, PuckPrintOptions? options = null) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var writer = new Writer(options: (options ?? PuckPrintOptions.Default));

        writer.Document(document: document);

        return writer.ToString();
    }
    /// <summary>Returns <paramref name="document"/> printed as source text, or <see langword="null"/> when the tree
    /// carries a statement the parse failed on and the printer would silently drop.</summary>
    /// <param name="document">The tree to print.</param>
    /// <param name="options">The layout to print with, or <see langword="null"/> for the default.</param>
    /// <returns>The printed source, or <see langword="null"/>.</returns>
    public static string? TryPrint(DocumentNode document, PuckPrintOptions? options = null) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var writer = new Writer(options: (options ?? PuckPrintOptions.Default));

        writer.Document(document: document);

        return (writer.Incomplete
            ? null
            : writer.ToString()
        );
    }
    /// <summary>Returns <paramref name="predicate"/> printed as the gate expression an author would write.</summary>
    /// <param name="predicate">The gate to print.</param>
    /// <param name="options">The layout to print with, or <see langword="null"/> for the default.</param>
    /// <returns>The printed gate, on one line and with no trailing newline.</returns>
    /// <remarks>Printed from the tree rather than copied from source, so the text is the same before and after a
    /// format — which is what lets a lowering carry a gate's own spelling into a document.</remarks>
    public static string PrintGate(PredicateNode predicate, PuckPrintOptions? options = null) {
        ArgumentNullException.ThrowIfNull(argument: predicate);

        var writer = new Writer(options: (options ?? PuckPrintOptions.Default));

        writer.Gate(predicate: predicate);

        return writer.ToString();
    }
    /// <summary>Returns <paramref name="expression"/> printed as an author would write it.</summary>
    /// <param name="expression">The expression to print.</param>
    /// <param name="options">The layout to print with, or <see langword="null"/> for the default.</param>
    /// <returns>The printed expression, with no trailing newline.</returns>
    public static string PrintExpression(ExpressionNode expression, PuckPrintOptions? options = null) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        var writer = new Writer(options: (options ?? PuckPrintOptions.Default));

        writer.Value(expression: expression);

        return writer.ToString();
    }
    /// <summary>Parses <paramref name="source"/> and prints it back.</summary>
    /// <param name="source">The source text to format.</param>
    /// <param name="options">The layout to print with, or <see langword="null"/> for the default.</param>
    /// <param name="vocabulary">The vocabulary whose embedded languages the parse must recognize, or
    /// <see langword="null"/>.</param>
    /// <returns>The formatted source, or a null value with the diagnostics that stopped the parse.</returns>
    public static CompilationResult<string> Format(string source, PuckPrintOptions? options = null, IDocumentVocabulary? vocabulary = null) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (source.AsSpan().IsWhiteSpace()) {
            return new CompilationResult<string>(Diagnostics: new DiagnosticBag(), Value: string.Empty);
        }

        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: vocabulary
        );

        // What stops a format is a tree the parse could not build, never a diagnostic about what the tree MEANS:
        // an unfinished rule still formats while its author is writing it.
        return new CompilationResult<string>(
            Diagnostics: parsed.Diagnostics,
            Value: ((parsed.Value is null)
                ? null
                : TryPrint(
                    document: parsed.Value,
                    options: options
                ))
        );
    }

    // A PROPERTY name a statement reader would take for the keyword it spells, because nothing in `name:` defeats
    // the match. A keyword whose own reader first demands a name (`table x :`, `enum X {`, `rules x {`) is not one
    // of these: `table: 1` already reads as a property, so quoting it buys nothing.
    private static readonly HashSet<string> PropertyKeywords = new(comparer: StringComparer.Ordinal) {
        "break", "countdown", "deal", "decision", "draw", "export", "for", "if", "import", "interrupt", "let",
        "local", "onNoChoice", "option", "push", "remove", "repeat", "request", "rule", "schedule", "score",
        "shuffle", "template", "transaction", "transform", "watchMemory", "when",
    };
    // `schema:` and `basis:` are the document's own headers, and only at the top level; inside any block they are
    // ordinary properties.
    private static readonly HashSet<string> DocumentHeaders = new(comparer: StringComparer.Ordinal) { "basis", "schema" };

    private static bool IsBareName(string name) {
        if (name.Length == 0) {
            return false;
        }
        if (
            !char.IsLetter(c: name[0]) &&
            (name[0] != '_') &&
            (name[0] != '$')
        ) {
            return false;
        }

        foreach (var character in name) {
            if (
                !char.IsLetterOrDigit(c: character) &&
                (character != '_') &&
                (character != '$')
            ) {
                return false;
            }
        }

        return true;
    }
    // The reader's own grammar, so what the printer writes is what the reader reads back.
    private static string Quote(string text) => PuckStrings.Write(value: text);
    // A name prints bare when the reader that takes it back would read the same string; otherwise it is quoted.
    private static string Name(string name) => (IsBareName(name: name)
        ? name
        : Quote(text: name)
    );
    private static string ExportName(string name) => string.Join(separator: '.', values: name.Split('.').Select(selector: Name));
    // A property name is quoted only where printing it bare would open a different production.
    private static string PropertyName(string name, int level) => ((IsBareName(name: name) &&
        !PropertyKeywords.Contains(item: name) &&
        ((level > 0) || !DocumentHeaders.Contains(item: name)))
        ? name
        : Quote(text: name)
    );

    private sealed class Writer {
        private readonly StringBuilder m_builder = new();
        private readonly PuckPrintOptions m_options;

        public Writer(PuckPrintOptions options) {
            m_options = options;
        }

        /// <summary>Whether the tree carried a statement the parse failed on, which has no spelling to print.</summary>
        public bool Incomplete { get; private set; }

        public override string ToString() => m_builder.ToString();

        private void Indent(int level) {
            if (m_options.InsertSpaces) {
                m_builder.Append(
                    repeatCount: (level * m_options.TabSize),
                    value: ' '
                );
            } else {
                m_builder.Append(
                    repeatCount: level,
                    value: '\t'
                );
            }
        }
        private void EndLine() => m_builder.Append(value: '\n');
        private void Trailing(SyntaxTrivia trivia) {
            if (trivia.Trailing is not null) {
                m_builder.Append(value: ' ').Append(value: trivia.Trailing);
            }
            EndLine();
        }
        private void Pieces(IReadOnlyList<TriviaPiece> pieces, int level) {
            foreach (var piece in pieces) {
                if (piece.Kind == TriviaKind.BlankLine) {
                    EndLine();

                    continue;
                }
                Indent(level: level);
                m_builder.Append(value: piece.Text);
                EndLine();
            }
        }
        // A construct's header can carry a line comment, which would swallow the brace if it stayed there; it
        // prints above the statement, in written order, along with the statement's own leading trivia.
        private void Leading(SyntaxTrivia trivia, int level) {
            Pieces(
                level: level,
                pieces: trivia.Leading
            );
            Pieces(
                level: level,
                pieces: [.. trivia.Header.Where(predicate: IsLineComment)]
            );
        }
        private void Inner(SyntaxTrivia trivia, int level) {
            Pieces(
                level: level,
                pieces: trivia.Inner
            );
        }
        private void Alternate(SyntaxTrivia trivia, int level) {
            Pieces(
                level: level,
                pieces: trivia.Alternate
            );
        }
        // A block comment in a header stands where it was written: right after the word that opened the construct.
        private void InlineHeader(SyntaxTrivia trivia) {
            foreach (var piece in trivia.Header) {
                if (!IsLineComment(piece: piece)) {
                    m_builder.Append(value: ' ').Append(value: piece.Text);
                }
            }
        }
        private static bool IsLineComment(TriviaPiece piece) => (
            (piece.Kind == TriviaKind.Comment) &&
            piece.Text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "//"
            )
        );
        // One of a test's three blocks: the keyword, its own body, and the trivia standing before its closing brace.
        private void TestBlock(string keyword, int level, SyntaxTrivia trivia, Action write) {
            Leading(
                level: level,
                trivia: trivia
            );
            Indent(level: level);
            m_builder.Append(value: keyword).Append(value: " {");
            if (trivia.Opening is { } opening) { m_builder.Append(value: ' ').Append(value: opening); }
            EndLine();
            write();
            Inner(
                level: (level + 1),
                trivia: trivia
            );
            Indent(level: level);
            m_builder.Append(value: '}');
            Trailing(trivia: trivia);
        }

        public void Gate(PredicateNode predicate) => Predicate(
            parenthesize: false,
            predicate: predicate
        );
        public void Document(DocumentNode document) {
            Leading(
                level: 0,
                trivia: document.Trivia
            );
            if (document.Schema is not null) {
                m_builder.Append(value: "schema: ").Append(value: Quote(text: document.Schema));
                EndLine();
            }
            if (document.Basis is not null) {
                m_builder.Append(value: "basis: ").Append(value: Quote(text: document.Basis));
                EndLine();
            }
            Statements(
                level: 0,
                statements: document.Statements
            );
            Inner(
                level: 0,
                trivia: document.Trivia
            );
            while (
                (m_builder.Length > 1) &&
                (m_builder[^1] == '\n') &&
                (m_builder[^2] == '\n')
            ) {
                m_builder.Length--;
            }
            if (
                (m_builder.Length > 0) &&
                (m_builder[^1] != '\n')
            ) {
                EndLine();
            }
        }

        private void Statements(IReadOnlyList<StatementNode> statements, int level) {
            foreach (var statement in statements) {
                Leading(
                    level: level,
                    trivia: statement.Trivia
                );
                Statement(
                    level: level,
                    statement: statement
                );
            }
        }
        // The body of a braced construct, opened on the line the caller has already begun.
        private void Body(IReadOnlyList<StatementNode> statements, IReadOnlyList<TriviaPiece> closing, int level, string? opening = null) {
            m_builder.Append(value: " {");
            if (
                (statements.Count == 0) &&
                (closing.Count == 0) &&
                (opening is null)
            ) {
                m_builder.Append(value: '}');

                return;
            }
            if (opening is not null) { m_builder.Append(value: ' ').Append(value: opening); }
            EndLine();
            Statements(
                level: (level + 1),
                statements: statements
            );
            Pieces(
                level: (level + 1),
                pieces: closing
            );
            Indent(level: level);
            m_builder.Append(value: '}');
        }
        private void Statement(StatementNode statement, int level) {
            // A failed statement has no spelling to print, so printing the tree would drop the text it stands for.
            if (statement is ErrorStatementNode) {
                Incomplete = true;

                return;
            }
            Indent(level: level);
            switch (statement) {
                case WorldDeclarationNode world: {
                        m_builder.Append(value: "world ");
                        Expression(expression: world.Name, level: level);
                        m_builder.Append(value: " = ");
                        Expression(expression: world.Module, level: level);

                        break;
                    }
                case WorldLinkNode link: {
                        m_builder.Append(value: link.Kind).Append(value: ' ');
                        Expression(expression: link.Left, level: level);
                        m_builder.Append(value: ", ");
                        Expression(expression: link.Right, level: level);
                        if (link.Kind == "border") {
                            Body(closing: link.Trivia.Inner, level: level, opening: link.Trivia.Opening, statements: link.Properties);
                        }

                        break;
                    }
                case ImportNode import: {
                        m_builder.Append(value: "import ").Append(value: Quote(text: import.Path));
                        if (import.Alias is not null) { m_builder.Append(value: " as ").Append(value: import.Alias); }

                        break;
                    }
                case ExportNode export: {
                        m_builder.Append(value: "export");
                        if (export.FacetExplicit) { m_builder.Append(value: ' ').Append(value: export.Facet); }
                        for (var index = 0; (index < export.Names.Count); index++) {
                            m_builder.Append(value: ((index == 0)
                                ? " "
                                : ", "
                            )).Append(value: ExportName(name: export.Names[index]));
                        }

                        break;
                    }
                case LetNode declaration: {
                        m_builder.Append(value: "let ").Append(value: declaration.Name).Append(value: " = ");
                        Expression(
                            expression: declaration.Value,
                            level: level
                        );

                        break;
                    }
                case TemplateNode template: {
                        m_builder.Append(value: (template.IsModule ? "module" : "template"));
                        InlineHeader(trivia: template.Trivia);
                        m_builder.Append(value: ' ').Append(value: template.Name).Append(value: '(');
                        for (var index = 0; (index < template.Parameters.Count); index++) {
                            if (index > 0) { m_builder.Append(value: ", "); }
                            m_builder.Append(value: template.Parameters[index].Name);
                            if (template.Parameters[index].Kind is { } kind) {
                                m_builder.Append(value: ": ").Append(value: kind);
                                if (template.Parameters[index].RequiredExports.Count > 0) {
                                    m_builder.Append(value: " exporting ").AppendJoin(separator: ", ", values: template.Parameters[index].RequiredExports);
                                }
                            }
                            if (template.Parameters[index].DefaultValue is { } fallback) {
                                m_builder.Append(value: " = ");
                                Expression(
                                    expression: fallback,
                                    level: level
                                );
                            }
                        }
                        m_builder.Append(value: ')');
                        Body(
                            closing: template.Body.Trivia.Inner,
                            level: level,
                            opening: template.Body.Trivia.Opening,
                            statements: template.Body.Statements
                        );

                        break;
                    }
                case ForStatementNode loop: {
                        m_builder.Append(value: "for");
                        InlineHeader(trivia: loop.Trivia);
                        m_builder.Append(value: ' ');
                        if (loop.Index is null) {
                            m_builder.Append(value: loop.Item);
                        } else {
                            m_builder.Append(value: '(').Append(value: loop.Item).Append(value: ", ").Append(value: loop.Index).Append(value: ')');
                        }
                        m_builder.Append(value: " in ");
                        Expression(
                            expression: loop.Sequence,
                            level: level
                        );
                        Body(
                            closing: loop.Trivia.Inner,
                            level: level,
                            opening: loop.Trivia.Opening,
                            statements: loop.Body
                        );

                        break;
                    }
                case EmbeddedBlockNode embedded: {
                        m_builder.Append(value: embedded.Language).Append(value: " {").Append(value: embedded.Body).Append(value: '}');

                        break;
                    }
                case BlockNode block: {
                        m_builder.Append(value: block.Identifier);
                        InlineHeader(trivia: block.Trivia);
                        if (block.Target is not null) { m_builder.Append(value: ' ').Append(value: Name(name: block.Target)); }
                        if (block.Name is not null) {
                            m_builder.Append(value: ' ').Append(value: (block.NameQuoted
                                ? Quote(text: block.Name)
                                : Name(name: block.Name)
                            ));
                        } else if (block.NameExpression is not null) {
                            m_builder.Append(value: ' ');
                            Expression(
                                expression: block.NameExpression,
                                level: level
                            );
                        }
                        Body(
                            closing: block.Trivia.Inner,
                            level: level,
                            opening: block.Trivia.Opening,
                            statements: block.Statements
                        );

                        break;
                    }
                case PropertyNode property: {
                        Property(
                            level: level,
                            property: property
                        );

                        break;
                    }
                case FlagStatementNode flag: {
                        m_builder.Append(value: flag.Name);

                        break;
                    }
                case ExpressionStatementNode expression: {
                        if (expression.IsUse && (expression.Expression is CallExpressionNode use)) {
                            m_builder.Append(value: "use ").Append(value: use.Name);
                            if (expression.UseAlias is { } alias) { m_builder.Append(value: " as ").Append(value: alias); }
                            m_builder.Append(value: '(');
                            Arguments(arguments: use.Arguments, level: level, multiLine: use.Trivia.MultiLine);
                            if (use.Trivia.MultiLine) { EndLine(); Indent(level: level); }
                            m_builder.Append(value: ')');
                        } else {
                            Expression(expression: expression.Expression, level: level);
                        }

                        break;
                    }
                case AddonRequestNode request: {
                        m_builder.Append(value: "request ").Append(value: request.Capability).Append(value: ' ').Append(value: Quote(text: request.Subject));

                        break;
                    }
                case AddonMemoryWatchNode watch: {
                        m_builder
                            .Append(value: "watchMemory screen: ").Append(value: watch.Screen.ToString(provider: CultureInfo.InvariantCulture))
                            .Append(value: ", address: ").Append(value: watch.Address.ToString(provider: CultureInfo.InvariantCulture))
                            .Append(value: ", length: ").Append(value: watch.WatchLength.ToString(provider: CultureInfo.InvariantCulture));

                        break;
                    }
                case WhenStatementNode gate: {
                        m_builder.Append(value: "when ");
                        Predicate(
                            parenthesize: false,
                            predicate: gate.Predicate
                        );

                        break;
                    }
                case InterruptStatementNode interrupt: {
                        m_builder.Append(value: "interrupt ");
                        Predicate(
                            parenthesize: false,
                            predicate: interrupt.Predicate
                        );

                        break;
                    }
                case ScoreStatementNode score: {
                        m_builder.Append(value: "score: ").Append(value: score.Expression.Text);

                        break;
                    }
                case LocalStatementNode local: {
                        m_builder.Append(value: "local ").Append(value: local.Name).Append(value: " = ").Append(value: local.Expression.Text);

                        break;
                    }
                case RuleBlockNode rule: {
                        m_builder.Append(value: "rule");
                        InlineHeader(trivia: rule.Trivia);
                        m_builder.Append(value: ' ');
                        if (rule.NameExpression is not null) {
                            Expression(
                                expression: rule.NameExpression,
                                level: level
                            );
                        } else {
                            m_builder.Append(value: Quote(text: rule.Name));
                        }
                        if ((rule.PoolForEach is { } pool) && (rule.PoolBinding is { } binding)) {
                            m_builder.Append(value: " for each ").Append(value: binding).Append(value: " in ").Append(value: pool);
                        }
                        Body(
                            closing: rule.Trivia.Inner,
                            level: level,
                            opening: rule.Trivia.Opening,
                            statements: rule.Statements
                        );

                        break;
                    }
                case RuleScopeNode scope: {
                        m_builder.Append(value: "rules");
                        InlineHeader(trivia: scope.Trivia);
                        m_builder.Append(value: ' ').Append(value: Name(name: scope.Name));
                        if (scope.HeaderWhen is not null) {
                            m_builder.Append(value: " when ");
                            Predicate(
                                parenthesize: false,
                                predicate: scope.HeaderWhen.Predicate
                            );
                        }
                        Body(
                            closing: scope.Trivia.Inner,
                            level: level,
                            opening: scope.Trivia.Opening,
                            statements: scope.Statements
                        );

                        break;
                    }
                case StabilizeGroupNode group: {
                        m_builder.Append(value: "stabilize");
                        InlineHeader(trivia: group.Trivia);
                        m_builder.Append(value: ' ').Append(value: Name(name: group.Name));
                        if (group.Undo is { } undo) {
                            m_builder.Append(value: " undo(");
                            Expression(expression: undo, level: level);
                            m_builder.Append(value: ')');
                        }
                        if (group.MaxPasses is { } passes) {
                            m_builder.Append(value: " maxPasses(");
                            Expression(
                                expression: passes,
                                level: level
                            );
                            m_builder.Append(value: ')');
                        }
                        if (group.UntilCondition is { } until) {
                            m_builder.Append(value: " until ");
                            Predicate(
                                parenthesize: false,
                                predicate: until
                            );
                        }
                        Body(
                            closing: group.Trivia.Inner,
                            level: level,
                            opening: group.Trivia.Opening,
                            statements: group.Statements
                        );

                        break;
                    }
                case WorkflowNode workflow: {
                        m_builder.Append(value: "workflow");
                        InlineHeader(trivia: workflow.Trivia);
                        m_builder.Append(value: ' ').Append(value: Name(name: workflow.Name));
                        if (workflow.Undo is { } undo) {
                            m_builder.Append(value: " undo(");
                            Expression(expression: undo, level: level);
                            m_builder.Append(value: ')');
                        }
                        Body(
                            closing: workflow.Trivia.Inner,
                            level: level,
                            opening: workflow.Trivia.Opening,
                            statements: workflow.Steps
                        );

                        break;
                    }
                case WorkflowStepNode step: {
                        switch (step.Kind) {
                            case WorkflowStepKind.Repeat: {
                                    m_builder.Append(value: "repeatStep ").Append(value: Name(name: step.Name));
                                    if (step.UntilCondition is { } until) {
                                        m_builder.Append(value: " until ");
                                        Predicate(
                                            parenthesize: false,
                                            predicate: until
                                        );
                                    }

                                    break;
                                }
                            case WorkflowStepKind.ForEach: {
                                    m_builder.Append(value: "forEachStep ").Append(value: (step.ForEachVariable ?? step.Name)).Append(value: " in ");
                                    if (step.ForEachCollection is { } collection) {
                                        Expression(
                                            expression: collection,
                                            level: level
                                        );
                                    }

                                    break;
                                }
                            default: {
                                    m_builder.Append(value: "step ").Append(value: Name(name: step.Name));
                                    if (step.Skip) { m_builder.Append(value: " skip"); }

                                    break;
                                }
                        }
                        Body(
                            closing: step.Trivia.Inner,
                            level: level,
                            opening: step.Trivia.Opening,
                            statements: step.Statements
                        );

                        break;
                    }
                case DecisionBlockNode decision: {
                        m_builder.Append(value: "decision");
                        InlineHeader(trivia: decision.Trivia);
                        Body(
                            closing: decision.Trivia.Inner,
                            level: level,
                            opening: decision.Trivia.Opening,
                            statements: decision.Statements
                        );

                        break;
                    }
                case OptionBlockNode option: {
                        m_builder.Append(value: "option");
                        InlineHeader(trivia: option.Trivia);
                        m_builder.Append(value: ' ').Append(value: Quote(text: option.Name));
                        Body(
                            closing: option.Trivia.Inner,
                            level: level,
                            opening: option.Trivia.Opening,
                            statements: option.Statements
                        );

                        break;
                    }
                case OnNoChoiceBlockNode fallback: {
                        m_builder.Append(value: "onNoChoice");
                        InlineHeader(trivia: fallback.Trivia);
                        Body(
                            closing: fallback.Trivia.Inner,
                            level: level,
                            opening: fallback.Trivia.Opening,
                            statements: fallback.Effects
                        );

                        break;
                    }
                case EnumDeclarationNode declaration: {
                        m_builder.Append(value: "enum");
                        InlineHeader(trivia: declaration.Trivia);
                        m_builder.Append(value: ' ').Append(value: declaration.Name).Append(value: " {");
                        if (
                            (declaration.Members.Count == 0) &&
                            (declaration.Trivia.Inner.Count == 0)
                        ) {
                            m_builder.Append(value: '}');

                            break;
                        }
                        EndLine();
                        foreach (var member in declaration.Members) {
                            Leading(
                                level: (level + 1),
                                trivia: member.Trivia
                            );
                            Indent(level: (level + 1));
                            m_builder.Append(value: member.Name);
                            Trailing(trivia: member.Trivia);
                        }
                        Inner(
                            level: (level + 1),
                            trivia: declaration.Trivia
                        );
                        Indent(level: level);
                        m_builder.Append(value: '}');

                        break;
                    }
                case RecordDeclarationNode declaration: {
                        m_builder.Append(value: "record");
                        InlineHeader(trivia: declaration.Trivia);
                        m_builder.Append(value: ' ').Append(value: declaration.Name).Append(value: " {");
                        if (
                            (declaration.Fields.Count == 0) &&
                            (declaration.Trivia.Inner.Count == 0)
                        ) {
                            m_builder.Append(value: '}');

                            break;
                        }
                        EndLine();
                        foreach (var field in declaration.Fields) {
                            Leading(
                                level: (level + 1),
                                trivia: field.Trivia
                            );
                            Indent(level: (level + 1));
                            m_builder.Append(value: field.Name).Append(value: ": ").Append(value: field.TypeName);
                            Modifiers(level: (level + 1), modifiers: field.Modifiers);
                            if (field.Default is { } defaultValue) {
                                m_builder.Append(value: " = ");
                                Expression(expression: defaultValue, level: (level + 1));
                            }
                            Trailing(trivia: field.Trivia);
                        }
                        Inner(
                            level: (level + 1),
                            trivia: declaration.Trivia
                        );
                        Indent(level: level);
                        m_builder.Append(value: '}');

                        break;
                    }
                case StatePoolDeclarationNode pool: {
                        m_builder.Append(value: "pool");
                        InlineHeader(trivia: pool.Trivia);
                        m_builder.Append(value: ' ').Append(value: pool.Name).Append(value: " of ").Append(value: pool.RecordName);
                        if (pool.Capacity is { } capacity) {
                            m_builder.Append(value: " capacity(");
                            Expression(expression: capacity, level: level);
                            m_builder.Append(value: ')');
                        }
                        if (pool.Initializer is { } initializer) {
                            m_builder.Append(value: " = ");
                            Expression(expression: initializer, level: level);
                        }

                        break;
                    }
                case StatePairPoolDeclarationNode pool: {
                        m_builder.Append(value: "pairPool ").Append(value: pool.Name).Append(value: " record ").Append(value: pool.RecordName).Append(value: " left ").Append(value: pool.LeftPool).Append(value: " right ").Append(value: pool.RightPool).Append(value: " maxLive ");
                        Expression(expression: pool.MaxLive, level: level);
                        m_builder.Append(value: " directed ").Append(value: (pool.Directed ? "true" : "false")).Append(value: " allowSelf ").Append(value: (pool.AllowSelf ? "true" : "false"));
                        break;
                    }
                case DerivedStateNode derived: {
                        m_builder.Append(value: "derive ").Append(value: derived.Name).Append(value: " = ");
                        Expression(expression: derived.Expression, level: level);

                        break;
                    }
                case TestDeclarationNode declaration: {
                        m_builder.Append(value: "test");
                        InlineHeader(trivia: declaration.Trivia);
                        m_builder.Append(value: ' ').Append(value: Quote(text: declaration.Name)).Append(value: " {");
                        EndLine();
                        if (declaration.Given is { } given) {
                            TestBlock(
                                keyword: "given",
                                level: (level + 1),
                                trivia: given.Trivia,
                                write: () => {
                                    foreach (var cell in given.Cells) {
                                        Leading(
                                            level: (level + 2),
                                            trivia: cell.Trivia
                                        );
                                        Indent(level: (level + 2));
                                        Row(row: cell.Target);
                                        m_builder.Append(value: " = ");
                                        Rhs(
                                            level: (level + 2),
                                            rhs: cell.Value
                                        );
                                        Trailing(trivia: cell.Trivia);
                                    }
                                }
                            );
                        }
                        if (declaration.When is { } when) {
                            TestBlock(
                                keyword: "when",
                                level: (level + 1),
                                trivia: when.Trivia,
                                write: () => {
                                    foreach (var step in when.Steps) {
                                        Leading(
                                            level: (level + 2),
                                            trivia: step.Trivia
                                        );
                                        Indent(level: (level + 2));
                                        switch (step) {
                                            case TestTicksStepNode ticks:
                                                m_builder.Append(value: "ticks ").Append(value: ticks.Ticks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

                                                break;
                                            case TestSeatStepNode seat:
                                                m_builder.Append(value: "seat").Append(value: seat.Seat.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)).Append(value: ": ").Append(value: seat.Command);

                                                break;
                                            default:
                                                break;
                                        }
                                        Trailing(trivia: step.Trivia);
                                    }
                                }
                            );
                        }
                        if (declaration.Expect is { } expect) {
                            TestBlock(
                                keyword: "expect",
                                level: (level + 1),
                                trivia: expect.Trivia,
                                write: () => {
                                    foreach (var expectation in expect.Expectations) {
                                        Leading(
                                            level: (level + 2),
                                            trivia: expectation.Trivia
                                        );
                                        Indent(level: (level + 2));
                                        Predicate(
                                            parenthesize: false,
                                            predicate: expectation.Predicate
                                        );
                                        Trailing(trivia: expectation.Trivia);
                                    }
                                }
                            );
                        }
                        Inner(
                            level: (level + 1),
                            trivia: declaration.Trivia
                        );
                        Indent(level: level);
                        m_builder.Append(value: '}');

                        break;
                    }
                case CellSetDeclarationNode declaration: {
                        m_builder.Append(value: "set ").Append(value: Name(name: declaration.Name)).Append(value: ": ").Append(value: declaration.Expression);

                        break;
                    }
                case PatternDeclarationNode pattern: {
                        Pattern(
                            level: level,
                            pattern: pattern
                        );

                        break;
                    }
                case StateTableDeclarationNode table: {
                        m_builder.Append(value: "table");
                        InlineHeader(trivia: table.Trivia);
                        m_builder.Append(value: ' ').Append(value: table.Name);
                        Family(
                            level: level,
                            members: table.FamilyMembers,
                            size: table.FamilySize
                        );
                        // A cell kind is never printed — WorldDocumentEmitter infers it. A non-empty `Kind` here
                        // names a record type the table still expands into one row per field.
                        if (!string.IsNullOrEmpty(value: table.Kind)) {
                            m_builder.Append(value: ": ").Append(value: table.Kind);
                        }
                        Modifiers(
                            level: level,
                            modifiers: table.Modifiers
                        );
                        if (table.Initializer is { } initializer) {
                            m_builder.Append(value: " = ");
                            Expression(
                                expression: initializer,
                                level: level
                            );

                            break;
                        }
                        Cells(
                            cells: table.Cells,
                            level: level,
                            trivia: table.Trivia
                        );

                        break;
                    }
                case StateGridDeclarationNode grid: {
                        m_builder.Append(value: "grid");
                        InlineHeader(trivia: grid.Trivia);
                        m_builder.Append(value: ' ').Append(value: grid.Name);
                        Family(
                            level: level,
                            members: grid.FamilyMembers,
                            size: grid.FamilySize
                        );
                        Modifiers(
                            level: level,
                            modifiers: grid.Modifiers
                        );
                        if (grid.HasBody) {
                            Cells(
                                cells: grid.Cells,
                                level: level,
                                trivia: grid.Trivia
                            );
                        }

                        break;
                    }
                case StateSlotDeclarationNode slot: {
                        m_builder.Append(value: "slot ").Append(value: slot.Name);
                        Family(
                            level: level,
                            members: slot.FamilyMembers,
                            size: slot.FamilySize
                        );
                        if (slot.Value is { } value) {
                            m_builder.Append(value: " = ");
                            Expression(
                                expression: value,
                                level: level
                            );
                        }
                        Modifiers(
                            level: level,
                            modifiers: slot.Modifiers
                        );

                        break;
                    }
                case StatePileDeclarationNode pile: {
                        m_builder.Append(value: "pile");
                        InlineHeader(trivia: pile.Trivia);
                        m_builder.Append(value: ' ').Append(value: pile.Name);
                        Family(
                            level: level,
                            members: pile.FamilyMembers,
                            size: pile.FamilySize
                        );
                        m_builder.Append(value: " of ").Append(value: pile.TokenRow);
                        Modifiers(
                            level: level,
                            modifiers: pile.Modifiers
                        );
                        if (pile.Initializer is { } initializer) {
                            m_builder.Append(value: " = ");
                            Expression(
                                expression: initializer,
                                level: level
                            );

                            break;
                        }
                        if (pile.Tokens.Count == 0) {
                            break;
                        }
                        m_builder.Append(value: " {");
                        EndLine();
                        foreach (var token in pile.Tokens) {
                            Indent(level: (level + 1));
                            m_builder.Append(value: Name(name: token.Key));
                            EndLine();
                        }
                        Indent(level: level);
                        m_builder.Append(value: '}');

                        break;
                    }
                default: {
                        Effect(
                            level: level,
                            statement: statement
                        );

                        break;
                    }
            }
            Trailing(trivia: statement.Trivia);
        }
        private void Effect(StatementNode statement, int level) {
            switch (statement) {
                case SetCellStatementNode set: {
                        Row(row: set.Target);
                        m_builder.Append(value: " = ");
                        Rhs(
                            level: level,
                            rhs: set.Rhs
                        );

                        break;
                    }
                case AddCellStatementNode add: {
                        Row(row: add.Target);
                        m_builder.Append(value: " += ");
                        Rhs(
                            level: level,
                            rhs: add.Rhs
                        );

                        break;
                    }
                case CompoundAssignStatementNode compound: {
                        Row(row: compound.Target);
                        m_builder.Append(value: ' ').Append(value: compound.Operator).Append(value: "= ");
                        Rhs(
                            level: level,
                            rhs: compound.Rhs
                        );

                        break;
                    }
                case PushStatementNode push: {
                        m_builder.Append(value: "push ").Append(value: push.RowName).Append(value: " = ");
                        Rhs(
                            level: level,
                            rhs: push.Rhs
                        );

                        break;
                    }
                case RemoveCellStatementNode remove: {
                        m_builder.Append(value: "remove ");
                        Row(row: remove.Target);

                        break;
                    }
                case ScheduleStatementNode schedule: {
                        m_builder.Append(value: "schedule ");
                        Row(row: schedule.Target);
                        m_builder.Append(value: " in ").Append(value: Number(value: schedule.DelaySeconds)).Append(value: 's');

                        break;
                    }
                case TransformStatementNode transform: {
                        m_builder.Append(value: "transform ");
                        Expression(
                            expression: transform.Transform,
                            level: level
                        );

                        break;
                    }
                case TransactionStatementNode transaction: {
                        m_builder.Append(value: "transaction");
                        InlineHeader(trivia: transaction.Trivia);
                        Body(
                            closing: transaction.Trivia.Inner,
                            level: level,
                            opening: transaction.Trivia.Opening,
                            statements: transaction.MainEffects
                        );
                        if (transaction.OnFailureEffects is { } failure) {
                            m_builder.Append(value: " onFailure");
                            Body(
                                closing: transaction.Trivia.Alternate,
                                level: level,
                                statements: failure
                            );
                        }

                        break;
                    }
                case IfStatementNode branch: {
                        m_builder.Append(value: "if");
                        InlineHeader(trivia: branch.Trivia);
                        m_builder.Append(value: ' ');
                        Predicate(
                            parenthesize: false,
                            predicate: branch.Condition
                        );
                        Body(
                            closing: branch.Trivia.Inner,
                            level: level,
                            opening: branch.Trivia.Opening,
                            statements: branch.Then
                        );
                        if (branch.Else is { } otherwise) {
                            if (
                                (otherwise.Count == 1) &&
                                (otherwise[0] is IfStatementNode nested)
                            ) {
                                m_builder.Append(value: " else ");
                                Effect(
                                    level: level,
                                    statement: nested
                                );
                            } else {
                                m_builder.Append(value: " else");
                                Body(
                                    closing: branch.Trivia.Alternate,
                                    level: level,
                                    statements: otherwise
                                );
                            }
                        }

                        break;
                    }
                case RepeatStatementNode loop: {
                        m_builder.Append(value: "repeat");
                        InlineHeader(trivia: loop.Trivia);
                        m_builder.Append(value: ' ');
                        Expression(
                            expression: loop.Count,
                            level: level
                        );
                        m_builder.Append(value: " as ").Append(value: loop.Index);
                        Body(
                            closing: loop.Trivia.Inner,
                            level: level,
                            opening: loop.Trivia.Opening,
                            statements: loop.Body
                        );

                        break;
                    }
                case ClaimStatementNode claim: {
                        m_builder.Append(value: "claim ").Append(value: claim.Pool).Append(value: " as ").Append(value: claim.Alias);
                        Body(closing: claim.Trivia.Inner, level: level, opening: claim.Trivia.Opening, statements: claim.Body);
                        break;
                    }
                case ClaimPairStatementNode claim: {
                        m_builder.Append(value: "claim pair ").Append(value: claim.Pool).Append(value: " between ").Append(value: claim.Left).Append(value: ", ").Append(value: claim.Right).Append(value: " as ").Append(value: claim.Alias);
                        Body(closing: claim.Trivia.Inner, level: level, opening: claim.Trivia.Opening, statements: claim.Body);
                        break;
                    }
                case PoolForEachStatementNode each: {
                        m_builder.Append(value: "for each ").Append(value: each.Alias).Append(value: " in ").Append(value: each.Pool);
                        Body(closing: each.Trivia.Inner, level: level, opening: each.Trivia.Opening, statements: each.Body);
                        break;
                    }
                case ReleaseStatementNode release: {
                        m_builder.Append(value: "release ").Append(value: release.Alias);
                        break;
                    }
                case BreakStatementNode: {
                        m_builder.Append(value: "break");

                        break;
                    }
                case DrawStatementNode draw: {
                        m_builder.Append(value: "draw ").Append(value: draw.From).Append(value: " to ").Append(value: draw.To);

                        break;
                    }
                case DealStatementNode deal: {
                        m_builder
                            .Append(value: "deal ").Append(value: deal.Count.ToString(provider: CultureInfo.InvariantCulture))
                            .Append(value: " from ").Append(value: deal.From)
                            .Append(value: " to ").Append(value: deal.To);

                        break;
                    }
                case ShuffleStatementNode shuffle: {
                        m_builder.Append(value: "shuffle ").Append(value: shuffle.Row).Append(value: " with ").Append(value: shuffle.Draw);

                        break;
                    }
                default: {
                        break;
                    }
            }
        }
        private void Property(PropertyNode property, int level) {
            var name = PropertyName(
                level: level,
                name: property.Name
            );

            switch (property.Value) {
                case ArrayExpressionNode array: {
                        m_builder.Append(value: name).Append(value: ' ');
                        Array(
                            array: array,
                            level: level
                        );

                        break;
                    }
                case ObjectExpressionNode @object: {
                        m_builder.Append(value: name).Append(value: ' ');
                        Object(
                            level: level,
                            @object: @object
                        );

                        break;
                    }
                default: {
                        m_builder.Append(value: name).Append(value: ": ");
                        Expression(
                            expression: property.Value,
                            level: level
                        );

                        break;
                    }
            }
        }
        private void Row(RowRefNode row) {
            m_builder.Append(value: row.Name);
            if (row.Key is null) {
                return;
            }
            if (row.FieldAccess) {
                m_builder.Append(value: '.').Append(value: row.Key);
                return;
            }
            // A key reads back in the document's own spelling — `$cell:row:key`, `$expr:<infix>` — which is not
            // what an author writes. `Puck.State` owns that fold, so it does it.
            m_builder.Append(value: '[');
            ExpressionSpelling.AppendSourceKey(
                into: m_builder,
                key: row.Key
            );
            m_builder.Append(value: ']');
        }
        private void Rhs(RhsNode rhs, int level) {
            switch (rhs) {
                case RhsTextNode text: {
                        m_builder.Append(value: Quote(text: text.Text));

                        break;
                    }
                case RhsSecondsNode seconds: {
                        m_builder.Append(value: Number(value: seconds.Seconds)).Append(value: 's');

                        break;
                    }
                case RhsOperandNode operand: {
                        m_builder.Append(value: operand.Expression.Text);

                        break;
                    }
                default: {
                        break;
                    }
            }
        }
        private void Family(ExpressionNode? size, IReadOnlyList<FamilyMemberNode>? members, int level) {
            if (size is { } count) {
                m_builder.Append(value: '[');
                Expression(
                    expression: count,
                    level: level
                );
                m_builder.Append(value: ']');

                return;
            }
            if (members is null) {
                return;
            }
            m_builder.Append(value: '[');
            for (var index = 0; (index < members.Count); index++) {
                if (index > 0) { m_builder.Append(value: ", "); }

                var member = members[index];

                if (member.Row is not null) {
                    m_builder.Append(value: Quote(text: member.Row));

                    continue;
                }
                if (member.First is { } first) {
                    Expression(
                        expression: first,
                        level: level
                    );
                }
                if (member.Last is { } last) {
                    m_builder.Append(value: "..");
                    Expression(
                        expression: last,
                        level: level
                    );
                }
            }
            m_builder.Append(value: ']');
        }
        private void Modifiers(IReadOnlyList<StateModifierNode> modifiers, int level) {
            foreach (var modifier in modifiers) {
                m_builder.Append(value: ' ').Append(value: modifier.Name);
                if (modifier.Arguments.Count == 0) {
                    // `evicts` is the one modifier spelled without a call's parentheses.
                    if (!string.Equals(a: modifier.Name, b: "evicts", comparisonType: StringComparison.Ordinal)) { m_builder.Append(value: "()"); }

                    continue;
                }
                m_builder.Append(value: '(');
                Arguments(
                    arguments: modifier.Arguments,
                    level: level
                );
                m_builder.Append(value: ')');
            }
        }
        private void Cells(IReadOnlyList<StateCellEntryNode> cells, SyntaxTrivia trivia, int level) {
            if (
                (cells.Count == 0) &&
                (trivia.Inner.Count == 0)
            ) {
                return;
            }
            m_builder.Append(value: " {");
            EndLine();
            foreach (var cell in cells) {
                Leading(
                    level: (level + 1),
                    trivia: cell.Trivia
                );
                Indent(level: (level + 1));
                m_builder.Append(value: Name(name: cell.Key)).Append(value: " = ");
                Expression(
                    expression: cell.Value,
                    level: (level + 1)
                );
                Modifiers(
                    level: (level + 1),
                    modifiers: cell.Modifiers
                );
                Trailing(trivia: cell.Trivia);
            }
            Inner(
                level: (level + 1),
                trivia: trivia
            );
            Indent(level: level);
            m_builder.Append(value: '}');
        }
        private void Pattern(PatternDeclarationNode pattern, int level) {
            m_builder.Append(value: "pattern ").Append(value: pattern.Name).Append(value: ": ").Append(value: pattern.Kind).Append(value: " {");
            EndLine();
            if (pattern.Attribute is not null) {
                Indent(level: (level + 1));
                m_builder.Append(value: "attribute: ").Append(value: Quote(text: pattern.Attribute));
                EndLine();
            }
            if (pattern.Value is not null) {
                Indent(level: (level + 1));
                m_builder.Append(value: "value: ").Append(value: Quote(text: pattern.Value.Text));
                EndLine();
            }
            if (pattern.MaximumStates is { } budget) {
                Indent(level: (level + 1));
                m_builder.Append(value: "maxStates: ").Append(value: budget.ToString(provider: CultureInfo.InvariantCulture));
                EndLine();
            }
            if (pattern.Symbols.Count > 0) {
                Indent(level: (level + 1));
                m_builder.Append(value: "symbols {");
                EndLine();
                foreach (var symbol in pattern.Symbols) {
                    Indent(level: (level + 2));
                    m_builder.Append(value: symbol.Name).Append(value: " = ").Append(value: Number(value: symbol.Minimum));
                    if (symbol.Maximum != symbol.Minimum) { m_builder.Append(value: "..").Append(value: Number(value: symbol.Maximum)); }
                    EndLine();
                }
                Indent(level: (level + 1));
                m_builder.Append(value: '}');
                EndLine();
            }
            Indent(level: (level + 1));
            m_builder.Append(value: "match: ").Append(value: PatternSpelling.Print(node: pattern.Match));
            EndLine();
            Indent(level: level);
            m_builder.Append(value: '}');
        }
        private void Predicate(PredicateNode predicate, bool parenthesize) {
            switch (predicate) {
                case ComparisonPredicateNode comparison: {
                        var annotated = (comparison.Kind is not null);

                        if (annotated) { m_builder.Append(value: '('); }
                        m_builder.Append(value: comparison.Left.Text).Append(value: ' ').Append(value: comparison.Comparator).Append(value: ' ').Append(value: comparison.Right.Text);
                        if (annotated) { m_builder.Append(value: " : ").Append(value: comparison.Kind).Append(value: ')'); }

                        break;
                    }
                case CallPredicateNode call: {
                        Expression(
                            expression: call.Call,
                            level: 0
                        );

                        break;
                    }
                case NotPredicateNode negation: {
                        m_builder.Append(value: "not ");
                        Predicate(
                            parenthesize: (negation.Operand is AndPredicateNode or OrPredicateNode),
                            predicate: negation.Operand
                        );

                        break;
                    }
                case AndPredicateNode conjunction: {
                        Chain(
                            keyword: " and ",
                            operands: conjunction.Operands,
                            parenthesize: parenthesize
                        );

                        break;
                    }
                case OrPredicateNode disjunction: {
                        Chain(
                            keyword: " or ",
                            operands: disjunction.Operands,
                            parenthesize: parenthesize
                        );

                        break;
                    }
                default: {
                        break;
                    }
            }
        }
        private void Chain(IReadOnlyList<PredicateNode> operands, string keyword, bool parenthesize) {
            if (parenthesize) { m_builder.Append(value: '('); }
            for (var index = 0; (index < operands.Count); index++) {
                if (index > 0) { m_builder.Append(value: keyword); }
                Predicate(
                    parenthesize: (operands[index] is AndPredicateNode or OrPredicateNode),
                    predicate: operands[index]
                );
            }
            if (parenthesize) { m_builder.Append(value: ')'); }
        }
        private void Arguments(IReadOnlyList<ArgumentNode> arguments, int level, bool multiLine = false) {
            for (var index = 0; (index < arguments.Count); index++) {
                var argument = arguments[index];
                var breaks = (multiLine && (argument.Trivia.OnNewLine || (argument.Trivia.Leading.Count > 0)));

                if (
                    (index > 0) &&
                    !breaks
                ) {
                    m_builder.Append(value: ", ");
                }
                if (breaks) {
                    EndLine();
                    Leading(
                        level: (level + 1),
                        trivia: argument.Trivia
                    );
                    Indent(level: (level + 1));
                }
                if (argument.Name is { } name) { m_builder.Append(value: name).Append(value: ": "); }
                Expression(
                    expression: argument.Value,
                    level: ((level + (breaks
                        ? 1
                        : 0)))
                );
                if (
                    argument.Trivia.Separated &&
                    (((index + 1) >= arguments.Count) || arguments[(index + 1)].Trivia.OnNewLine || (arguments[(index + 1)].Trivia.Leading.Count > 0))
                ) {
                    m_builder.Append(value: ',');
                }
                if (argument.Trivia.Trailing is not null) { m_builder.Append(value: ' ').Append(value: argument.Trivia.Trailing); }
            }
        }
        private void Array(ArrayExpressionNode array, int level) {
            if (array.Elements.Count == 0) {
                m_builder.Append(value: ((array.Trivia.Inner.Count == 0)
                    ? "[]"
                    : "["
                ));
                if (array.Trivia.Inner.Count == 0) { return; }
                EndLine();
                Inner(
                    level: (level + 1),
                    trivia: array.Trivia
                );
                Indent(level: level);
                m_builder.Append(value: ']');

                return;
            }
            if (!array.Trivia.MultiLine) {
                m_builder.Append(value: '[');
                for (var index = 0; (index < array.Elements.Count); index++) {
                    if (index > 0) { m_builder.Append(value: ", "); }
                    Expression(
                        expression: array.Elements[index],
                        level: level
                    );
                }
                m_builder.Append(value: ']');

                return;
            }
            // The author's own line breaks decide where the rows fall, so a board written eight to a row keeps its
            // rows and a list written one to a line keeps its lines.
            m_builder.Append(value: '[');
            if (array.Trivia.Opening is not null) { m_builder.Append(value: ' ').Append(value: array.Trivia.Opening); }
            for (var index = 0; (index < array.Elements.Count); index++) {
                var element = array.Elements[index];
                var breaks = (element.Trivia.OnNewLine || (element.Trivia.Leading.Count > 0));
                var joins = (
                    ((index + 1) < array.Elements.Count) &&
                    !(array.Elements[(index + 1)].Trivia.OnNewLine || (array.Elements[(index + 1)].Trivia.Leading.Count > 0))
                );

                if (
                    (index > 0) &&
                    !breaks
                ) {
                    m_builder.Append(value: ", ");
                }
                if (breaks) {
                    EndLine();
                    Leading(
                        level: (level + 1),
                        trivia: element.Trivia
                    );
                    Indent(level: (level + 1));
                }
                Expression(
                    expression: element,
                    level: (level + 1)
                );
                // A line break does not end an expression: a following element opening with a sign or a parenthesis
                // would read as this one's continuation, so one takes a separator whether its author wrote it or not.
                if (!joins && (
                    element.Trivia.Separated ||
                    (((index + 1) < array.Elements.Count) &&
                    ContinuesPreviousElement(element: array.Elements[(index + 1)], level: (level + 1)))
                )) {
                    m_builder.Append(value: ',');
                }
                if (element.Trivia.Trailing is not null) {
                    m_builder.Append(value: ' ').Append(value: element.Trivia.Trailing);
                }
            }
            EndLine();
            Inner(
                level: (level + 1),
                trivia: array.Trivia
            );
            Indent(level: level);
            m_builder.Append(value: ']');
        }
        private void Object(ObjectExpressionNode @object, int level) {
            if (@object.Properties.Count == 0) {
                m_builder.Append(value: "{}");

                return;
            }
            if (!@object.Trivia.MultiLine) {
                m_builder.Append(value: "{ ");
                for (var index = 0; (index < @object.Properties.Count); index++) {
                    if (index > 0) { m_builder.Append(value: ", "); }
                    Property(
                        level: level,
                        property: @object.Properties[index]
                    );
                }
                m_builder.Append(value: " }");

                return;
            }
            m_builder.Append(value: '{');
            if (@object.Trivia.Opening is not null) { m_builder.Append(value: ' ').Append(value: @object.Trivia.Opening); }
            for (var index = 0; (index < @object.Properties.Count); index++) {
                var property = @object.Properties[index];
                var breaks = (property.Trivia.OnNewLine || (property.Trivia.Leading.Count > 0));
                var joins = (
                    ((index + 1) < @object.Properties.Count) &&
                    !(@object.Properties[(index + 1)].Trivia.OnNewLine || (@object.Properties[(index + 1)].Trivia.Leading.Count > 0))
                );

                if (
                    (index > 0) &&
                    !breaks
                ) {
                    m_builder.Append(value: ", ");
                }
                if (breaks) {
                    EndLine();
                    Leading(
                        level: (level + 1),
                        trivia: property.Trivia
                    );
                    Indent(level: (level + 1));
                }
                Property(
                    level: (level + 1),
                    property: property
                );
                if (!joins && property.Trivia.Separated) { m_builder.Append(value: ','); }
                if (property.Trivia.Trailing is not null) {
                    m_builder.Append(value: ' ').Append(value: property.Trivia.Trailing);
                }
            }
            EndLine();
            Inner(
                level: (level + 1),
                trivia: @object.Trivia
            );
            Indent(level: level);
            m_builder.Append(value: '}');
        }

        public void Value(ExpressionNode expression) => this.Expression(expression: expression, level: 0);

        private void Expression(ExpressionNode expression, int level) {
            if (expression.Parenthesized) {
                m_builder.Append(value: '(');
                Expression(expression: expression with { Parenthesized = false }, level: level);
                m_builder.Append(value: ')');
                return;
            }
            switch (expression) {
                case AssetExpressionNode asset: {
                        m_builder.Append(value: "asset ").Append(value: Quote(text: asset.Path));

                        break;
                    }
                case LiteralExpressionNode literal: {
                        m_builder.Append(value: Literal(literal: literal));

                        break;
                    }
                case ColorExpressionNode color: {
                        m_builder.Append(value: color.Hex);

                        break;
                    }
                case IdentifierExpressionNode identifier: {
                        m_builder.Append(value: identifier.Name);

                        break;
                    }
                case MemberAccessExpressionNode member: {
                        Expression(
                            expression: member.Target,
                            level: level
                        );
                        m_builder.Append(value: '.').Append(value: member.Member);

                        break;
                    }
                case CallExpressionNode call: {
                        m_builder.Append(value: call.Name).Append(value: '(');
                        Arguments(
                            arguments: call.Arguments,
                            level: level,
                            multiLine: call.Trivia.MultiLine
                        );
                        if (call.Trivia.MultiLine) {
                            EndLine();
                            Indent(level: level);
                        }
                        m_builder.Append(value: ')');

                        break;
                    }
                case BinaryExpressionNode binary: {
                        Operand(
                            level: level,
                            operand: binary.Left,
                            parenthesize: (Precedence(@operator: binary.Left) < Precedence(@operator: binary))
                        );
                        m_builder.Append(value: ' ').Append(value: binary.Operator).Append(value: ' ');
                        Operand(
                            level: level,
                            operand: binary.Right,
                            parenthesize: (Precedence(@operator: binary.Right) <= Precedence(@operator: binary))
                        );

                        break;
                    }
                case UnaryExpressionNode unary: {
                        m_builder.Append(value: unary.Operator);
                        Operand(
                            level: level,
                            operand: unary.Operand,
                            parenthesize: (Precedence(@operator: unary.Operand) < PrimaryPrecedence)
                        );

                        break;
                    }
                case IndexExpressionNode index: {
                        Expression(
                            expression: index.Target,
                            level: level
                        );
                        m_builder.Append(value: '[');
                        Expression(
                            expression: index.Index,
                            level: level
                        );
                        m_builder.Append(value: ']');

                        break;
                    }
                case OperandExpressionNode operand: {
                        m_builder.Append(value: operand.Text);

                        break;
                    }
                case RangeExpressionNode range: {
                        if (range.Start is { } start) {
                            Expression(expression: start, level: level);
                        }
                        m_builder.Append(value: "..");
                        if (range.End is { } end) {
                            Expression(expression: end, level: level);
                        }

                        break;
                    }
                case LambdaExpressionNode lambda: {
                        if (lambda.Parameters.Count == 1) {
                            m_builder.Append(value: lambda.Parameters[0]);
                        } else {
                            m_builder.Append(value: '(').Append(value: string.Join(
                                separator: ", ",
                                values: lambda.Parameters
                            )).Append(value: ')');
                        }
                        m_builder.Append(value: " => ");
                        Expression(
                            expression: lambda.Body,
                            level: level
                        );

                        break;
                    }
                case ArrayExpressionNode array: {
                        Array(
                            array: array,
                            level: level
                        );

                        break;
                    }
                case ObjectExpressionNode @object: {
                        Object(
                            level: level,
                            @object: @object
                        );

                        break;
                    }
                case InterpolatedStringNode interpolated: {
                        Interpolated(
                            interpolated: interpolated,
                            level: level
                        );

                        break;
                    }
                default: {
                        break;
                    }
            }
        }
        private void Operand(ExpressionNode operand, bool parenthesize, int level) {
            parenthesize &= !operand.Parenthesized;
            if (parenthesize) { m_builder.Append(value: '('); }
            Expression(
                expression: operand,
                level: level
            );
            if (parenthesize) { m_builder.Append(value: ')'); }
        }
        // An interpolated string's holes are read out of the string's DECODED text, so the whole body — hole
        // expressions included — is composed first and escaped once. Escaping a hole's own string arguments is what
        // keeps `$"{row["key"]}"` one string rather than three.
        private void Interpolated(InterpolatedStringNode interpolated, int level) {
            var body = new StringBuilder();

            foreach (var segment in interpolated.Segments) {
                switch (segment) {
                    case InterpolationSegment.Literal literal: {
                            foreach (var character in literal.Text) {
                                switch (character) {
                                    case '{': { body.Append(value: "{{"); break; }
                                    case '}': { body.Append(value: "}}"); break; }
                                    default: { body.Append(value: character); break; }
                                }
                            }

                            break;
                        }
                    case InterpolationSegment.Hole hole: {
                            body.Append(value: '{').Append(value: Capture(expression: hole.Expression, level: level)).Append(value: '}');

                            break;
                        }
                    default: {
                            break;
                        }
                }
            }

            var text = body.ToString();

            // A fence carries no escapes, so it is the spelling only when it reads back exactly.
            if (
                interpolated.RawFenced &&
                PuckStrings.CanFence(text: text)
            ) {
                m_builder.Append(value: "$\"\"\"").Append(value: text).Append(value: "\"\"\"");

                return;
            }
            m_builder.Append(value: "$\"");
            PuckStrings.Escape(
                into: m_builder,
                text: text
            );
            m_builder.Append(value: '"');
        }
        private bool ContinuesPreviousElement(ExpressionNode element, int level) {
            var text = Capture(
                expression: element,
                level: level
            );

            return ((text.Length > 0) && (text[0] is '-' or '+' or '('));
        }
        private string Capture(ExpressionNode expression, int level) {
            var start = m_builder.Length;

            Expression(
                expression: expression,
                level: level
            );

            var text = m_builder.ToString(
                length: (m_builder.Length - start),
                startIndex: start
            );

            m_builder.Length = start;

            return text;
        }

        private const int PrimaryPrecedence = 5;

        private static int Precedence(ExpressionNode @operator) => (@operator switch {
            BinaryExpressionNode { Operator: "==" or "!=" or "<" or "<=" or ">" or ">=" } => 1,
            RangeExpressionNode => 2,
            BinaryExpressionNode { Operator: "+" or "-" } => 3,
            BinaryExpressionNode { Operator: "*" or "/" or "%" } => 4,
            LambdaExpressionNode => 0,
            _ => PrimaryPrecedence,
        });
        private static string Number(decimal value) => value.ToString(provider: CultureInfo.InvariantCulture);
        private static string Literal(LiteralExpressionNode literal) {
            var text = (literal.Value switch {
                null => "null",
                bool flag => (flag
                    ? "true"
                    : "false"),
                string value => ((literal.RawFenced && PuckStrings.CanFence(text: value))
                    ? $"\"\"\"{value}\"\"\""
                    : Quote(text: value)),
                double number => (literal.RawText ?? number.ToString(
                    format: "R",
                    provider: CultureInfo.InvariantCulture
                )),
                long number => (literal.RawText ?? number.ToString(provider: CultureInfo.InvariantCulture)),
                decimal number => Number(value: number),
                _ => Convert.ToString(
                    provider: CultureInfo.InvariantCulture,
                    value: literal.Value
                )!,
            });

            return ((literal.Unit is null)
                ? text
                : (text + literal.Unit)
            );
        }
    }
}
