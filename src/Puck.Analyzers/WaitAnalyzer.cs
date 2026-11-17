using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using static Puck.Analyzers.NamedTypes;

namespace Puck.Analyzers;

/// <summary>
/// WAIT001: a thread blocks outside the engine's one wait door, <c>Puck.Abstractions.Waiting.HostWaits</c>. A project
/// is held to the rule when it sets the MSBuild property <c>PuckWaitRule</c> to <c>true</c>; a test assembly (one that
/// references xUnit v3) never is.
/// <para>
/// The rule reports any reference to a blocking member, read from the compiler's symbols: a call, a method group or
/// delegate conversion (<c>Action&lt;TimeSpan&gt; sleep = Thread.Sleep</c>), or a property read. The members are every
/// managed wait family (<c>Task.Wait</c>, <c>WaitAll</c>, <c>WaitAny</c>, a task's <c>Result</c> and its awaiters'
/// <c>GetResult</c>, <c>WaitHandle.WaitOne</c>, <c>WaitAll</c>, <c>WaitAny</c> and <c>SignalAndWait</c>,
/// <c>ManualResetEventSlim.Wait</c>, <c>SemaphoreSlim.Wait</c>, <c>CountdownEvent.Wait</c>,
/// <c>Barrier.SignalAndWait</c>, <c>Monitor.Wait</c>, <c>Thread.Join</c>, <c>Thread.Sleep</c>, <c>Thread.SpinWait</c>,
/// <c>SpinWait.SpinUntil</c> and <c>SpinOnce</c>, <c>BlockingCollection&lt;T&gt;.Take</c> and its timed
/// <c>TryTake</c>) and the engine members beneath the door that block (<c>ConfinedDirectory.AcquireLock</c>,
/// <c>FileLock.Acquire</c> and <c>FileLockWait.Acquire</c>).
/// </para>
/// <para>
/// Two shapes pass. A call whose timeout argument is the constant zero polls and does not block. A task's
/// <c>Result</c> or <c>GetResult</c> passes where the task is proven complete on the path: inside the true branch of an
/// <c>if</c> or conditional, or to the right of <c>&amp;&amp;</c>, whose condition tests the same expression's
/// <c>IsCompleted</c>, <c>IsCompletedSuccessfully</c>, <c>IsFaulted</c> or <c>IsCanceled</c> (directly or through
/// <c>&amp;&amp;</c>, never negated), or matches it with a property pattern such as <c>{ IsCompleted: true }</c>. The
/// proof compares expressions by their text, so a proof made in another method or after an early return is not seen.
/// </para>
/// The rule cannot see a wait reached through reflection, through a delegate bound in an assembly the rule does not
/// hold, or inside native code.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WaitAnalyzer : DiagnosticAnalyzer {
    /// <summary>The MSBuild property, surfaced to the analyzer as a compiler-visible property, that holds a project to
    /// the rule.</summary>
    public const string RuleProperty = "build_property.PuckWaitRule";

    private const string Category = "Puck.Waits";
    private const string DoorNamespace = "Puck.Abstractions.Waiting";
    private const string DoorType = "HostWaits";

    private static readonly ImmutableHashSet<string> CompletionTests = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "IsCompleted",
        "IsCompletedSuccessfully",
        "IsFaulted",
        "IsCanceled"
    );

    /// <summary>WAIT001: a thread blocks outside the wait door.</summary>
    public static readonly DiagnosticDescriptor Wait001BlocksOutsideTheDoor = new(
        id: "WAIT001",
        title: "A thread blocks outside HostWaits",
        messageFormat: "'{0}' blocks the thread outside HostWaits; wait through the door with a deadline and a reason (or as a queue's consumer), or prove the task complete on this path",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every wait on another subsystem's progress takes a deadline and a named reason through Puck.Abstractions.Waiting.HostWaits, so the host loop's control plane is read at least once a wait budget and an expiry is a value the waiter handles."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(item: Wait001BlocksOutsideTheDoor);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(action: start => {
            if (
                !start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(key: RuleProperty, value: out var held) ||
                !string.Equals(a: held?.Trim(), b: "true", comparisonType: StringComparison.OrdinalIgnoreCase)
            ) {
                return;
            }
            if (start.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.v3.IFactAttribute") is not null) {
                return;
            }

            start.RegisterOperationAction(action: AnalyzeInvocation, operationKinds: OperationKind.Invocation);
            start.RegisterOperationAction(action: AnalyzeMethodReference, operationKinds: OperationKind.MethodReference);
            start.RegisterOperationAction(action: AnalyzePropertyReference, operationKinds: OperationKind.PropertyReference);
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context) {
        var invocation = ((IInvocationOperation)context.Operation);
        var method = invocation.TargetMethod;

        if (!IsBlocking(method: method) || InsideTheDoor(symbol: context.ContainingSymbol) || PollsWithZeroTimeout(invocation: invocation)) {
            return;
        }
        if (IsAwaiterGetResult(method: method) && (invocation.Instance is IInvocationOperation { TargetMethod.Name: "GetAwaiter" } awaiter) && ProvenComplete(node: invocation.Syntax, receiver: awaiter.Instance)) {
            return;
        }

        Report(context: context, location: invocation.Syntax.GetLocation(), symbol: method);
    }
    private static void AnalyzeMethodReference(OperationAnalysisContext context) {
        var reference = ((IMethodReferenceOperation)context.Operation);

        if (IsBlocking(method: reference.Method) && !InsideTheDoor(symbol: context.ContainingSymbol)) {
            Report(context: context, location: reference.Syntax.GetLocation(), symbol: reference.Method);
        }
    }
    private static void AnalyzePropertyReference(OperationAnalysisContext context) {
        var reference = ((IPropertyReferenceOperation)context.Operation);
        var property = reference.Property;

        if (
            (property.Name != "Result") ||
            !(Is(type: property.ContainingType, @namespace: "System.Threading.Tasks", name: "Task") || Is(type: property.ContainingType, @namespace: "System.Threading.Tasks", name: "ValueTask")) ||
            InsideTheDoor(symbol: context.ContainingSymbol) ||
            ProvenComplete(node: reference.Syntax, receiver: reference.Instance)
        ) {
            return;
        }

        Report(context: context, location: reference.Syntax.GetLocation(), symbol: property);
    }
    private static void Report(OperationAnalysisContext context, Location location, ISymbol symbol) =>
        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Wait001BlocksOutsideTheDoor,
            location: location,
            messageArgs: $"{symbol.ContainingType?.Name}.{symbol.Name}"
        ));
    private static bool IsBlocking(IMethodSymbol method) {
        for (var current = method.OriginalDefinition; (current is not null); current = current.OverriddenMethod?.OriginalDefinition) {
            if (IsBlockingDefinition(method: current)) {
                return true;
            }
        }

        return false;
    }
    private static bool IsBlockingDefinition(IMethodSymbol method) {
        var type = method.ContainingType;
        var name = method.Name;

        if (IsAwaiterGetResult(method: method)) {
            return true;
        }

        return name switch {
            "Wait" => (
                Is(name: "Task", @namespace: "System.Threading.Tasks", type: type) ||
                Is(name: "ManualResetEventSlim", @namespace: "System.Threading", type: type) ||
                Is(name: "SemaphoreSlim", @namespace: "System.Threading", type: type) ||
                Is(name: "CountdownEvent", @namespace: "System.Threading", type: type) ||
                Is(name: "Monitor", @namespace: "System.Threading", type: type)
            ),
            "WaitAll" or "WaitAny" => (
                Is(name: "Task", @namespace: "System.Threading.Tasks", type: type) ||
                Is(name: "WaitHandle", @namespace: "System.Threading", type: type)
            ),
            "WaitOne" => Is(name: "WaitHandle", @namespace: "System.Threading", type: type),
            "SignalAndWait" => (
                Is(name: "WaitHandle", @namespace: "System.Threading", type: type) ||
                Is(name: "Barrier", @namespace: "System.Threading", type: type)
            ),
            "Join" or "Sleep" => Is(name: "Thread", @namespace: "System.Threading", type: type),
            "SpinWait" => Is(name: "Thread", @namespace: "System.Threading", type: type),
            "SpinUntil" or "SpinOnce" => Is(name: "SpinWait", @namespace: "System.Threading", type: type),
            "Take" => Is(name: "BlockingCollection", @namespace: "System.Collections.Concurrent", type: type),
            "TryTake" => (Is(name: "BlockingCollection", @namespace: "System.Collections.Concurrent", type: type) && (method.Parameters.Length > 1)),
            "AcquireLock" => Is(name: "ConfinedDirectory", @namespace: "Puck.Confinement", type: type),
            "Acquire" => (Is(name: "FileLockWait", @namespace: "Puck.Confinement", type: type) || Is(name: "FileLock", @namespace: "Puck.Abstractions", type: type)),
            _ => false,
        };
    }
    private static bool IsAwaiterGetResult(IMethodSymbol method) =>
        ((method.Name == "GetResult") &&
        (method.ContainingType is { } awaiter) &&
        awaiter.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: "Awaiter") &&
        (Outermost(type: awaiter).ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices"));
    private static INamedTypeSymbol Outermost(INamedTypeSymbol type) {
        while (type.ContainingType is { } outer) {
            type = outer;
        }

        return type;
    }
    private static bool InsideTheDoor(ISymbol? symbol) {
        for (var type = ((symbol as INamedTypeSymbol) ?? symbol?.ContainingType); (type is not null); type = type.ContainingType) {
            if (Is(name: DoorType, @namespace: DoorNamespace, type: type)) {
                return true;
            }
        }

        return false;
    }
    private static bool PollsWithZeroTimeout(IInvocationOperation invocation) {
        foreach (var argument in invocation.Arguments) {
            if ((argument.Parameter is not { } parameter) || (parameter.Name.IndexOf(comparisonType: StringComparison.Ordinal, value: "imeout") < 0)) {
                continue;
            }

            var value = argument.Value;

            while (value is IConversionOperation conversion) {
                value = conversion.Operand;
            }
            if ((value.ConstantValue is { HasValue: true, Value: int zero }) && (zero == 0)) {
                return true;
            }
            if ((value is IFieldReferenceOperation { Field.Name: "Zero" } field) && Is(type: field.Field.ContainingType, @namespace: "System", name: "TimeSpan")) {
                return true;
            }
        }

        return false;
    }
    // Whether `node` reads `receiver` only where a condition on its way up has proven `receiver` complete.
    private static bool ProvenComplete(SyntaxNode node, IOperation? receiver) {
        if (receiver is null) {
            return false;
        }

        var key = Key(node: receiver.Syntax);

        if (key.Length == 0) {
            return false;
        }

        var child = node;

        for (var parent = node.Parent; (parent is not null); child = parent, parent = parent.Parent) {
            var condition = parent switch {
                IfStatementSyntax ifStatement when (ifStatement.Statement == child) => ifStatement.Condition,
                ConditionalExpressionSyntax conditional when (conditional.WhenTrue == child) => conditional.Condition,
                BinaryExpressionSyntax binary when (binary.IsKind(kind: SyntaxKind.LogicalAndExpression) && (binary.Right == child)) => binary.Left,
                _ => ((ExpressionSyntax?)null),
            };

            if ((condition is not null) && Proves(condition: condition, key: key)) {
                return true;
            }
            if (parent is MemberDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) {
                return false;
            }
        }

        return false;
    }
    private static bool Proves(ExpressionSyntax condition, string key) {
        foreach (var descendant in condition.DescendantNodesAndSelf()) {
            switch (descendant) {
                case MemberAccessExpressionSyntax access when CompletionTests.Contains(item: access.Name.Identifier.ValueText):
                    if ((Key(node: access.Expression) == key) && ReachesConditionThroughAnd(condition: condition, node: access)) {
                        return true;
                    }
                    break;
                case RecursivePatternSyntax pattern when ((pattern.Designation is SingleVariableDesignationSyntax designation) && (designation.Identifier.ValueText == key)):
                    if (pattern.PropertyPatternClause is { } clause) {
                        foreach (var subpattern in clause.Subpatterns) {
                            if (
                                (subpattern.NameColon?.Name.Identifier.ValueText is { } name) &&
                                CompletionTests.Contains(item: name) &&
                                (subpattern.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal }) &&
                                literal.IsKind(kind: SyntaxKind.TrueLiteralExpression) &&
                                ReachesConditionThroughAnd(condition: condition, node: pattern)
                            ) {
                                return true;
                            }
                        }
                    }
                    break;
            }
        }

        return false;
    }
    // Whether a test sits under nothing but parentheses, `&&`, `is` and the pattern's own syntax on its way to the
    // condition: a test under `!` or `||` proves nothing about the true branch.
    private static bool ReachesConditionThroughAnd(SyntaxNode node, ExpressionSyntax condition) {
        for (var current = node; (current != condition); current = current.Parent!) {
            var parent = current.Parent;

            if (parent is null) {
                return false;
            }
            if (
                !(parent is ParenthesizedExpressionSyntax or IsPatternExpressionSyntax) &&
                !((parent is BinaryExpressionSyntax binary) && binary.IsKind(kind: SyntaxKind.LogicalAndExpression))
            ) {
                return false;
            }
        }

        return true;
    }
    private static string Key(SyntaxNode node) {
        var text = node.WithoutTrivia().ToString();
        var builder = new System.Text.StringBuilder(capacity: text.Length);

        foreach (var character in text) {
            if (!char.IsWhiteSpace(c: character)) {
                _ = builder.Append(value: character);
            }
        }

        return builder.ToString();
    }
}
