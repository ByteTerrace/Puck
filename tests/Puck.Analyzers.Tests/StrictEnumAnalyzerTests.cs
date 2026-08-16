using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="StrictEnumAnalyzer"/>'s reachability walk against small, self-contained
/// <c>JsonSerializerContext</c> declarations. Every case declares its own converters, enums, and root types inline
/// rather than referencing <c>Puck.Abstractions.Documents.StrictEnumConverter{TEnum}</c> — this test project does not
/// reference that assembly, and exercising the two generic shapes the analyzer actually recognizes
/// (<c>System.Text.Json.Serialization.JsonConverter&lt;T&gt;</c> directly, and the BCL's own
/// <c>System.Text.Json.Serialization.JsonStringEnumConverter&lt;TEnum&gt;</c> — a <c>JsonConverterFactory</c> in its
/// class hierarchy despite converting exactly one closed enum type, the same shape
/// <c>StrictEnumConverter&lt;TEnum&gt;</c> itself derives from) proves the general mechanism rather than one brand's
/// use of it.
/// </summary>
public sealed class StrictEnumAnalyzerTests {
    /// <summary>
    /// The body every test source's <c>TestContext</c> needs to compile: the harness builds a plain
    /// <see cref="Microsoft.CodeAnalysis.CSharp.CSharpCompilation"/> with no source generators attached (see
    /// <see cref="Harness.Compile"/>), so a partial <c>JsonSerializerContext</c> declaration's abstract members
    /// (which the real System.Text.Json source generator would fill in) are never implemented unless a case
    /// supplies them itself. The analyzer only reads attributes and symbols, never runs this code, so a trivial,
    /// always-throwing implementation is enough to satisfy the compiler.
    /// </summary>
    private const string ContextBoilerplate = """

            public TestContext() : base(null) { }
            protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => throw new System.NotImplementedException();
            public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => throw new System.NotImplementedException();
        """;

