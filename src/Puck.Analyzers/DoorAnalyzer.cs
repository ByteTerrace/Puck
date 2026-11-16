using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Puck.Analyzers;

/// <summary>
/// Holds what the compiler cannot about a state holder's two sides (<c>Puck.State.Versioned&lt;T&gt;</c>,
/// <c>VersionedArray&lt;T&gt;</c>, <c>VersionedRecord&lt;T&gt;</c>). The read side is a readonly reference, so the
/// compiler refuses an assignment, a <c>ref</c> or an <c>out</c> through it; the write side moves the holder's version
/// before it hands out a writable one. Each rule here reads one call or one declaration, so none has to recognise
/// every way a value can be written. A section is a type that implements <c>Puck.State.IVersionedSection</c>. A test
/// assembly (one that references xUnit v3) is held to none of them, and a compilation that cannot see the holder
/// types has nothing to hold.
/// <list type="bullet">
/// <item><see cref="Door001MutatingCallThroughReadSide"/>: a non-<c>readonly</c> instance method of a struct invoked
/// on a value reached through a readonly reference whose path is a holder's read side or passes through a section.
/// The compiler copies the struct and calls the method on the copy, so the write is lost.</item>
/// <item><see cref="Door002ReinterpretingCall"/>: a call to a member that turns a readonly reference into a writable
/// one (<c>Unsafe.AsRef</c>, <c>MemoryMarshal.GetReference</c>, <c>CreateSpan</c>, <c>GetArrayDataReference</c>,
/// <c>AsMemory</c>, <c>ImmutableCollectionsMarshal.AsArray</c> and <c>AsImmutableArray</c>). In a compilation that
/// declares the holder types or a section, every such call outside the holder types; in any other compilation that
/// can see the holder types, every such call with a type argument that is a section or an open type parameter. The
/// rule reads the member called and its type arguments, never where a value came from.</item>
/// <item><see cref="Door003MutableRecordSectionField"/>: a field of a <c>VersionedRecord&lt;T&gt;</c> section whose
/// type is not unmanaged, <c>string</c>, a readonly struct of such fields, or an immutable array of them.</item>
/// <item><see cref="Door005KeptWriteReference"/>: a <c>ref</c> local, a <c>ref</c> field or a <c>Span&lt;T&gt;</c> local
/// initialised from a holder's write side, which would keep a writable reference alive past its statement.</item>
/// </list>
/// The rules stop where the marker does: a nested value that is not a section and is reached through a readonly
/// reference with no section on its path (an <c>in</c> parameter of a plain struct) is outside DOOR001, and a
/// reinterpreting call in a compilation that cannot name a section is outside DOOR002.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DoorAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.Door";
    private const string StateNamespace = "Puck.State";

    /// <summary>DOOR001: a mutating struct method is invoked on a value reached through a holder's read side.</summary>
    public static readonly DiagnosticDescriptor Door001MutatingCallThroughReadSide = new(
        id: "DOOR001",
        title: "A mutating call through a holder's read side",
        messageFormat: "'{0}' is not readonly and is invoked on a value reached through a readonly reference to holder state; the compiler calls it on a copy and the write is lost, so reach the value through the holder's write side",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A non-readonly instance method invoked through a readonly reference runs on a defensive copy. Through a holder's read side that is a write the stored section never receives."
    );
    /// <summary>DOOR002: a member that defeats <c>readonly</c> is called where it could reach a section.</summary>
    public static readonly DiagnosticDescriptor Door002ReinterpretingCall = new(
        id: "DOOR002",
        title: "A reinterpreting call that can defeat a holder's read side",
        messageFormat: "'{0}' turns a readonly reference into a writable one, {1}; a holder's state is written only through its write side",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Unsafe.AsRef, MemoryMarshal.GetReference, CreateSpan, GetArrayDataReference and AsMemory, and ImmutableCollectionsMarshal.AsArray and AsImmutableArray exist to defeat readonly. A write through one moves no version."
    );
    /// <summary>DOOR003: a record-holder section declares a field that is not deeply immutable.</summary>
    public static readonly DiagnosticDescriptor Door003MutableRecordSectionField = new(
        id: "DOOR003",
        title: "A record-holder section field that is not immutable",
        messageFormat: "'{0}.{1}' is a '{2}', which a VersionedRecord section may not hold; its fields are unmanaged values, strings, readonly structs of those, or immutable arrays of them, so nothing reached through the read side can be written",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A VersionedRecord<T> section may carry references, which the compiler's readonly reference does not follow. Every field must be deeply immutable."
    );
    /// <summary>DOOR005: a writable reference from a holder's write side is kept in a local or a field.</summary>
    public static readonly DiagnosticDescriptor Door005KeptWriteReference = new(
        id: "DOOR005",
        title: "A writable reference to holder state is kept",
        messageFormat: "'{0}' keeps a writable reference from a holder's write side; use the write side within one statement or hand it to one call, so no write lands after a reader has run",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A write-side reference bound to a ref local, a ref field or a Span local outlives its statement and can be written after a fold or a derivation has read the section."
    );

    private static readonly ImmutableHashSet<string> UnsafeMembers = ImmutableHashSet.Create("AsRef");
    private static readonly ImmutableHashSet<string> MemoryMarshalMembers = ImmutableHashSet.Create("GetReference", "CreateSpan", "GetArrayDataReference", "AsMemory");
    private static readonly ImmutableHashSet<string> ImmutableMarshalMembers = ImmutableHashSet.Create("AsArray", "AsImmutableArray");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Door001MutatingCallThroughReadSide,
        Door002ReinterpretingCall,
        Door003MutableRecordSectionField,
        Door005KeptWriteReference
    );

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        // A generated statement loses a write or keeps a reference exactly as a hand-written one does.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(action: static start => {
            var compilation = start.Compilation;
            var holder = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Puck.State.VersionedHolder");

            if ((holder is null) || (compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Xunit.v3.IFactAttribute") is not null)) {
                return;
            }

            var declares = (
                SymbolEqualityComparer.Default.Equals(x: holder.ContainingAssembly, y: compilation.Assembly) ||
                DeclaresSection(@namespace: compilation.Assembly.GlobalNamespace)
            );

            start.RegisterOperationAction(
                action: operation => AnalyzeInvocation(context: operation, declares: declares),
                operationKinds: OperationKind.Invocation
            );
            start.RegisterOperationAction(
                action: AnalyzeDeclarator,
                operationKinds: OperationKind.VariableDeclarator
            );
            start.RegisterOperationAction(
                action: AnalyzeRefAssignment,
                operationKinds: OperationKind.SimpleAssignment
            );
            start.RegisterSymbolAction(
                action: AnalyzeField,
                symbolKinds: SymbolKind.Field
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
    private static bool IsHolder(ITypeSymbol? type) {
        for (var current = type; (current is not null); current = current.BaseType) {
            if (Is(type: current, @namespace: StateNamespace, name: "VersionedHolder")) {
                return true;
            }
        }

        return false;
    }
    private static bool IsSection(ITypeSymbol? type) {
        if (type is null) {
            return false;
        }

        foreach (var contract in type.AllInterfaces) {
            if (Is(type: contract, @namespace: StateNamespace, name: "IVersionedSection")) {
                return true;
            }
        }

        return false;
    }
    private static bool IsSpan(ITypeSymbol? type, bool writable) =>
        Is(type: type, @namespace: "System", name: (writable ? "Span" : "ReadOnlySpan"));
    private static bool DeclaresSection(INamespaceSymbol @namespace) {
        foreach (var member in @namespace.GetMembers()) {
            if (member is INamespaceSymbol nested) {
                if (DeclaresSection(@namespace: nested)) {
                    return true;
                }
            } else if ((member is INamedTypeSymbol type) && DeclaresSection(type: type)) {
                return true;
            }
        }

        return false;
    }
    private static bool DeclaresSection(INamedTypeSymbol type) {
        if (IsSection(type: type)) {
            return true;
        }

        foreach (var nested in type.GetTypeMembers()) {
            if (DeclaresSection(type: nested)) {
                return true;
            }
        }

        return false;
    }

    // The path from an expression down to the storage it names: through struct fields, inline-array and span elements
    // and conversions, to the member, local or parameter the reference came from. Answers that root and whether any
    // value on the path is a section.
    private static IOperation Root(IOperation operation, out bool passesSection) {
        passesSection = false;
        while (true) {
            passesSection |= IsSection(type: operation.Type);
            switch (operation) {
                case IConversionOperation conversion when conversion.IsImplicit || conversion.Conversion.IsIdentity:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                case IFieldReferenceOperation { Instance: { } instance, Field.ContainingType.IsValueType: true }:
                    operation = instance;
                    continue;
                case IInlineArrayAccessOperation inline:
                    operation = inline.Instance;
                    continue;
                case IPropertyReferenceOperation { Instance: { } span, Property.IsIndexer: true } when IsSpan(type: span.Type, writable: true) || IsSpan(type: span.Type, writable: false):
                    operation = span;
                    continue;
                case IInvocationOperation { Instance: { } sliced } when IsSpan(type: sliced.Type, writable: true) || IsSpan(type: sliced.Type, writable: false):
                    operation = sliced;
                    continue;
                default:
                    return operation;
            }
        }
    }
    private static bool IsHolderMember(IOperation root, string name) => root switch {
        IPropertyReferenceOperation property => ((property.Property.Name == name) && IsHolder(type: property.Property.ContainingType)),
        IInvocationOperation invocation => ((invocation.TargetMethod.Name == name) && IsHolder(type: invocation.TargetMethod.ContainingType)),
        _ => false,
    };
    // A readonly reference: one the compiler will not write through, and through which a mutating call runs on a copy.
    private static bool IsReadOnlyReference(IOperation root) => root switch {
        IPropertyReferenceOperation property => ((property.Property.RefKind == RefKind.RefReadOnly) || IsSpan(type: property.Type, writable: false)),
        IInvocationOperation invocation => ((invocation.TargetMethod.RefKind == RefKind.RefReadOnly) || IsSpan(type: invocation.Type, writable: false)),
        ILocalReferenceOperation local => ((local.Local.RefKind == RefKind.RefReadOnly) || IsSpan(type: local.Type, writable: false)),
        IParameterReferenceOperation parameter => ((parameter.Parameter.RefKind == RefKind.In) || (parameter.Parameter.RefKind == RefKind.RefReadOnlyParameter) || IsSpan(type: parameter.Type, writable: false)),
        _ => false,
    };
    // A holder's write side, or a member that hands it on: one that returns a writable reference or span to a section.
    private static bool IsWriteSide(IOperation root, bool passesSection) {
        if (IsHolderMember(root: root, name: "Write") || IsHolderMember(root: root, name: "Resize")) {
            return true;
        }

        return root switch {
            IPropertyReferenceOperation property => (passesSection && ((property.Property.RefKind == RefKind.Ref) || IsSpan(type: property.Type, writable: true))),
            IInvocationOperation invocation => (passesSection && ((invocation.TargetMethod.RefKind == RefKind.Ref) || IsSpan(type: invocation.Type, writable: true))),
            _ => false,
        };
    }
    private static bool SpanOfSection(ITypeSymbol? type) =>
        (IsSpan(type: type, writable: true) && (type is INamedTypeSymbol { TypeArguments.Length: 1 } span) && IsSection(type: span.TypeArguments[0]));

    private static void AnalyzeInvocation(OperationAnalysisContext context, bool declares) {
        var invocation = ((IInvocationOperation)context.Operation);
        var method = invocation.TargetMethod;

        if (
            (invocation.Instance is { } receiver) &&
            (method.MethodKind == MethodKind.Ordinary) &&
            method.ContainingType.IsValueType &&
            !method.IsReadOnly
        ) {
            var root = Root(operation: receiver, passesSection: out var passesSection);

            if (IsReadOnlyReference(root: root) && (passesSection || IsHolderMember(root: root, name: "Read"))) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                    descriptor: Door001MutatingCallThroughReadSide,
                    location: invocation.Syntax.GetLocation(),
                    messageArgs: [method.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat)]
                ));
            }
        }

        if (!method.IsStatic || !IsReinterpreting(method: method)) {
            return;
        }
        if (declares) {
            // The holder types reinterpret their own storage; nothing else in a declaring compilation may.
            if (!IsHolder(type: context.ContainingSymbol.ContainingType)) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                    descriptor: Door002ReinterpretingCall,
                    location: invocation.Syntax.GetLocation(),
                    messageArgs: [method.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat), "and this project declares holder state"]
                ));
            }

            return;
        }

        foreach (var argument in method.TypeArguments) {
            if ((argument.TypeKind == TypeKind.TypeParameter) || IsSection(type: argument)) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                    descriptor: Door002ReinterpretingCall,
                    location: invocation.Syntax.GetLocation(),
                    messageArgs: [
                        method.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                        ((argument.TypeKind == TypeKind.TypeParameter) ? $"and '{argument.Name}' is an open type parameter a section can fill" : $"and '{argument.Name}' is a section"),
                    ]
                ));

                return;
            }
        }
    }
    private static bool IsReinterpreting(IMethodSymbol method) {
        var type = method.ContainingType;

        return (
            (Is(type: type, @namespace: "System.Runtime.CompilerServices", name: "Unsafe") && UnsafeMembers.Contains(item: method.Name)) ||
            (Is(type: type, @namespace: "System.Runtime.InteropServices", name: "MemoryMarshal") && MemoryMarshalMembers.Contains(item: method.Name)) ||
            (Is(type: type, @namespace: "System.Runtime.InteropServices", name: "ImmutableCollectionsMarshal") && ImmutableMarshalMembers.Contains(item: method.Name))
        );
    }
    private static void AnalyzeDeclarator(OperationAnalysisContext context) {
        var declarator = ((IVariableDeclaratorOperation)context.Operation);
        var local = declarator.Symbol;
        var initializer = (declarator.Initializer ?? ((declarator.Parent as IVariableDeclarationOperation)?.Initializer));

        if (initializer is null) {
            return;
        }

        var keepsReference = (local.RefKind == RefKind.Ref);
        var keepsSpan = IsSpan(type: local.Type, writable: true);

        if (!keepsReference && !keepsSpan) {
            return;
        }

        var root = Root(operation: initializer.Value, passesSection: out var passesSection);

        if (IsWriteSide(root: root, passesSection: (passesSection || (keepsSpan && SpanOfSection(type: local.Type))))) {
            context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Door005KeptWriteReference,
                location: declarator.Syntax.GetLocation(),
                messageArgs: [local.Name]
            ));
        }
    }
    private static void AnalyzeRefAssignment(OperationAnalysisContext context) {
        var assignment = ((ISimpleAssignmentOperation)context.Operation);

        if (!assignment.IsRef) {
            return;
        }

        var root = Root(operation: assignment.Value, passesSection: out var passesSection);

        if (IsWriteSide(root: root, passesSection: passesSection)) {
            context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Door005KeptWriteReference,
                location: assignment.Syntax.GetLocation(),
                messageArgs: [assignment.Target.Syntax.ToString()]
            ));
        }
    }
    private static void AnalyzeField(SymbolAnalysisContext context) {
        var field = ((IFieldSymbol)context.Symbol);

        if (!Is(type: field.Type, @namespace: StateNamespace, name: "VersionedRecord") || (field.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } record)) {
            return;
        }

        var section = record.TypeArguments[0];

        foreach (var member in section.GetMembers()) {
            if ((member is IFieldSymbol { IsStatic: false } stored) && !IsImmutable(type: stored.Type, visiting: new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(
                    descriptor: Door003MutableRecordSectionField,
                    location: (field.Locations.IsEmpty ? Location.None : field.Locations[0]),
                    messageArgs: [section.Name, (stored.AssociatedSymbol?.Name ?? stored.Name), stored.Type.ToDisplayString(format: SymbolDisplayFormat.CSharpShortErrorMessageFormat)]
                ));
            }
        }
    }
    private static bool IsImmutable(ITypeSymbol type, HashSet<ITypeSymbol> visiting) {
        if (type.IsUnmanagedType || (type.SpecialType == SpecialType.System_String)) {
            return true;
        }
        if (type is not INamedTypeSymbol named) {
            return false;
        }
        if (Is(type: named, @namespace: "System", name: "Nullable") || Is(type: named, @namespace: "System.Collections.Immutable", name: "ImmutableArray")) {
            return ((named.TypeArguments.Length == 1) && IsImmutable(type: named.TypeArguments[0], visiting: visiting));
        }
        if (!named.IsValueType || !named.IsReadOnly) {
            return false;
        }
        // A type met again is one already being judged or already judged: its own verdict stands.
        if (!visiting.Add(item: named)) {
            return true;
        }

        foreach (var member in named.GetMembers()) {
            if ((member is IFieldSymbol { IsStatic: false } stored) && !IsImmutable(type: stored.Type, visiting: visiting)) {
                return false;
            }
        }

        return true;
    }
}
