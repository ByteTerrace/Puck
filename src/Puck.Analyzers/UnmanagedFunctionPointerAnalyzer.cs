using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a call through an unmanaged function pointer whose signature, as the call site sees it, mentions a type
/// parameter (<see cref="Interop001GenericUnmanagedSignature"/>). Such a call compiles, but the runtime builds the
/// transition from the open signature and throws <c>System.Runtime.InteropServices.MarshalDirectiveException</c>
/// ("Non-blittable generic types cannot be marshaled") when the call runs.
/// <para>
/// <b>What counts as unmanaged.</b> Every calling convention other than the managed default: <c>delegate* unmanaged</c>
/// with or without a bracketed convention list. A managed <c>delegate*</c> never crosses a marshalling boundary and is
/// not inspected.
/// </para>
/// <para>
/// <b>What counts as a mention.</b> Each parameter, whatever its <c>in</c>/<c>ref</c>/<c>out</c> modifier, and the
/// return type are walked: a type parameter itself, a type parameter as an array element, and a type parameter as a
/// type argument of a named type or of any type containing it (<c>Span&lt;T&gt;</c>, <c>Outer&lt;T&gt;.Inner</c>).
/// </para>
/// <para>
/// <b>What is accepted.</b> A pointer, whatever it points at (<c>T*</c>, <c>Pair&lt;T&gt;*</c>), and a nested function
/// pointer are passed as a native address with no marshalling, so the walk stops at them. A closed generic type
/// (<c>Pair&lt;int&gt;</c>) names no type parameter. A generic signature that is only declared, stored, passed, or cast
/// is never marshalled: the runtime judges the signature a call is made through, so a generic entry point converted
/// to a pointer-typed view and called through that view is accepted, and so is a field declared in a generic type
/// and called where the type is closed.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnmanagedFunctionPointerAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Interop";

    /// <summary>INTEROP001: a call goes through an unmanaged function-pointer signature that mentions a type parameter.</summary>
    public static readonly DiagnosticDescriptor Interop001GenericUnmanagedSignature = new(
        id: "INTEROP001",
        title: "Unmanaged function-pointer call through a signature that mentions a type parameter",
        messageFormat: "Calling unmanaged function pointer '{0}' throws MarshalDirectiveException (\"Non-blittable generic types cannot be marshaled\"): type parameter '{1}' appears in {2} ('{3}'); call through a closed signature, or a view that passes the value by pointer ('{1}*')",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The runtime marshals an unmanaged function-pointer call from the signature the call is made through. A signature that mentions a type parameter anywhere other than behind a pointer compiles but is refused at the call with MarshalDirectiveException, so the build refuses the call instead."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(item: Interop001GenericUnmanagedSignature);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();

        // The runtime refuses a generated call exactly as it refuses a hand-written one.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterOperationAction(
            action: AnalyzeInvocation,
            operationKinds: OperationKind.FunctionPointerInvocation
        );
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context) {
        var invocation = ((IFunctionPointerInvocationOperation)context.Operation);
        var signature = invocation.GetFunctionPointerSignature();

        if (signature.CallingConvention == SignatureCallingConvention.Default) {
            return;
        }

        for (var index = 0; (index < signature.Parameters.Length); index++) {
            var parameter = signature.Parameters[index];

            if (Report(
                context: context,
                invocation: invocation,
                position: string.Format(
                    arg0: (index + 1),
                    format: "parameter {0}",
                    provider: CultureInfo.InvariantCulture
                ),
                refKind: parameter.RefKind,
                type: parameter.Type
            )) {
                return;
            }
        }

        _ = Report(
            context: context,
            invocation: invocation,
            position: "the return type",
            refKind: signature.RefKind,
            type: signature.ReturnType
        );
    }
    private static ITypeParameterSymbol? FindTypeParameter(ITypeSymbol type) {
        switch (type) {
            case ITypeParameterSymbol typeParameter:
                return typeParameter;
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
                return null;
            case IArrayTypeSymbol array:
                return FindTypeParameter(type: array.ElementType);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments) {
                    var found = FindTypeParameter(type: argument);

                    if (found is not null) {
                        return found;
                    }
                }

                return ((named.ContainingType is null)
                    ? null
                    : FindTypeParameter(type: named.ContainingType)
                );
            default:
                return null;
        }
    }
    private static string RefKindPrefix(RefKind refKind) =>
        refKind switch {
            RefKind.In => "in ",
            RefKind.Out => "out ",
            RefKind.Ref => "ref ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => string.Empty,
        };
    // A call is refused once, at its first position that mentions a type parameter.
    private static bool Report(OperationAnalysisContext context, IFunctionPointerInvocationOperation invocation, string position, RefKind refKind, ITypeSymbol type) {
        var typeParameter = FindTypeParameter(type: type);

        if (typeParameter is null) {
            return false;
        }

        var semanticModel = invocation.SemanticModel;
        var at = invocation.Syntax.SpanStart;
        var pointerText = ((semanticModel is null)
            ? invocation.Target.Type?.ToDisplayString()
            : invocation.Target.Type?.ToMinimalDisplayString(
                position: at,
                semanticModel: semanticModel
            )
        );
        var typeText = ((semanticModel is null)
            ? type.ToDisplayString()
            : type.ToMinimalDisplayString(
                position: at,
                semanticModel: semanticModel
            )
        );

        context.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: Interop001GenericUnmanagedSignature,
            location: invocation.Syntax.GetLocation(),
            messageArgs: [(pointerText ?? string.Empty), typeParameter.Name, position, (RefKindPrefix(refKind: refKind) + typeText)]
        ));

        return true;
    }
}