    private static AnalysisResult Run(string source) =>
        Harness.Analyze(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source.Replace(newValue: (("JsonSerializerContext {" + ContextBoilerplate) + "}"), oldValue: "JsonSerializerContext { }"))),
            analyzer: new StrictEnumAnalyzer());

    [Fact]
    public void EnumWithNoConverterAnywhereIsReported() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum PlainEnum { A, B }

            public sealed record Root(PlainEnum Plain);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM001" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "PlainEnum", actualString: result.Single(id: "ENUM001").GetMessage());
        Assert.Contains(expectedSubstring: "Root", actualString: result.Single(id: "ENUM001").GetMessage());
        Assert.Contains(expectedSubstring: "Plain", actualString: result.Single(id: "ENUM001").GetMessage());
    }
    [Fact]
    public void EnumConvertedAtItsOwnDeclarationIsNotReported() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            [JsonConverter(typeof(JsonStringEnumConverter<CoveredEnum>))]
            public enum CoveredEnum { A, B }

            public sealed record Root(CoveredEnum Covered);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EnumRegisteredAsAClosedJsonStringEnumConverterFactoryOnTheContextIsNotReported() {
        // The CommandPhase shape: an enum that cannot carry [JsonConverter] at its own declaration is
        // instead registered as a closed instance in the context's Converters array. JsonStringEnumConverter<TEnum>
        // is a JsonConverterFactory in its class hierarchy (confirmed against the real BCL type, not assumed), so
        // this proves the factory-recognizing half of ConvertedType.
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum FactoryRegisteredEnum { A, B }

            public sealed record Root(FactoryRegisteredEnum Factory);

            [JsonSerializable(typeof(Root))]
            [JsonSourceGenerationOptions(Converters = new[] { typeof(JsonStringEnumConverter<FactoryRegisteredEnum>) })]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EnumRegisteredAsAClosedBespokeJsonConverterOnTheContextIsNotReported() {
        // The SurfaceFormat/WorldBackendPreference shape: a hand-written JsonConverter<TEnum> (not
        // JsonStringEnumConverter) registered on the context rather than the enum's own declaration.
        var result = Run(source: """
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum BespokeEnum { A, B }

            public sealed class BespokeEnumConverter : JsonConverter<BespokeEnum> {
                public override BespokeEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => BespokeEnum.A;
                public override void Write(Utf8JsonWriter writer, BespokeEnum value, JsonSerializerOptions options) { }
            }

            public sealed record Root(BespokeEnum Bespoke);

            [JsonSerializable(typeof(Root))]
            [JsonSourceGenerationOptions(Converters = new[] { typeof(BespokeEnumConverter) })]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EnumBehindAWholeTypeConverterIsNeverReached() {
        // The GrantSubjectKind/PrincipalKind shape: Wrapped carries its own converter, so System.Text.Json never
        // serializes its properties directly — HiddenEnum (uncovered) must never be visited, let alone reported.
        var result = Run(source: """
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum HiddenEnum { A, B }

            public sealed record Wrapped(HiddenEnum Hidden);

            public sealed class WrappedConverter : JsonConverter<Wrapped> {
                public override Wrapped? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
                public override void Write(Utf8JsonWriter writer, Wrapped value, JsonSerializerOptions options) { }
            }

            public sealed record Root(Wrapped Wrapped);

            [JsonSerializable(typeof(Root))]
            [JsonSourceGenerationOptions(Converters = new[] { typeof(WrappedConverter) })]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EnumBehindAnAlwaysIgnoredPropertyIsNeverReached() {
        // The CommandValueKind shape: a bare [JsonIgnore] removes the member from serialization entirely.
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum IgnoredEnum { A, B }

            public sealed record Root([property: JsonIgnore] IgnoredEnum Ignored = IgnoredEnum.A);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void EnumBehindAConditionallyIgnoredPropertyIsStillReached() {
        // JsonIgnoreCondition.WhenWritingNull still lets the member reach the wire when non-null, so the walk must
        // NOT treat it the same as an unconditional [JsonIgnore].
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum ConditionallyReachedEnum { A, B }

            public sealed record Root([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConditionallyReachedEnum? Maybe = null);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM001" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "ConditionallyReachedEnum", actualString: result.Single(id: "ENUM001").GetMessage());
    }
    [Fact]
    public void EnumInsideANullableWrapperIsUnwrappedAndReached() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum NullableEnum { A, B }

            public sealed record Root(NullableEnum? Maybe);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM001" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "NullableEnum", actualString: result.Single(id: "ENUM001").GetMessage());
    }
    [Fact]
    public void EnumInsideACollectionElementIsReached() {
        var result = Run(source: """
            using System.Collections.Generic;
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum ElementEnum { A, B }

            public sealed record Row(ElementEnum Value);

            public sealed record Root(IReadOnlyList<Row>? Rows);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM001" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "ElementEnum", actualString: result.Single(id: "ENUM001").GetMessage());
    }
    [Fact]
    public void EnumOnOnePolymorphicDerivedTypeIsReached() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum DerivedEnum { A, B }

            [JsonDerivedType(typeof(Shape.Circle), typeDiscriminator: "circle")]
            [JsonDerivedType(typeof(Shape.Square), typeDiscriminator: "square")]
            [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
            public abstract record Shape {
                public sealed record Circle(DerivedEnum Kind) : Shape;
                public sealed record Square() : Shape;
            }

            public sealed record Root(Shape Shape);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM001" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "DerivedEnum", actualString: result.Single(id: "ENUM001").GetMessage());
    }
    [Fact]
    public void ObjectTypedMemberIsRefusedAsUnclassifiable() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public sealed record Root(object Anything);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM002" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "Anything", actualString: result.Single(id: "ENUM002").GetMessage());
        Assert.Contains(expectedSubstring: "System.Object", actualString: result.Single(id: "ENUM002").GetMessage());
    }
    [Fact]
    public void InterfaceWithNoPolymorphicFamilyIsRefusedAsUnclassifiable() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public interface IUnconstrained { }

            public sealed record Root(IUnconstrained Value);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM002" }, actual: result.Ids);
        Assert.Contains(expectedSubstring: "IUnconstrained", actualString: result.Single(id: "ENUM002").GetMessage());
    }
    [Fact]
    public void ARecordsSynthesizedEqualityContractNeverTriggersAReflectionWalk() {
        // Regression coverage for the bug this gate's own falsification pass caught: every C# record synthesizes a
        // `protected virtual System.Type EqualityContract` property. Left unfiltered, walking it follows straight
        // into System.Type's own enormous reflection surface (System.Reflection.MemberInfo/MethodBase/Assembly and
        // their many unconverted enums), reporting dozens of unrelated ENUM001s for a compilation with none of its
        // own. A plain record with only covered members must produce zero diagnostics.
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            [JsonConverter(typeof(JsonStringEnumConverter<CoveredEnum>))]
            public enum CoveredEnum { A, B }

            public sealed record Leaf(CoveredEnum Kind, int Count);

            public sealed record Root(Leaf Leaf, string Name);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    [Fact]
    public void TheSameEnumReachedFromTwoPropertiesIsReportedOnlyOnce() {
        var result = Run(source: """
            using System.Text.Json.Serialization;

            namespace Subject.Assembly;

            public enum SharedEnum { A, B }

            public sealed record Root(SharedEnum First, SharedEnum Second);

            [JsonSerializable(typeof(Root))]
            internal partial class TestContext : JsonSerializerContext { }
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: new[] { "ENUM001" }, actual: result.Ids);
    }
    [Fact]
    public void ACompilationWithNoJsonSerializerContextIsNotAnalyzedAtAll() {
        var result = Run(source: """
            namespace Subject.Assembly;

            public enum UnrelatedEnum { A, B }

            public sealed record NotADocument(UnrelatedEnum Value);
            """);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
}
