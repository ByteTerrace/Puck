using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// Walks the serialization graph one <c>JsonSerializerContext</c> declares — its <c>[JsonSerializable]</c> roots,
/// every reachable property (skipping members System.Text.Json itself would never serialize), every
/// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> family member, and every collection element — reporting
/// <see cref="StrictEnumAnalyzer.Enum001EnumNotExplicitlyConverted"/> for an enum it reaches with no explicit
/// converter and <see cref="StrictEnumAnalyzer.Enum002UnclassifiableJsonShape"/> for a member shape it cannot
/// classify. One instance is built per context type per compilation (<see cref="StrictEnumAnalyzer"/>) and its
/// <see cref="m_visited"/> set is shared across every root that context declares, so a type reachable from two roots
/// is only ever walked (and, if it is a violating enum, only ever reported) once.
/// </summary>
internal sealed class StrictEnumReachability {
    private readonly INamedTypeSymbol m_context;
    private readonly StrictEnumKnownTypes m_knownTypes;
    private readonly HashSet<ITypeSymbol> m_registeredConverterTargets;
    private readonly SymbolAnalysisContext m_symbolContext;
    private readonly HashSet<ITypeSymbol> m_visited = new(comparer: SymbolEqualityComparer.Default);

    /// <summary>Initializes a new instance of the <see cref="StrictEnumReachability"/> class for one <c>JsonSerializerContext</c> declaration.</summary>
    /// <param name="context">The <c>JsonSerializerContext</c>-derived type whose roots are being walked.</param>
    /// <param name="knownTypes">The resolved System.Text.Json marker types for this compilation.</param>
    /// <param name="registeredConverterTargets">The exact types <paramref name="context"/>'s <c>[JsonSourceGenerationOptions(Converters = ...)]</c> array registers a converter for.</param>
    /// <param name="symbolContext">The analysis context diagnostics are reported through.</param>
    public StrictEnumReachability(INamedTypeSymbol context, StrictEnumKnownTypes knownTypes, HashSet<ITypeSymbol> registeredConverterTargets, SymbolAnalysisContext symbolContext) {
        m_context = context;
        m_knownTypes = knownTypes;
        m_registeredConverterTargets = registeredConverterTargets;
        m_symbolContext = symbolContext;
    }

    /// <summary>Walks one <c>[JsonSerializable]</c> root and everything reachable from it.</summary>
    /// <param name="type">The root type.</param>
    public void Walk(ITypeSymbol type) =>
        WalkMember(type: type, ownerDisplay: m_context.Name, memberDisplay: "(root)", location: ContextLocation());

    /// <summary>Every <c>typeof(...)</c> argument of a <c>[JsonSerializable(typeof(X))]</c> attribute on <paramref name="context"/>.</summary>
    public static IReadOnlyList<ITypeSymbol> CollectSerializableRoots(INamedTypeSymbol context, StrictEnumKnownTypes knownTypes) {
        if (knownTypes.JsonSerializableAttributeType is null) {
            return [];
        }

        var roots = new List<ITypeSymbol>();

        foreach (var attribute in context.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: knownTypes.JsonSerializableAttributeType)) {
                continue;
            }

