using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Analyzers;

/// <summary>
/// Holds the fences a rule evaluation's answer cannot hold by its type: an <c>Outcome</c> is a case of the value, so a
/// caller that drops it, reads a number from it without looking at the answer, reads a verdict as one case,
/// evaluates tokens outside the interpreter, or lets an operand resolve an address of its own has turned a fault back
/// into a quiet value. Each rule reads the compiler's symbols, so an alias, a <c>using static</c> or a method group is
/// seen exactly as the compiler sees it. A test assembly (one that references xUnit v3) is held to none of them: its
/// evaluations are the oracle the interpreter is measured against, and a test that watches state rather than an answer
/// owes the answer no handling.
/// <list type="bullet">
/// <item><see cref="Outcome001DiscardedOutcome"/>: an expression statement, a discard assignment or a discard
/// declaration over <c>Outcome</c>, <c>Resolved&lt;T&gt;</c>, <c>Verdict</c> or <c>RuleOutcome</c>. Two shapes are
/// not drops: an assignment (its target keeps the value) and a statement in a <c>catch</c> clause that rethrows (the
/// exception is the answer the caller reads). A method <see cref="OutcomeDiscardAllowlist"/> names may drop one, with its
/// reason.</item>
/// <item><see cref="Outcome002ReadsOneCase"/>: a <c>TryFinite</c> or <c>TryNamed</c> whose bool is dropped (the number
/// then reads as zero beside a case the caller never saw), or a <c>Verdict</c> or <c>FiringDecision</c> compared with
/// <c>==</c>, <c>!=</c> or an <c>is</c> constant pattern to a case other than <c>Faulted</c>, which reads a faulted
/// answer as the case it was not compared to. A <c>switch</c> naming every case is the spelling, and a comparison to
/// <c>Faulted</c> is the handling itself.</item>
/// <item><see cref="Outcome003DetachedCaller"/>: a call of <c>RuleExpressions.EvaluateDetached</c> from a file
/// <see cref="OutcomeDetachedAllowlist"/> does not name.</item>
/// <item><see cref="Outcome004OperandResolvesAnAddress"/>: a member of a type implementing <c>IRuleOperand</c> that
/// resolves an address against the reader through a <c>CompiledCellRef</c>, a <c>LiveRow</c> or an <c>IRuleKey</c>. An
/// operand declares its addresses through <c>Addresses</c> and receives them resolved, so one it resolves itself reads
/// through a fault the interpreter would have ended the read at. An operand may hold the declaration (the read set,
/// the price and the address list are built from it); it may not call it.</item>
/// </list>
/// The rules stop where the syntax does: a bool held in a local and never read, and a hand-written arena read with a
/// literal key, are outside what they see.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OutcomeAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Outcomes";

    /// <summary>OUTCOME001: an answer that carries a fault is dropped.</summary>
    public static readonly DiagnosticDescriptor Outcome001DiscardedOutcome = new(
        id: "OUTCOME001",
        title: "A rule outcome is discarded",
        messageFormat: "'{0}' answers a {1}, which this statement discards; read its case or hand it on, because a dropped answer is a fault nothing saw",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Outcome, Resolved<T>, Verdict and RuleOutcome carry a faulted case beside the values. An expression statement or a discard of one loses that case."
    );
    /// <summary>OUTCOME002: a number or a key is read without the answer's case, or a verdict is read as one case.</summary>
    public static readonly DiagnosticDescriptor Outcome002ReadsOneCase = new(
        id: "OUTCOME002",
        title: "A rule outcome is read without its case",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "TryFinite and TryNamed answer whether the value exists; ignoring that answer reads zero beside a fault. A Verdict or FiringDecision compared to one case other than Faulted reads a faulted answer as the other case; a switch that names every case is the spelling."
    );
    /// <summary>OUTCOME003: the detached evaluator is called from a file the list does not name.</summary>
    public static readonly DiagnosticDescriptor Outcome003DetachedCaller = new(
        id: "OUTCOME003",
        title: "A detached evaluation outside the listed consumers",
        messageFormat: "'{0}' is called from {1}, which OutcomeDetachedAllowlist does not name; only a consumer that is not a firing and states its Faulted arm evaluates detached, and the interpreter's own entry takes a frame",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "RuleExpressions.EvaluateDetached is the second entry to the interpreter for a caller that is not a firing. The list of such callers is closed, and each entry names what its Faulted arm does."
    );
    /// <summary>OUTCOME004: an operand resolves an address against the reader itself.</summary>
    public static readonly DiagnosticDescriptor Outcome004OperandResolvesAnAddress = new(
        id: "OUTCOME004",
        title: "An operand resolves an address of its own",
        messageFormat: "'{0}' is an IRuleOperand and calls '{1}', which resolves an address against the reader; an operand reads only the addresses it declares through Addresses, which the interpreter resolves first and ends the read at when one faults",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An operand receives the resolved keys of its declared addresses. Resolving a CompiledCellRef, a LiveRow or an IRuleKey itself reads through an address whose computation may have faulted."
    );

    private static readonly (string Namespace, string Name, string Display)[] OutcomeTypes = [
        ("Puck.State", "Outcome", "Outcome"),
        ("Puck.State", "Resolved", "Resolved<T>"),
        ("Puck.State", "Verdict", "Verdict"),
        ("Puck.State.Rules", "RuleOutcome", "RuleOutcome"),
    ];
    private static readonly (string Namespace, string Name)[] CaseTypes = [
        ("Puck.State", "Verdict"),
        ("Puck.State", "FiringDecision"),
    ];

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Outcome001DiscardedOutcome,
        Outcome002ReadsOneCase,
        Outcome003DetachedCaller,
        Outcome004OperandResolvesAnAddress
    );

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        // A generated statement drops an answer exactly as a hand-written one does.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(action: static start => {
            if (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.v3.IFactAttribute") is not null) {
                return;
            }

            start.RegisterOperationAction(
                action: AnalyzeExpressionStatement,
                operationKinds: OperationKind.ExpressionStatement
            );
            start.RegisterOperationAction(
                action: AnalyzeDiscardAssignment,
                operationKinds: OperationKind.SimpleAssignment
            );
            start.RegisterOperationAction(
                action: AnalyzeDiscardDeclaration,
                operationKinds: OperationKind.VariableDeclarator
            );
            start.RegisterOperationAction(
                action: AnalyzeBinary,
                operationKinds: OperationKind.Binary
            );
            start.RegisterOperationAction(
                action: AnalyzeIsPattern,
                operationKinds: OperationKind.IsPattern
            );
            start.RegisterOperationAction(
                action: AnalyzeInvocation,
                operationKinds: OperationKind.Invocation
            );
        });
    }

    private static bool Is(ITypeSymbol? type, string @namespace, string name) {
        if (type is null) {
            return false;
        }

        var definition = type.OriginalDefinition;

        return (
            (definition.Name == name) &&
            (definition.ContainingType is null) &&
            (definition.ContainingNamespace?.ToDisplayString() == @namespace)
        );
    }
    private static bool Implements(ITypeSymbol? type, string @namespace, string name) {
        if (type is null) {
            return false;
        }
        if (Is(type: type, @namespace: @namespace, name: name)) {
            return true;
        }

        foreach (var contract in type.AllInterfaces) {
            if (Is(type: contract, @namespace: @namespace, name: name)) {
                return true;
            }
        }

        return false;
    }
    private static string? OutcomeDisplay(ITypeSymbol? type) {
        foreach (var (@namespace, name, display) in OutcomeTypes) {
            if (Is(type: type, @namespace: @namespace, name: name)) {
                return display;
            }
        }

        return null;
    }
    private static bool IsCaseType(ITypeSymbol? type) {
        foreach (var (@namespace, name) in CaseTypes) {
            if (Is(type: type, @namespace: @namespace, name: name)) {
                return true;
            }
        }

        return false;
    }
    private static IOperation Unwrap(IOperation operation) {
        var current = operation;

        while (true) {
            switch (current) {
                case IParenthesizedOperation parenthesized:
                    current = parenthesized.Operand;
                    continue;
                case IAwaitOperation awaited:
                    current = awaited.Operation;
                    continue;
                case IConversionOperation { IsImplicit: true } conversion:
                    current = conversion.Operand;
                    continue;
                default:
                    return current;
            }
        }
    }
    private static string Describe(IOperation operation) =>
        ((operation is IInvocationOperation invocation)
            ? ($"{invocation.TargetMethod.ContainingType?.Name}.{invocation.TargetMethod.Name}")
            : operation.Syntax.ToString());
    // A catch clause that rethrows answers by throwing: the caller reads the exception, so the outcome of the rewind
    // it runs on the way out is not a drop.
    private static bool InRethrowingCatch(SyntaxNode node) {
        foreach (var ancestor in node.Ancestors()) {
            if (ancestor is CatchClauseSyntax clause) {
                return clause.Block.DescendantNodes(descendIntoChildren: static child => child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<ThrowStatementSyntax>().Any();
            }
            if (ancestor is MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) {
                return false;
            }
        }

        return false;
    }
    private static void ReportDiscard(OperationAnalysisContext context, IOperation dropped, Location location) {
        var display = OutcomeDisplay(type: Unwrap(operation: dropped).Type);

        if (display is null) {
            return;
        }
        if (InRethrowingCatch(node: dropped.Syntax)) {
            return;
        }
        if (context.ContainingSymbol is IMethodSymbol method && OutcomeDiscardAllowlist.Admits(
            assembly: (context.Compilation.AssemblyName ?? string.Empty),
            member: method.Name,
            type: (method.ContainingType?.Name ?? string.Empty)
        )) {
            return;
        }

        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Outcome001DiscardedOutcome,
            location: location,
            messageArgs: [Describe(operation: Unwrap(operation: dropped)), display]
        ));
    }
    private static bool IsTry(IOperation operation) =>
        (Unwrap(operation: operation) is IInvocationOperation { TargetMethod: { Name: "TryFinite" or "TryNamed" } method } &&
        (Is(type: method.ContainingType, @namespace: "Puck.State", name: "Outcome") || Is(type: method.ContainingType, @namespace: "Puck.State", name: "Resolved")));
    private static void AnalyzeExpressionStatement(OperationAnalysisContext context) {
        var statement = ((IExpressionStatementOperation)context.Operation);

        // An assignment's value is stored in its target, so only a discard target drops it (AnalyzeDiscardAssignment).
        if (Unwrap(operation: statement.Operation) is IAssignmentOperation) {
            return;
        }
        if (IsTry(operation: statement.Operation)) {
            ReportUnconsumedTry(
                context: context,
                location: statement.Syntax.GetLocation(),
                operation: Unwrap(operation: statement.Operation)
            );

            return;
        }

        ReportDiscard(
            context: context,
            dropped: statement.Operation,
            location: statement.Syntax.GetLocation()
        );
    }
    private static void AnalyzeDiscardAssignment(OperationAnalysisContext context) {
        var assignment = ((ISimpleAssignmentOperation)context.Operation);

        if (assignment.Target is not IDiscardOperation) {
            return;
        }
        if (IsTry(operation: assignment.Value)) {
            ReportUnconsumedTry(
                context: context,
                location: assignment.Syntax.GetLocation(),
                operation: Unwrap(operation: assignment.Value)
            );

            return;
        }

        ReportDiscard(
            context: context,
            dropped: assignment.Value,
            location: assignment.Syntax.GetLocation()
        );
    }
    private static void AnalyzeDiscardDeclaration(OperationAnalysisContext context) {
        var declarator = ((IVariableDeclaratorOperation)context.Operation);

        if (
            (declarator.Symbol.Name != "_") ||
            (declarator.Initializer is not { } initializer)
        ) {
            return;
        }

        ReportDiscard(
            context: context,
            dropped: initializer.Value,
            location: declarator.Syntax.GetLocation()
        );
    }
    private static void ReportUnconsumedTry(OperationAnalysisContext context, Location location, IOperation operation) {
        var invocation = ((IInvocationOperation)operation);

        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Outcome002ReadsOneCase,
            location: location,
            messageArgs: [$"'{invocation.TargetMethod.ContainingType?.Name}.{invocation.TargetMethod.Name}' answers whether the value exists, and this statement drops that answer; branch on it, so the faulted and absent cases are handled where they are met"]
        ));
    }
    // Whether an operand names the type's Faulted case, the handling a deciding place does first.
    private static bool NamesFaulted(IOperation operand) =>
        (Unwrap(operation: operand) is IFieldReferenceOperation { Field.Name: "Faulted" });
    private static void AnalyzeBinary(OperationAnalysisContext context) {
        var binary = ((IBinaryOperation)context.Operation);

        if (binary.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)) {
            return;
        }

        var left = Unwrap(operation: binary.LeftOperand);
        var right = Unwrap(operation: binary.RightOperand);
        var type = (IsCaseType(type: left.Type)
            ? left.Type
            : (IsCaseType(type: right.Type)
                ? right.Type
                : null
            )
        );

        if ((type is null) || NamesFaulted(operand: left) || NamesFaulted(operand: right)) {
            return;
        }

        ReportCase(
            context: context,
            location: binary.Syntax.GetLocation(),
            type: type
        );
    }
    private static void AnalyzeIsPattern(OperationAnalysisContext context) {
        var pattern = ((IIsPatternOperation)context.Operation);
        var type = Unwrap(operation: pattern.Value).Type;

        if (!IsCaseType(type: type)) {
            return;
        }

        var inner = pattern.Pattern;

        while (inner is INegatedPatternOperation negated) {
            inner = negated.Pattern;
        }

        // `is A or B` names several cases, and a pattern naming Faulted is the handling; only a pattern that is one
        // other case reads a fault as the case it was not compared to.
        if (inner is not IConstantPatternOperation constant || NamesFaulted(operand: constant.Value)) {
            return;
        }

        ReportCase(
            context: context,
            location: pattern.Syntax.GetLocation(),
            type: type!
        );
    }
    private static void ReportCase(OperationAnalysisContext context, Location location, ITypeSymbol type) =>
        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Outcome002ReadsOneCase,
            location: location,
            messageArgs: [$"a {type.Name} is compared to one case other than Faulted, which reads a faulted answer as the case it was not compared to; switch over every case of {type.Name}"]
        ));
    private static void AnalyzeInvocation(OperationAnalysisContext context) {
        var invocation = ((IInvocationOperation)context.Operation);
        var method = invocation.TargetMethod;

        if (
            (method.Name == "EvaluateDetached") &&
            Is(
                type: method.ContainingType,
                @namespace: "Puck.State.Rules",
                name: "RuleExpressions"
            )
        ) {
            var path = (invocation.Syntax.SyntaxTree.FilePath ?? string.Empty);

            if (!OutcomeDetachedAllowlist.Admits(
                assembly: (context.Compilation.AssemblyName ?? string.Empty),
                path: path
            )) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                    descriptor: Outcome003DetachedCaller,
                    location: invocation.Syntax.GetLocation(),
                    messageArgs: ["RuleExpressions.EvaluateDetached", (string.IsNullOrEmpty(value: path) ? "a file with no path" : System.IO.Path.GetFileName(path: path))]
                ));
            }

            return;
        }

        if (
            (context.ContainingSymbol.ContainingType is { } holder) &&
            Implements(type: holder, @namespace: "Puck.State.Rules", name: "IRuleOperand") &&
            ResolvesAgainstReader(method: method)
        ) {
            context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Outcome004OperandResolvesAnAddress,
                location: invocation.Syntax.GetLocation(),
                messageArgs: [holder.Name, $"{method.ContainingType?.Name}.{method.Name}"]
            ));
        }
    }
    // A method that resolves an address of a cell reference, a live row or a key against the reader it is handed.
    private static bool ResolvesAgainstReader(IMethodSymbol method) {
        if (
            (method.ContainingType is not { } owner) ||
            !(
                Is(type: owner, @namespace: "Puck.State.Rules", name: "CompiledCellRef") ||
                Is(type: owner, @namespace: "Puck.State.Rules", name: "LiveRow") ||
                Implements(type: owner, @namespace: "Puck.State.Rules", name: "IRuleKey")
            )
        ) {
            return false;
        }

        foreach (var parameter in method.Parameters) {
            if (Is(
                type: parameter.Type,
                @namespace: "Puck.State",
                name: "IStateReader"
            )) {
                return true;
            }
        }

        return false;
    }
}
