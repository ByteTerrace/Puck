using Microsoft.CodeAnalysis;

namespace Puck.Analyzers;

/// <summary>
/// The <c>System.Text.Json</c> marker types <see cref="StrictEnumAnalyzer"/> needs resolved against one
/// compilation, plus the fixed set of BCL leaf types the reachability walk never recurses into. Resolved once per
/// compilation (see <see cref="Resolve"/>) rather than re-looked-up per symbol.
/// </summary>
internal sealed class StrictEnumKnownTypes {
    /// <summary>The BCL/aggregate types the walk treats as leaves — none of them can carry a user enum, so recursing
    /// into their own members would only waste work (or, for a sealed BCL type with private fields, fail outright).
    /// A hand-maintained list, not a derived one: see <see cref="StrictEnumAnalyzer"/>'s remarks for why.</summary>
    private static readonly string[] KnownLeafMetadataNames = [
        "System.Guid",
        "System.TimeSpan",
        "System.DateTimeOffset",
        "System.Uri",
        "System.Numerics.Vector2",
        "System.Numerics.Vector3",
        "System.Numerics.Vector4",
        "System.Numerics.Quaternion",
        "System.Numerics.Matrix4x4",
        "System.Text.Json.JsonElement",
        "System.Text.Json.JsonDocument",
    ];

    private StrictEnumKnownTypes(
        INamedTypeSymbol jsonSerializerContextType,
        INamedTypeSymbol? jsonSerializableAttributeType,
        INamedTypeSymbol? jsonSourceGenerationOptionsAttributeType,
        INamedTypeSymbol? jsonConverterAttributeType,
        INamedTypeSymbol? jsonIgnoreAttributeType,
        INamedTypeSymbol? jsonDerivedTypeAttributeType,
        INamedTypeSymbol? jsonConverterOpenGenericType,
        INamedTypeSymbol? jsonStringEnumConverterOpenGenericType,
        INamedTypeSymbol? enumerableOpenGenericType
    ) {
        JsonSerializerContextType = jsonSerializerContextType;
        JsonSerializableAttributeType = jsonSerializableAttributeType;
        JsonSourceGenerationOptionsAttributeType = jsonSourceGenerationOptionsAttributeType;
        JsonConverterAttributeType = jsonConverterAttributeType;
        JsonIgnoreAttributeType = jsonIgnoreAttributeType;
        JsonDerivedTypeAttributeType = jsonDerivedTypeAttributeType;
        JsonConverterOpenGenericType = jsonConverterOpenGenericType;
        JsonStringEnumConverterOpenGenericType = jsonStringEnumConverterOpenGenericType;
        EnumerableOpenGenericType = enumerableOpenGenericType;
    }

    /// <summary><c>System.Text.Json.Serialization.JsonSerializerContext</c>. Resolving is the gate for this analyzer doing anything at all.</summary>
    public INamedTypeSymbol JsonSerializerContextType { get; }

    /// <summary><c>System.Text.Json.Serialization.JsonSerializableAttribute</c>.</summary>
    public INamedTypeSymbol? JsonSerializableAttributeType { get; }

    /// <summary><c>System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute</c>, whose <c>Converters</c> named argument is the closed-generic registration list for an enum that cannot carry the attribute at its own declaration (<c>CommandPhase</c>).</summary>
    public INamedTypeSymbol? JsonSourceGenerationOptionsAttributeType { get; }

    /// <summary><c>System.Text.Json.Serialization.JsonConverterAttribute</c> — presence on a type (any converter, strict or bespoke) is what this gate accepts as "explicitly handled".</summary>
    public INamedTypeSymbol? JsonConverterAttributeType { get; }

    /// <summary><c>System.Text.Json.Serialization.JsonIgnoreAttribute</c> — a bare use (or an explicit <c>Condition: JsonIgnoreCondition.Always</c>) removes the member from the reachability walk entirely.</summary>
    public INamedTypeSymbol? JsonIgnoreAttributeType { get; }

    /// <summary><c>System.Text.Json.Serialization.JsonDerivedTypeAttribute</c> — names one member of a polymorphic family the walk must also cover.</summary>
    public INamedTypeSymbol? JsonDerivedTypeAttributeType { get; }

    /// <summary>The open generic <c>System.Text.Json.Serialization.JsonConverter&lt;T&gt;</c>, walked up a converter class's base-type chain to recover the exact type it converts.</summary>
    public INamedTypeSymbol? JsonConverterOpenGenericType { get; }

    /// <summary>
    /// The open generic <c>System.Text.Json.Serialization.JsonStringEnumConverter&lt;TEnum&gt;</c> — the BCL's own
    /// generic string-enum converter, and the base <c>Puck.Abstractions.Documents.StrictEnumConverter&lt;TEnum&gt;</c>
    /// derives from. It is itself a <c>JsonConverterFactory</c> (not a closed <c>JsonConverter&lt;TEnum&gt;</c>) even though the enum it
    /// converts is fixed at compile time by its own type argument, so a converter class deriving from a CLOSED
    /// instantiation of this type is understood to convert exactly that closing type argument — the same shortcut
    /// this analyzer takes for an ordinary closed <c>JsonConverter&lt;T&gt;</c> descendant, extended to cover the one
    /// BCL type in this repository's registered converters that is a factory in name only.
    /// </summary>
    public INamedTypeSymbol? JsonStringEnumConverterOpenGenericType { get; }

    /// <summary>The open generic <c>System.Collections.Generic.IEnumerable&lt;T&gt;</c>, used to find a collection's element type (including a dictionary's <c>KeyValuePair&lt;TKey, TValue&gt;</c>, which is then walked like any other struct).</summary>
    public INamedTypeSymbol? EnumerableOpenGenericType { get; }

    /// <summary>Whether <paramref name="type"/> is one of the fixed BCL leaves the walk never recurses into.</summary>
    public static bool IsKnownLeaf(ITypeSymbol type) {
        var ns = type.ContainingNamespace;

        if (
            (ns is null) ||
            ns.IsGlobalNamespace
        ) {
            return false;
        }

        var fullName = $"{ns.ToDisplayString()}.{type.MetadataName}";

        foreach (var candidate in KnownLeafMetadataNames) {
            if (string.Equals(
                a: candidate,
                b: fullName,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves every marker type against <paramref name="compilation"/>, or <see langword="null"/> when the compilation carries no <c>JsonSerializerContext</c> at all — the precondition for anything else here mattering.</summary>
    public static StrictEnumKnownTypes? Resolve(Compilation compilation) {
        var jsonSerializerContextType = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonSerializerContext");

        if (jsonSerializerContextType is null) {
            return null;
        }

        return new StrictEnumKnownTypes(
            jsonSerializerContextType: jsonSerializerContextType,
            jsonSerializableAttributeType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonSerializableAttribute"),
            jsonSourceGenerationOptionsAttributeType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute"),
            jsonConverterAttributeType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonConverterAttribute"),
            jsonIgnoreAttributeType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonIgnoreAttribute"),
            jsonDerivedTypeAttributeType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonDerivedTypeAttribute"),
            jsonConverterOpenGenericType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonConverter`1"),
            jsonStringEnumConverterOpenGenericType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Text.Json.Serialization.JsonStringEnumConverter`1"),
            enumerableOpenGenericType: compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "System.Collections.Generic.IEnumerable`1")
        );
    }
}
