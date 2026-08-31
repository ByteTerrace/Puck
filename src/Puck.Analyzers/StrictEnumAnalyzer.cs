using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// Closes a completeness gap in enum serialization: the strict-enum mechanism
/// (<c>[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter&lt;TEnum&gt;))]</c>, plus the closed
/// generic instances a leaner project registers on its <c>JsonSerializerContext</c> instead) has no completeness
/// gate, so an enum added — or forgotten — silently regresses to numeric-tolerant serialization with a green build.
/// This analyzer walks every <c>[JsonSerializable]</c> root of every <c>System.Text.Json.Serialization.JsonSerializerContext</c>
/// this compilation declares and refuses to build when the walk reaches an enum that carries no explicit
/// <c>System.Text.Json.Serialization.JsonConverterAttribute</c> of its own — neither directly on its
/// declaration nor as a closed converter entry in its context's <c>[JsonSourceGenerationOptions(Converters = ...)]</c>
/// array (<see cref="Enum001EnumNotExplicitlyConverted"/>). A member shape the walk cannot classify statically — an
/// <see cref="object"/>-typed member, or an interface with no
/// <c>System.Text.Json.Serialization.JsonPolymorphicAttribute</c> family — is refused rather than silently treated as
/// covered (<see cref="Enum002UnclassifiableJsonShape"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What "explicitly converted" means, and why that is the whole check.</b> This gate proves an enum was not
/// forgotten — that someone made a deliberate choice about how it crosses the wire — not that the converter it
/// carries refuses a numeric token (that is <c>Puck.Abstractions.Documents.StrictEnumConverter&lt;TEnum&gt;</c>'s own
/// job, proven once, by construction, at its single definition). Any explicit <c>[JsonConverter]</c> counts: the strict one, and the
/// repository's few bespoke enum converters (<c>SurfaceFormatJsonConverter</c>, <c>WorldBackendPreferenceJsonConverter</c>)
/// that emit a hand-chosen token vocabulary and refuse anything else via their own hand-written <c>Read</c>. Proving a
/// bespoke converter's body is itself strict is a code-review question this analyzer does not attempt to settle by
/// static analysis of arbitrary <c>Read</c>/<c>Write</c> bodies.
/// </para>
/// <para>
/// <b>The reachability rule.</b> Starting at each <c>[JsonSerializable(typeof(Root))]</c> type, the walk unwraps
/// <c>Nullable&lt;T&gt;</c>, arrays, and any <c>IEnumerable&lt;T&gt;</c> (via its element type — a dictionary's
/// <c>KeyValuePair&lt;TKey, TValue&gt;</c> included, walked afterwards like any other struct, so a non-string,
/// unconverted enum key would still be caught rather than needing its own rule); descends into every accessible,
/// non-static, gettable instance property (including those inherited from a base type), across the whole
/// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> family when the type declares one; and stops — without
/// requiring anything further, and without reporting anything — the moment it reaches a type that itself carries an
/// explicit <c>[JsonConverter]</c> (attribute-based, e.g. <c>StrictEnumConverter&lt;TEnum&gt;</c> on an enum, or
/// context-registered, e.g. <c>GrantSubjectJsonConverter</c> on <c>GrantSubject</c>): a type converted whole never has
/// its own properties visited by System.Text.Json's serializer, so nothing under it is reachable through property
/// serialization at all. This is what excuses <c>GrantSubjectKind</c>/<c>PrincipalKind</c> (nested inside a
/// wrapper-converted record) and the <c>Puck.World.Authoring</c> document family (bridged through its own
/// <c>DocumentJsonOptions.Shared</c> serializer via a converter) without any allowlist: the exclusion is the
/// converter attribute or <c>Converters</c> entry already sitting in source, at the site a reader finds it, for the
/// reason already written there. A member marked <c>System.Text.Json.Serialization.JsonIgnoreAttribute</c> with
/// no <c>Condition</c> (or <c>Condition: JsonIgnoreCondition.Always</c>) is never serialized, so the walk skips it
/// outright. A conditional ignore
/// (<c>WhenWritingNull</c>/<c>WhenWritingDefault</c>) is NOT treated as absent — the member can still reach the wire —
/// so the walk still descends into it.
/// </para>
/// <para>
/// <b>What this does not cover.</b> Known BCL leaves (the numeric/bool/char/string primitives, <see cref="System.DateTime"/>,
/// <see cref="System.Guid"/>, <see cref="System.TimeSpan"/>, <see cref="System.DateTimeOffset"/>, <see cref="System.Uri"/>,
/// <see cref="System.Numerics.Vector2"/>/<see cref="System.Numerics.Vector3"/>/<see cref="System.Numerics.Vector4"/>/
/// <see cref="System.Numerics.Quaternion"/>/<see cref="System.Numerics.Matrix4x4"/>, and
/// <c>System.Text.Json.JsonElement</c>/<c>System.Text.Json.JsonDocument</c>) are treated as leaves and
/// never recursed into — a hand-maintained list, not a derived one, because none of them can carry a user enum. Only
/// <c>System.Text.Json.Serialization.JsonSerializerContext</c>-derived contexts are walked at all: a document family
/// riding a hand-built <c>System.Text.Json.JsonSerializerOptions</c> instead (<c>Puck.World.Authoring</c>'s
/// own) is a different serializer entirely and is never inspected — not because it is excluded, but because this
/// analyzer only ever looks at <c>JsonSerializerContext</c> subclasses in the first place. Public fields (as opposed
/// to properties — including the compiler-synthesized properties behind a positional record parameter) are not
/// walked; the document families this gate currently covers declare none, so this is not a gap in practice today,
/// but it is a gap in principle and is recorded as such here rather than silently.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StrictEnumAnalyzer : DiagnosticAnalyzer {
    private const string Category = "Puck.StrictEnum";

    /// <summary>ENUM001: an enum reachable through a strict <c>JsonSerializerContext</c>'s serialization graph carries no explicit <c>[JsonConverter]</c> of its own.</summary>
    public static readonly DiagnosticDescriptor Enum001EnumNotExplicitlyConverted = new(
        id: "ENUM001",
        title: "Enum crosses a JSON boundary with no explicit converter",
        messageFormat: "'{0}' is reachable from '{1}''s JsonSerializable graph through '{2}'.'{3}' but carries no explicit [JsonConverter] of its own (neither on its declaration nor as a closed converter entry in '{1}''s [JsonSourceGenerationOptions(Converters = ...)] array); it will serialize as a numeric ordinal and silently accept an out-of-range number on read — add [JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<{0}>))] to its declaration, or register a closed StrictEnumConverter<{0}> (or a bespoke converter) on '{1}' if it cannot reference Puck.Abstractions",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every enum reachable from a JsonSerializerContext's [JsonSerializable] roots through ordinary property serialization must declare how it crosses the wire. The default — no converter — is a silent numeric ordinal that tolerates any in-range integer on read, which is exactly the accepted-and-inert regression the strict-enum mechanism exists to prevent."
    );
    /// <summary>ENUM002: the walk reached a member shape it cannot classify without risking a false 'covered'.</summary>
    public static readonly DiagnosticDescriptor Enum002UnclassifiableJsonShape = new(
        id: "ENUM002",
        title: "JSON member shape cannot be statically classified for strict-enum coverage",
        messageFormat: "'{0}'.'{1}' has a shape this analyzer cannot statically classify ({2}), so it cannot prove every enum reachable through it is explicitly converted; give it a concrete, classifiable type (or a converter of its own) rather than leaving the reachability check to guess",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The reachability walk this gate runs on is only as good as its ability to name every type an enum could hide behind. An System.Object-typed member, or an interface with no [JsonPolymorphic]/[JsonDerivedType] family and no converter of its own, defeats that naming, so it is refused outright rather than silently treated as though nothing were there."
    );

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
        item1: Enum001EnumNotExplicitlyConverted,
        item2: Enum002UnclassifiableJsonShape
    );

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(action: AnalyzeCompilationStart);
    }

    private static void AnalyzeCompilationStart(CompilationStartAnalysisContext context) {
        var knownTypes = StrictEnumKnownTypes.Resolve(compilation: context.Compilation);

        // No System.Text.Json.Serialization.JsonSerializerContext in scope means nothing this analyzer can walk;
        // most compilations in this repository (Puck.Maths, the emulators, the analyzers themselves) never declare
        // one, and bailing here keeps them from paying for a symbol walk that can never find a root.
        if (knownTypes is null) {
            return;
        }

        context.RegisterSymbolAction(
            action: symbolContext => AnalyzeNamedType(
                knownTypes: knownTypes,
                symbolContext: symbolContext
            ),
            symbolKinds: ImmutableArray.Create(item: SymbolKind.NamedType)
        );
    }
    private static void AnalyzeNamedType(SymbolAnalysisContext symbolContext, StrictEnumKnownTypes knownTypes) {
        var candidate = ((INamedTypeSymbol)symbolContext.Symbol);

        if (!StrictEnumReachability.DerivesFromJsonSerializerContext(
            type: candidate,
            jsonSerializerContextType: knownTypes.JsonSerializerContextType
        )) {
            return;
        }

        // Only a context this compilation itself declares (a partial declaration with source) is walked: a
        // JsonSerializerContext referenced only as metadata (from another assembly) carries no roots to discover —
        // JsonSerializableAttribute is not preserved as reflectable metadata the way ordinary attributes are, and
        // there would be no source location to report against regardless.
        if (candidate.Locations.All(predicate: location => !location.IsInSource)) {
            return;
        }

        var roots = StrictEnumReachability.CollectSerializableRoots(
            context: candidate,
            knownTypes: knownTypes
        );
        var converters = StrictEnumReachability.CollectRegisteredConverters(
            context: candidate,
            knownTypes: knownTypes
        );

        var walker = new StrictEnumReachability(
            context: candidate,
            knownTypes: knownTypes,
            registeredConverterTargets: converters,
            symbolContext: symbolContext
        );

        foreach (var root in roots) {
            walker.Walk(type: root);
        }
    }
}
