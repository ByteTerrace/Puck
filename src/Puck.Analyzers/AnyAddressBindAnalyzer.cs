using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a listener bound to every network interface outside the deployment sites
/// <see cref="AnyAddressBindAllowlist"/> names (<see cref="Net001AnyAddressBind"/>). A listener on every interface
/// raises a Windows Firewall prompt on every local run that starts it, and a local World, silo, test or canary has no
/// reason to accept traffic from another machine. Every listener takes its address from configuration and defaults
/// to loopback; only a deployment document names the any-address.
/// <para>
/// <b>What counts as an any-address bind.</b> A reference to <c>IPAddress.Any</c>, <c>IPAddress.IPv6Any</c> or
/// <c>IPAddress.IPv6None</c>; a call to Kestrel's <c>ListenAnyIP</c> or to <c>TcpListener.Create</c>; a
/// <c>TcpListener</c> or <c>UdpClient</c> constructed from a port alone; an <c>IPAddress</c> or <c>IPEndPoint</c>
/// constructed from the constant address 0; an Orleans <c>listenOnAnyHostAddress: true</c>; and a string literal
/// spelling the any-address: <c>0.0.0.0</c>, <c>[::]</c>, a bare <c>::</c>, or a URL whose host is the <c>*</c> or
/// <c>+</c> wildcard. A literal is refused wherever it appears, because a string that names the any-address is
/// configuration that will be bound somewhere; an assertion about a refusal is allowlisted like any other site.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AnyAddressBindAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Network";

    /// <summary>NET001: a listener bound to every network interface outside the allowlisted deployment sites.</summary>
    public static readonly DiagnosticDescriptor Net001AnyAddressBind = new(
        id: "NET001",
        title: "Listener bound to every network interface",
        messageFormat: "{0} binds every network interface, which raises a firewall prompt on every local run; take the listen address from configuration and default it to loopback (127.0.0.1, or ::1 where the code is IPv6-first), or add a deployment-configuration site to AnyAddressBindAllowlist with its reason",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Puck listens on loopback unless a deployment document says otherwise. Container and Azure configurations name the any-address explicitly, each site listed with its reason in one allowlist; every other any-address bind, wildcard URL prefix or any-address literal is refused."
    );

    private static readonly string[] WildcardHosts = ["://*:", "://+:", "://*/", "://+/"];

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(item: Net001AnyAddressBind);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        // A generated listener binds exactly as a hand-written one does.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterOperationAction(
            action: AnalyzeOperation,
            operationKinds: ImmutableArray.Create(item1: OperationKind.FieldReference, item2: OperationKind.Invocation, item3: OperationKind.ObjectCreation)
        );
        // Literals are read from syntax, so one in an attribute argument, a constant or a parameter default is seen
        // as surely as one in a method body.
        context.RegisterSyntaxNodeAction(
            action: AnalyzeLiteral,
            syntaxKinds: ImmutableArray.Create(item1: SyntaxKind.StringLiteralExpression, item2: SyntaxKind.Utf8StringLiteralExpression, item3: SyntaxKind.InterpolatedStringText)
        );
    }
    /// <summary>Returns whether a string spells the any-address: <c>0.0.0.0</c> standing alone as an address,
    /// <c>[::]</c>, a bare <c>::</c>, or a URL whose host is the <c>*</c> or <c>+</c> wildcard.</summary>
    /// <param name="text">The string to inspect.</param>
    /// <param name="whole"><see langword="true"/> when <paramref name="text"/> is a whole literal; a fragment between
    /// an interpolated string's holes is never a bare <c>::</c> address, only a separator.</param>
    /// <returns><see langword="true"/> when the string names the any-address.</returns>
    public static bool SpellsAnyAddress(string text, bool whole = true) {
        if (
            (whole && string.Equals(
                a: text.Trim(),
                b: "::",
                comparisonType: StringComparison.Ordinal
            )) ||
            (text.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: "[::]"
            ) >= 0)
        ) {
            return true;
        }
        foreach (var wildcard in WildcardHosts) {
            if (text.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: wildcard
            ) >= 0) {
                return true;
            }
        }

        const string Unspecified = "0.0.0.0";
        var start = text.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: Unspecified
        );

        while (start >= 0) {
            // A longer address that merely contains the text, such as 10.0.0.0, names a different address.
            var end = (start + Unspecified.Length);

            if (
                ((start == 0) || !IsAddressCharacter(character: text[(start - 1)])) &&
                ((end == text.Length) || !IsAddressCharacter(character: text[end]))
            ) {
                return true;
            }
            start = text.IndexOf(
                comparisonType: StringComparison.Ordinal,
                startIndex: (start + 1),
                value: Unspecified
            );
        }

        return false;
    }

    private static void AnalyzeLiteral(SyntaxNodeAnalysisContext context) {
        var text = (context.Node switch {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            InterpolatedStringTextSyntax fragment => fragment.TextToken.ValueText,
            _ => null,
        });

        if (
            (text is null) ||
            !SpellsAnyAddress(
                text: text,
                whole: (context.Node is LiteralExpressionSyntax)
            )
        ) {
            return;
        }

        Report(
            assembly: context.Compilation.AssemblyName,
            containingSymbol: context.ContainingSymbol,
            refusal: $"the literal \"{text}\"",
            report: context.ReportDiagnostic,
            syntax: context.Node
        );
    }
    private static void AnalyzeOperation(OperationAnalysisContext context) {
        var refusal = (context.Operation switch {
            IFieldReferenceOperation field => RefuseField(field: field.Field),
            IInvocationOperation invocation => RefuseInvocation(invocation: invocation),
            IObjectCreationOperation creation => RefuseCreation(creation: creation),
            _ => null,
        });

        if (refusal is null) {
            return;
        }

        Report(
            assembly: context.Compilation.AssemblyName,
            containingSymbol: context.ContainingSymbol,
            refusal: refusal,
            report: context.ReportDiagnostic,
            syntax: context.Operation.Syntax
        );
    }
    private static void Report(string? assembly, ISymbol? containingSymbol, string refusal, Action<Diagnostic> report, SyntaxNode syntax) {
        // A lambda or a local function sits inside the member an allowlist entry names.
        var member = containingSymbol;

        while (member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }) {
            member = member.ContainingSymbol;
        }
        if (AnyAddressBindAllowlist.Admits(
            assembly: (assembly ?? string.Empty),
            member: (member?.Name ?? string.Empty),
            path: syntax.SyntaxTree.FilePath
        )) {
            return;
        }

        report(obj: Diagnostic.Create(
            descriptor: Net001AnyAddressBind,
            location: syntax.GetLocation(),
            messageArgs: [refusal]
        ));
    }
    private static bool IsAddressCharacter(char character) => ((character == '.') || ((character >= '0') && (character <= '9')));
    private static bool IsConstantZero(IArgumentOperation? argument) => ((argument?.Value.ConstantValue is { HasValue: true, Value: long or int } constant) && (Convert.ToInt64(value: constant.Value) == 0));
    private static bool IsType(ITypeSymbol? type, string containingNamespace, string name) => (
        (type is not null) &&
        string.Equals(
            a: type.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ) &&
        string.Equals(
            a: type.ContainingNamespace?.ToDisplayString(),
            b: containingNamespace,
            comparisonType: StringComparison.Ordinal
        )
    );
    private static string? RefuseCreation(IObjectCreationOperation creation) {
        var constructor = creation.Constructor;

        if (constructor is null) {
            return null;
        }

        var type = constructor.ContainingType;
        var parameters = constructor.Parameters;

        if (
            IsType(containingNamespace: "System.Net.Sockets", name: "TcpListener", type: type) &&
            (parameters.Length == 1) &&
            (parameters[0].Type.SpecialType == SpecialType.System_Int32)
        ) {
            return "new TcpListener(port)";
        }
        if (
            IsType(containingNamespace: "System.Net.Sockets", name: "UdpClient", type: type) &&
            (parameters.Length >= 1) &&
            (parameters[0].Type.SpecialType == SpecialType.System_Int32) &&
            ((parameters.Length == 1) || IsType(containingNamespace: "System.Net.Sockets", name: "AddressFamily", type: parameters[1].Type))
        ) {
            return "new UdpClient(port)";
        }
        if (
            (IsType(containingNamespace: "System.Net", name: "IPAddress", type: type) || IsType(containingNamespace: "System.Net", name: "IPEndPoint", type: type)) &&
            (parameters.Length >= 1) &&
            (parameters[0].Type.SpecialType == SpecialType.System_Int64) &&
            IsConstantZero(argument: creation.Arguments.FirstOrDefault(predicate: static argument => (argument.Parameter?.Ordinal == 0)))
        ) {
            return $"new {type.Name}(0)";
        }

        return null;
    }
    private static string? RefuseField(IFieldSymbol field) => (
        (IsType(containingNamespace: "System.Net", name: "IPAddress", type: field.ContainingType) && (field.Name is "Any" or "IPv6Any" or "IPv6None"))
            ? $"IPAddress.{field.Name}"
            : null
    );
    private static string? RefuseInvocation(IInvocationOperation invocation) {
        var method = invocation.TargetMethod;

        if (string.Equals(
            a: method.Name,
            b: "ListenAnyIP",
            comparisonType: StringComparison.Ordinal
        )) {
            return "ListenAnyIP";
        }
        if (
            IsType(containingNamespace: "System.Net.Sockets", name: "TcpListener", type: method.ContainingType) &&
            string.Equals(
                a: method.Name,
                b: "Create",
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return "TcpListener.Create";
        }
        foreach (var argument in invocation.Arguments) {
            if (
                string.Equals(
                    a: argument.Parameter?.Name,
                    b: "listenOnAnyHostAddress",
                    comparisonType: StringComparison.Ordinal
                ) &&
                (argument.Value.ConstantValue is { HasValue: true, Value: true })
            ) {
                return "listenOnAnyHostAddress: true";
            }
        }

        return null;
    }
}
