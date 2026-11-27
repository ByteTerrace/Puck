using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck eval &lt;file&gt; &lt;expression&gt;</c>: evaluates a compile-time expression in a <c>.puck</c> source's
/// scope (its <c>let</c>s, functions, templates' constants and imports) and prints the value as JSON.</summary>
/// <remarks>The expression is compiled as a member of the source itself, through the one compile every host uses
/// (<see cref="WorldCompileSession"/>), so it reads exactly what a value written at the end of the file reads; a
/// library source evaluates as well as a document.</remarks>
public static class EvalCommand {
    // The member the expression is compiled as: a reserved extension key, which the document keeps as data.
    private const string Member = "_puckEval";

    /// <summary>Creates the <c>eval</c> verb.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() {
        var fileArgument = new Argument<string>(name: "file") { Description = "The .puck source whose scope the expression is evaluated in." };
        var expressionArgument = new Argument<string>(name: "expression") { Description = "The expression, as it would be written as a value in that source: a let's name, a function call, arithmetic, a list." };
        var command = new Command(
            description: "Evaluate a compile-time expression in a .puck source's scope (its lets, functions and imports) and print its value as JSON.",
            name: "eval"
        ) {
            fileArgument,
            expressionArgument,
        };

        command.SetAction(action: parseResult => Execute(
            expression: parseResult.GetRequiredValue(argument: expressionArgument),
            path: parseResult.GetRequiredValue(argument: fileArgument)
        ));

        return command;
    }
    /// <summary>Evaluates <paramref name="expression"/> in the scope of the source at <paramref name="path"/> and prints
    /// its value.</summary>
    /// <param name="path">The <c>.puck</c> source.</param>
    /// <param name="expression">The expression.</param>
    /// <returns>0 when the expression evaluated; 1 when it did not, with its diagnostics printed; 2 when the source
    /// could not be read.</returns>
    public static int Execute(string path, string expression) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: expression);

        var fullPath = Path.GetFullPath(path: path);
        string source;

        try {
            source = File.ReadAllText(path: fullPath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return CliExit.Refuse(verb: "eval", what: CliPaths.ToDisplay(fullPath: fullPath), why: ex.Message);
        }

        // The expression stands on a line of its own after the source, so a diagnostic about it names that line; bound to a
        // `let` first, it is evaluated as a constant is, where a name nothing declares is refused rather than read as a word.
        var text = $"{source.TrimEnd()}\nlet {Member} = ({expression})\n{Member}: {Member}\n";
        var expressionLine = (text.Count(predicate: static c => (c == '\n')) - 1);
        var diagnostics = new DiagnosticBag();
        var compilation = WorldCompileSession.Shared.Compile(diagnostics: diagnostics, imports: ImportHandling.Validate, source: text, sourceMap: new SourceMap(), sourcePath: fullPath);
        var errors = diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)).ToList();

        if ((errors.Count != 0) || (compilation.Json?[Member] is not { } value)) {
            foreach (var error in errors.Take(count: 8)) {
                var where = (((error.SourcePath is null) && (error.Span.Line == expressionLine)) ? "the expression" : $"{error.SourcePath ?? CliPaths.ToDisplay(fullPath: fullPath)}({error.Span.Line},{error.Span.Column})");

                Console.Error.WriteLine(value: $"eval: {where}: {error.Code}: {error.Message}");
            }
            if (errors.Count == 0) { Console.Error.WriteLine(value: "eval: the expression has no value at compile time."); }

            return CliExit.Failed;
        }

        // A bare word a value position reads as its own name when nothing declares it: an expression is asked for a value,
        // so that word is refused.
        if ((Puck.Transpiler.Parsing.PuckParser.ParseExpression(source: expression) is Puck.Transpiler.Ast.IdentifierExpressionNode word) &&
            (value is JsonValue spelled) && spelled.TryGetValue<string>(value: out var spelling) && (spelling == word.Name)) {
            Console.Error.WriteLine(value: $"eval: '{word.Name}' names no let, function or import in scope.");

            return CliExit.Failed;
        }

        Console.WriteLine(value: value.ToJsonString(options: Compact));

        return CliExit.Success;
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
}