            if ((attribute.ConstructorArguments.Length > 0) && (attribute.ConstructorArguments[0].Value is ITypeSymbol root)) {
                roots.Add(item: root);
            }
        }

        return roots;
    }

    /// <summary>
    /// Every type <paramref name="context"/>'s <c>[JsonSourceGenerationOptions(Converters = ...)]</c> array names a
    /// converter for, recovered by walking each listed converter class's base-type chain to the closed
    /// <c>JsonConverter&lt;T&gt;</c> it derives from and reading off <c>T</c>. This is what lets
    /// <c>StrictEnumConverter&lt;CommandPhase&gt;</c> (registered here because that enum lives in a project that
    /// cannot reference <c>Puck.Abstractions</c>) and the bespoke
    /// <c>WorldBackendPreferenceJsonConverter</c>/<c>SurfaceFormatJsonConverter</c>/<c>GrantSubjectJsonConverter</c>/
    /// <c>WorldPrincipalJsonConverter</c>/<c>Vector3JsonConverter</c>/document-bridge converters all count as
    /// "explicitly converted" without this analyzer special-casing any of their names.
    /// </summary>
    public static HashSet<ITypeSymbol> CollectRegisteredConverters(INamedTypeSymbol context, StrictEnumKnownTypes knownTypes) {
        var targets = new HashSet<ITypeSymbol>(comparer: SymbolEqualityComparer.Default);

        if ((knownTypes.JsonSourceGenerationOptionsAttributeType is null) || (knownTypes.JsonConverterOpenGenericType is null)) {
            return targets;
        }

        foreach (var attribute in context.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: knownTypes.JsonSourceGenerationOptionsAttributeType)) {
                continue;
            }

            foreach (var namedArgument in attribute.NamedArguments) {
                if (!string.Equals(a: namedArgument.Key, b: "Converters", comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                foreach (var element in namedArgument.Value.Values) {
                    if ((element.Value is ITypeSymbol converterType) && (ConvertedType(converterType: converterType, knownTypes: knownTypes) is { } converted)) {
                        targets.Add(item: converted);
                    }
                }
            }
        }

        return targets;
    }

    /// <summary>Whether <paramref name="type"/> derives (directly or transitively) from <paramref name="jsonSerializerContextType"/>.</summary>
    public static bool DerivesFromJsonSerializerContext(INamedTypeSymbol type, INamedTypeSymbol jsonSerializerContextType) {
        for (var current = type.BaseType; (current is not null); current = current.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(x: current, y: jsonSerializerContextType)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks up <paramref name="converterType"/>'s base-type chain looking for either a closed
    /// <c>JsonConverter&lt;T&gt;</c> (an ordinary bespoke converter) or a closed
    /// <c>JsonStringEnumConverter&lt;TEnum&gt;</c> (the BCL factory
    /// <c>Puck.Abstractions.Documents.StrictEnumConverter&lt;TEnum&gt;</c> itself
    /// derives from — see <see cref="StrictEnumKnownTypes.JsonStringEnumConverterOpenGenericType"/> for why a
    /// factory counts here), returning the type argument that closes whichever is found first. Returns
    /// <see langword="null"/> when neither is ever reached (an unrelated converter factory, for instance — none
    /// besides the enum one are registered in this repository's strict contexts).
    /// </summary>
    private static ITypeSymbol? ConvertedType(ITypeSymbol converterType, StrictEnumKnownTypes knownTypes) {
        for (var current = (converterType as INamedTypeSymbol); (current is not null); current = current.BaseType) {
            if (!current.IsGenericType || (current.TypeArguments.Length != 1)) {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(x: current.OriginalDefinition, y: knownTypes.JsonConverterOpenGenericType)
                || SymbolEqualityComparer.Default.Equals(x: current.OriginalDefinition, y: knownTypes.JsonStringEnumConverterOpenGenericType)) {
                return current.TypeArguments[0];
            }
        }

        return null;
    }
    private Location ContextLocation() =>
        (m_context.Locations.FirstOrDefault(predicate: location => location.IsInSource) ?? Location.None);
    private void WalkMember(ITypeSymbol? type, string ownerDisplay, string memberDisplay, Location location) {
        if (type is null) {
            return;
        }

        // Nullable<T>: a value-type "?" annotation is a distinct wrapper type; a reference-type "?" is the SAME
        // symbol with only its NullableAnnotation changed, so it falls through to the checks below unchanged.
        if ((type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)) {
            WalkMember(type: nullable.TypeArguments[0], ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location);

            return;
        }

        if (type is IArrayTypeSymbol array) {
            WalkMember(type: array.ElementType, ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location);

            return;
        }

        if (type.SpecialType == SpecialType.System_Object) {
            Report002(ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location, reason: "its declared type is System.Object, which could hide any runtime type including an unconverted enum");

            return;
        }

        if (IsPrimitiveLeaf(type: type) || StrictEnumKnownTypes.IsKnownLeaf(type: type)) {
            return;
        }

        if (TryGetEnumerableElementType(type: type, elementType: out var elementType)) {
            WalkMember(type: elementType, ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location);

            return;
        }

        if (type.TypeKind == TypeKind.Enum) {
            WalkEnum(enumType: (INamedTypeSymbol)type, ownerDisplay: ownerDisplay, memberDisplay: memberDisplay);

            return;
        }

        if (type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface) {
            WalkClassLike(type: type, ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location);

            return;
        }

        Report002(ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location, reason: $"its declared type '{type}' ({type.TypeKind}) is neither a class, a struct, an interface, nor an enum");
    }
    private void WalkEnum(INamedTypeSymbol enumType, string ownerDisplay, string memberDisplay) {
        if (!m_visited.Add(item: enumType)) {
            return;
        }

        if (IsExplicitlyConverted(type: enumType)) {
            return;
        }

        var location = (enumType.Locations.FirstOrDefault(predicate: candidate => candidate.IsInSource) ?? Location.None);

        m_symbolContext.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: StrictEnumAnalyzer.Enum001EnumNotExplicitlyConverted,
            location: location,
            messageArgs: [enumType.ToDisplayString(), m_context.Name, ownerDisplay, memberDisplay]));
    }
    private void WalkClassLike(ITypeSymbol type, string ownerDisplay, string memberDisplay, Location location) {
        if (!m_visited.Add(item: type)) {
            return;
        }

        if (IsExplicitlyConverted(type: type)) {
            // The whole value rides a converter that never touches System.Text.Json's own property serialization,
            // so nothing under this type is reachable through property serialization at all — this is what excuses
            // GrantSubjectKind/PrincipalKind (nested inside GrantSubject/WorldPrincipal) and the Puck.Forge.Authoring
            // document family (bridged through CreationDocumentJsonConverter and its siblings) with no allowlist.
            return;
        }

        var derivedTypes = GetJsonDerivedTypes(type: type);

        if ((type.TypeKind == TypeKind.Interface) && (derivedTypes.Count == 0)) {
            Report002(ownerDisplay: ownerDisplay, memberDisplay: memberDisplay, location: location, reason: $"'{type}' is an interface with no [JsonPolymorphic]/[JsonDerivedType] family and no converter of its own");

            return;
        }

        foreach (var property in GetInstanceProperties(type: type)) {
            if (IsAlwaysIgnored(property: property)) {
                // A bare [JsonIgnore] (or an explicit Condition: JsonIgnoreCondition.Always) removes this member from
                // serialization entirely.
                continue;
            }

            var propertyLocation = (property.Locations.FirstOrDefault(predicate: candidate => candidate.IsInSource) ?? location);

            WalkMember(type: property.Type, ownerDisplay: type.Name, memberDisplay: property.Name, location: propertyLocation);
        }

        foreach (var derived in derivedTypes) {
            WalkMember(type: derived, ownerDisplay: type.Name, memberDisplay: $"$type:{derived.Name}", location: location);
        }
    }
    private void Report002(string ownerDisplay, string memberDisplay, Location location, string reason) {
        m_symbolContext.ReportDiagnostic(diagnostic: Diagnostic.Create(
            descriptor: StrictEnumAnalyzer.Enum002UnclassifiableJsonShape,
            location: location,
            messageArgs: [ownerDisplay, memberDisplay, reason]));
    }
    private bool IsExplicitlyConverted(ITypeSymbol type) {
        if (m_registeredConverterTargets.Contains(item: type)) {
            return true;
        }

        if (m_knownTypes.JsonConverterAttributeType is null) {
            return false;
        }

        foreach (var attribute in type.GetAttributes()) {
            if (SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: m_knownTypes.JsonConverterAttributeType)) {
                return true;
            }
        }

        return false;
    }
    private bool IsAlwaysIgnored(IPropertySymbol property) {
        if (m_knownTypes.JsonIgnoreAttributeType is null) {
            return false;
        }

        foreach (var attribute in property.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: m_knownTypes.JsonIgnoreAttributeType)) {
                continue;
            }

            foreach (var namedArgument in attribute.NamedArguments) {
                if (string.Equals(a: namedArgument.Key, b: "Condition", StringComparison.Ordinal)) {
                    // JsonIgnoreCondition.Always == 0; any other named value (Never/WhenWritingDefault/WhenWritingNull)
                    // still lets the member reach the wire under some condition, so it must still be walked.
                    return ((namedArgument.Value.Value is int conditionValue) && (conditionValue == 0));
                }
            }

            // No Condition named argument at all: JsonIgnoreAttribute's default Condition is Always.
            return true;
        }

        return false;
    }
    private List<ITypeSymbol> GetJsonDerivedTypes(ITypeSymbol type) {
        var derived = new List<ITypeSymbol>();

        if (m_knownTypes.JsonDerivedTypeAttributeType is null) {
            return derived;
        }

        foreach (var attribute in type.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(x: attribute.AttributeClass, y: m_knownTypes.JsonDerivedTypeAttributeType)) {
                continue;
            }

            if ((attribute.ConstructorArguments.Length > 0) && (attribute.ConstructorArguments[0].Value is ITypeSymbol derivedType)) {
                derived.Add(item: derivedType);
            }
        }

        return derived;
    }

    /// <summary>
    /// Every public, non-static, gettable instance property declared on <paramref name="type"/> or inherited from a
    /// base type, stopping at <see cref="object"/> (a struct's one extra stop at <see cref="ValueType"/> along the
    /// way declares nothing relevant). Restricted to <see cref="Accessibility.Public"/> rather than "anything with a
    /// getter" because every C# record synthesizes a <c>protected virtual System.Type EqualityContract</c> property
    /// that System.Text.Json's serializer never touches (it is not public, and nothing here marks it
    /// <c>[JsonInclude]</c>) but that this walk, unfiltered, followed straight into System.Type's own enormous
    /// reflection surface (System.Reflection.MemberInfo/MethodBase/Assembly and their many unconverted enums) —
    /// caught by the falsification pass this gate's own doctrine requires (see <see cref="StrictEnumAnalyzer"/>).
    /// </summary>
    private static List<IPropertySymbol> GetInstanceProperties(ITypeSymbol type) {
        var properties = new List<IPropertySymbol>();
        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var current = type; ((current is not null) && (current.SpecialType != SpecialType.System_Object)); current = current.BaseType) {
            foreach (var member in current.GetMembers()) {
                if ((member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } property) && seenNames.Add(item: property.Name)) {
                    properties.Add(item: property);
                }
            }
        }

        return properties;
    }
    private static bool IsPrimitiveLeaf(ITypeSymbol type) =>
        (type.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Char
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Decimal
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_String
            or SpecialType.System_DateTime);
    private bool TryGetEnumerableElementType(ITypeSymbol type, out ITypeSymbol elementType) {
        if (m_knownTypes.EnumerableOpenGenericType is { } enumerableType) {
            if ((type is INamedTypeSymbol { IsGenericType: true } named) && SymbolEqualityComparer.Default.Equals(x: named.OriginalDefinition, y: enumerableType)) {
                elementType = named.TypeArguments[0];

                return true;
            }

            foreach (var candidate in type.AllInterfaces) {
                if (candidate.IsGenericType && SymbolEqualityComparer.Default.Equals(x: candidate.OriginalDefinition, y: enumerableType)) {
                    elementType = candidate.TypeArguments[0];

                    return true;
                }
            }
        }

        elementType = null!;

        return false;
    }
}
