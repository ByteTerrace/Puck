using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Analysis;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Pins derivation reach with source compilations whose project references contain metadata only.</summary>
public sealed class DerivationLawTests {
    private const string GeneratedPath = "src/Puck.SignedDistance/Baking/DerivationFingerprint.cs";
    private const string Marker = """
        namespace Puck {
            [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Constructor, AllowMultiple = true)]
            internal sealed class DerivationAttribute : System.Attribute {
                public DerivationAttribute(string name) { }
            }
        }
        """;

    private static readonly MetadataReference[] References = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(separator: Path.PathSeparator)
        .Select(selector: static path => MetadataReference.CreateFromFile(path: path))
        .ToArray<MetadataReference>();

    private static CSharpCompilation Compile(string assemblyName, string[] sources, params MetadataReference[] references) {
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary),
            references: References.Concat(second: references),
            syntaxTrees: sources.Prepend(element: Marker).Select(selector: static (source, index) => CSharpSyntaxTree.ParseText(
                path: $"derivation-keys-fixture-{index}.cs",
                text: source
            ))
        );

        Assert.Empty(collection: compilation.GetDiagnostics().Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)));

        return compilation;
    }
    private static DerivationResult Derive(params Compilation[] compilations) {
        var result = Assert.Single(collection: DerivationReach.Compute(compilations: compilations));

        Assert.Equal(actual: result.Name, expected: "bake");

        return result;
    }
    private static PortableExecutableReference Emit(CSharpCompilation compilation) {
        using var image = new MemoryStream();
        var result = compilation.Emit(peStream: image);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));

        return MetadataReference.CreateFromImage(peImage: image.ToArray());
    }
    private static DerivationResult SingleSource(string source) {
        return Derive(compilations: [Compile(assemblyName: "Fixture.Bake", sources: [source])]);
    }

    [Fact]
    public void AliasRebindingMovesTheFingerprintEvenWhenTheReachIsUnchanged() {
        const string Source = """
            using Left = Fixture.First;
            using Right = Fixture.Second;
            namespace Fixture;
            public static class First { public static int Value() => 7; }
            public static class Second { public static int Value() => 2; }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() => Left.Value() - Right.Value();
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Left = Fixture.Second", oldValue: "Left = Fixture.First")
            .Replace(comparisonType: StringComparison.Ordinal, newValue: "Right = Fixture.First", oldValue: "Right = Fixture.Second"));

        Assert.Equal(expected: original.Symbols, actual: edited.Symbols);
        Assert.NotEqual(expected: original.Fingerprint, actual: edited.Fingerprint);
    }
    [Fact]
    public void ExternalCallsReachTheCallbacksOnTheirSourceArguments() {
        const string Source = """
            namespace Fixture;
            public sealed class Comparer : System.Collections.Generic.IEqualityComparer<int> {
                public bool Equals(int x, int y) => true;
                public int GetHashCode(int value) => 0;
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() {
                    var values = new System.Collections.Generic.HashSet<int>(new Comparer());
                    values.Add(1);
                    values.Add(2);
                    return values.Count;
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "=> x == y", oldValue: "=> true"));

        Assert.NotEqual(expected: original.Fingerprint, actual: edited.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Comparer.Equals(System.Int32,System.Int32)"));
    }
    [Fact]
    public void ExternalCallsReachTheVirtualOverridesOnTheirSourceArguments() {
        const string Source = """
            namespace Fixture;
            public sealed class Descending : System.Collections.Generic.Comparer<int> {
                public override int Compare(int x, int y) => y.CompareTo(x);
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() {
                    var values = new System.Collections.Generic.List<int> { 1, 2 };

                    values.Sort(new Descending());
                    return values[0];
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "=> x.CompareTo(y)", oldValue: "=> y.CompareTo(x)"));

        Assert.NotEqual(expected: original.Fingerprint, actual: edited.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Descending.Compare(System.Int32,System.Int32)"));
    }
    [Fact]
    public void SpellingOnlyEditsDoNotMoveTheFingerprint() {
        const string Source = """
            using Left = Fixture.First;
            using Right = Fixture.Second;
            using System;
            using System.Collections.Generic;
            namespace Fixture;
            public static class First { public static int Value() => 7; }
            public static class Second { public static int Value() => 2; }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() { var values = new HashSet<int> { Left.Value(), Right.Value() }; return values.Count; }
            }
            """;
        const string Edited = """
            // A leading comment.
            using System.Collections.Generic;
            using Right = Fixture.Second;

            using System;
            using Left = Fixture.First;
            namespace Fixture;
            public static class First { public static int Value() => 7; }
            public static class Second { public static int Value() => 2; }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() {
                    /* regrouped */
                    var values = new HashSet<int> {
                        Left.Value(),
                        Right.Value()
                    };

                    return values.Count;
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Edited);

        Assert.Equal(expected: original.Fingerprint, actual: edited.Fingerprint);
        Assert.Equal(expected: original.Symbols, actual: edited.Symbols);
    }
    [InlineData("Create<Sample>()", "private static int Create<T>() where T : new() { _ = new T(); return Value; }")]
    [InlineData("Factory<Sample>.Create()", "private static class Factory<T> where T : new() { public static int Create() { _ = new T(); return Value; } }")]
    [Theory]
    public void GenericConstructionReachesConcreteConstructors(string expression, string factory) {
        var source = $$"""
            namespace Fixture;
            public sealed class Sample { public Sample() { Producer.Value = 7; } }
            public static class Producer {
                public static int Value;
                {{factory}}
                [Puck.Derivation("bake")]
                public static int Bake() => {{expression}};
            }
            """;
        var original = SingleSource(source: source);
        var edited = SingleSource(source: source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Value = 8", oldValue: "Value = 7"));

        Assert.NotEqual(expected: original.Fingerprint, actual: edited.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Sample.#ctor"));
    }
    [Fact]
    public void GenericConstructionDoesNotReachParameterizedConstructors() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample {
                public Sample() { Value = 7; }
                public Sample(int value) { Value = value + 13; }
                public int Value { get; }
            }
            public static class Producer {
                private static T Create<T>() where T : new() => new T();
                [Puck.Derivation("bake")]
                public static int Bake() => Create<Sample>().Value;
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "value + 14", oldValue: "value + 13"));

        Assert.Equal(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Sample.#ctor"));
        Assert.DoesNotContain(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Sample.#ctor(System.Int32)"));
    }
    [InlineData("new()", "public sealed class Sample { public Sample() : this(3) { } public Sample(int value) { Producer.Value = value + 7; } }", "value + 7", "value + 8")]
    [InlineData("new()", "public sealed class Sample { private readonly int m_seed = Producer.Seed(); public Sample() { } }", "Seed() => 7", "Seed() => 8")]
    [InlineData("new()", "public sealed class Sample { public int Seed { get; } = Producer.Seed(); }", "Seed() => 7", "Seed() => 8")]
    [InlineData("new()", "public sealed class Sample { public event System.Action Changed = Producer.Hook; }", "Value = 7", "Value = 8")]
    [InlineData("new()", "public sealed class Sample { private static readonly int s_seed = Producer.Seed(); }", "Seed() => 7", "Seed() => 8")]
    [InlineData("new()", "public sealed partial class Sample { public Sample() { } } public sealed partial class Sample { private readonly int m_seed = Producer.Seed(); }", "Seed() => 7", "Seed() => 8")]
    [InlineData("new()", "public class Root { public Root() { Producer.Value = 7; } } public sealed class Sample : Root { }", "Value = 7", "Value = 8")]
    [InlineData("new()", "public class Root { public Root(int value) { Producer.Value = value + 7; } } public sealed class Sample : Root { public Sample() : base(2) { } }", "value + 7", "value + 8")]
    [InlineData("new()", "public class Root { private readonly int m_seed = Producer.Seed(); } public sealed class Sample : Root { }", "Seed() => 7", "Seed() => 8")]
    [InlineData("new()", "public sealed class Sample(int value) { private readonly int m_seed = value + Producer.Seed(); public Sample() : this(1) { } }", "Seed() => 7", "Seed() => 8")]
    [InlineData("new()", "public struct Sample { public Sample() { Producer.Value = 7; } }", "Value = 7", "Value = 8")]
    [InlineData("struct", "public struct Sample { public Sample() { Producer.Value = 7; } }", "Value = 7", "Value = 8")]
    [Theory]
    public void GenericConstructionReachesEverythingTheParameterlessConstructorRuns(string constraint, string declaration, string original, string edited) {
        var source = $$"""
            namespace Fixture;
            {{declaration}}
            public static class Producer {
                public static int Value;
                public static int Seed() => 7;
                public static void Hook() { Value = 7; }
                private static int Create<T>() where T : {{constraint}} { _ = new T(); return Value; }
                [Puck.Derivation("bake")]
                public static int Bake() => Create<Sample>();
            }
            """;
        var before = SingleSource(source: source);
        var after = SingleSource(source: source.Replace(comparisonType: StringComparison.Ordinal, newValue: edited, oldValue: original));

        Assert.NotEqual(expected: before.Fingerprint, actual: after.Fingerprint);
    }
    [Fact]
    public void GenericLocalConstructionReachesConcreteConstructors() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample { public Sample() { Producer.Value = 7; } }
            public static class Producer {
                public static int Value;
                [Puck.Derivation("bake")]
                public static int Bake() {
                    static int Create<T>() where T : new() { _ = new T(); return Value; }
                    return Create<Sample>();
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Value = 8", oldValue: "Value = 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Sample.#ctor"));
    }
    [Fact]
    public void PartialInitializerExecutionOrderMovesTheFingerprint() {
        const string First = """
            namespace Fixture;
            public static partial class Producer {
                private static int s_counter;
                private static int Next() => ++s_counter;
                public static int First = Next();
                [Puck.Derivation("bake")]
                public static int Bake() => Used;
            }
            """;
        const string Second = """
            namespace Fixture;
            public static partial class Producer { public static int Used = Next(); }
            """;
        var original = Derive(compilations: [Compile(assemblyName: "Fixture.Bake", sources: [First, Second])]);
        var reordered = Derive(compilations: [Compile(assemblyName: "Fixture.Bake", sources: [Second, First])]);

        Assert.NotEqual(expected: original.Fingerprint, actual: reordered.Fingerprint);
    }
    [Fact]
    public void TransitiveReachMovesForItsMembersButNotAnUncalledSibling() {
        const string Source = """
            namespace Fixture;
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() => Step();
                private static int Step() => Value;
                private static int Value => Seed;
                private const int Seed = 7;
                public static int Outside() => 13;
            }
            """;
        var original = SingleSource(source: Source);
        var reachedEdit = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Seed = 8", oldValue: "Seed = 7"));
        var outsideEdit = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Outside() => 14", oldValue: "Outside() => 13"));

        Assert.NotEqual(actual: reachedEdit.Fingerprint, expected: original.Fingerprint);
        Assert.Equal(actual: outsideEdit.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Producer.Step")));
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "P:Fixture.Producer.Value")));
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "F:Fixture.Producer.Seed")));
        Assert.DoesNotContain(collection: original.Symbols, filter: static symbol => (symbol.Id == "M:Fixture.Producer.Outside"));
    }
    [Fact]
    public void CrossAssemblyMetadataCallsRebindToTheSourceBody() {
        const string Library = """
            namespace FixtureMath;
            public static class Fixed {
                public static int Evaluate() => Value;
                private static int Value => 17;
                public static int Outside() => 29;
            }
            """;
        const string Entry = """
            namespace Fixture;
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() => FixtureMath.Fixed.Evaluate();
            }
            """;
        var library = Compile(assemblyName: "Fixture.Maths", sources: [Library]);
        var editedLibrary = Compile(assemblyName: "Fixture.Maths", sources: [Library.Replace(comparisonType: StringComparison.Ordinal, newValue: "Value => 18", oldValue: "Value => 17")]);
        var outsideLibrary = Compile(assemblyName: "Fixture.Maths", sources: [Library.Replace(comparisonType: StringComparison.Ordinal, newValue: "Outside() => 30", oldValue: "Outside() => 29")]);
        var entry = Compile(assemblyName: "Fixture.Bake", sources: [Entry], references: [Emit(compilation: library)]);
        var editedEntry = Compile(assemblyName: "Fixture.Bake", sources: [Entry], references: [Emit(compilation: editedLibrary)]);
        var outsideEntry = Compile(assemblyName: "Fixture.Bake", sources: [Entry], references: [Emit(compilation: outsideLibrary)]);
        var original = Derive(compilations: [entry, library]);
        var edited = Derive(compilations: [editedEntry, editedLibrary]);
        var outside = Derive(compilations: [outsideEntry, outsideLibrary]);

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Equal(actual: outside.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Assembly == "Fixture.Maths") && (symbol.Id == "P:FixtureMath.Fixed.Value")));
        Assert.DoesNotContain(collection: original.Symbols, filter: static symbol => (symbol.External && (symbol.Id == "M:FixtureMath.Fixed.Evaluate")));
    }
    [Fact]
    public void DeclarationTreeAndCompilationOrderDoNotMoveTheFingerprint() {
        const string First = """
            namespace Fixture;
            public static partial class Producer {
                [Puck.Derivation("bake")]
                public static int BakeFirst() => FixtureMath.Fixed.First();
            }
            """;
        const string Second = """
            namespace Fixture;
            public static partial class Producer {
                [Puck.Derivation("bake")]
                public static int BakeSecond() => FixtureMath.Fixed.Second();
            }
            """;
        const string FirstMethod = "public static int First() => 7;";
        const string SecondMethod = "public static int Second() => 11;";
        var library = Compile(assemblyName: "Fixture.Maths", sources: [$"namespace FixtureMath; public static class Fixed {{ {FirstMethod} {SecondMethod} }}"]);
        var reorderedLibrary = Compile(assemblyName: "Fixture.Maths", sources: [$"namespace FixtureMath; public static class Fixed {{ {SecondMethod} {FirstMethod} }}"]);
        var entry = Compile(assemblyName: "Fixture.Bake", sources: [First, Second], references: [Emit(compilation: library)]);
        var reorderedEntry = Compile(assemblyName: "Fixture.Bake", sources: [Second, First], references: [Emit(compilation: reorderedLibrary)]);
        var original = Derive(compilations: [entry, library]);
        var reordered = Derive(compilations: [reorderedLibrary, reorderedEntry]);

        Assert.Equal(actual: reordered.Fingerprint, expected: original.Fingerprint);
        Assert.Equal(actual: reordered.Symbols, expected: original.Symbols);
    }
    [Fact]
    public void PartialMethodImplementationContributesItsBody() {
        const string Declaration = """
            namespace Fixture;
            public static partial class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() => Step();
                private static partial int Step();
            }
            """;
        const string Implementation = """
            namespace Fixture;
            public static partial class Producer {
                private static partial int Step() => 7;
            }
            """;
        var original = Derive(compilations: [Compile(assemblyName: "Fixture.Bake", sources: [Declaration, Implementation])]);
        var edited = Derive(compilations: [Compile(assemblyName: "Fixture.Bake", sources: [Declaration, Implementation.Replace(comparisonType: StringComparison.Ordinal, newValue: "Step() => 8", oldValue: "Step() => 7")])]);

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Producer.Step")));
    }
    [Fact]
    public void ImplicitUserDefinedConversionsContributeTheirBody() {
        const string Source = """
            namespace Fixture;
            public readonly struct Sample {
                public static implicit operator int(Sample value) => 7;
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() => new Sample();
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "value) => 8", oldValue: "value) => 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && symbol.Id.Contains(comparisonType: StringComparison.Ordinal, value: ".op_Implicit")));
    }
    [Fact]
    public void ConstructorExecutionReachesFieldInitializersAndTheirCallees() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample {
                private readonly int m_seed = InitialValue();
                private static int InitialValue() => Producer.Value = 7;
                public Sample() { }
            }
            public static class Producer {
                public static int Value;
                [Puck.Derivation("bake")]
                public static int Bake() {
                    _ = new Sample();
                    return Value;
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Value = 8", oldValue: "Value = 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Sample.#ctor")));
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "F:Fixture.Sample.m_seed")));
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Sample.InitialValue")));
    }
    [InlineData("ICalculator")]
    [InlineData("Calculator")]
    [Theory]
    public void DispatchReachesInRepositoryImplementations(string receiver) {
        var source = $$"""
            namespace Fixture;
            public interface ICalculator { int Evaluate(); }
            public abstract class Calculator { public abstract int Evaluate(); }
            public sealed class FixedCalculator : Calculator, ICalculator {
                public override int Evaluate() => 7;
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake({{receiver}} calculator) => calculator.Evaluate();
            }
            """;
        var original = SingleSource(source: source);
        var edited = SingleSource(source: source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Evaluate() => 8", oldValue: "Evaluate() => 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.FixedCalculator.Evaluate")));
    }
    [Fact]
    public void ExternalInterfaceDispatchReachesItsInRepositoryImplementation() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample : System.IComparable<int> {
                public int CompareTo(int value) => 7;
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake(System.IComparable<int> value) => value.CompareTo(1);
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "CompareTo(int value) => 8", oldValue: "CompareTo(int value) => 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Sample.CompareTo(System.Int32)")));
    }
    [Fact]
    public void ExternalVirtualDispatchReachesItsInRepositoryOverride() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample {
                public override int GetHashCode() => 7;
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake(object value) => value.GetHashCode();
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "GetHashCode() => 8", oldValue: "GetHashCode() => 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
    }
    [Fact]
    public void ExtensionDeconstructionReachesItsBody() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample { }
            public static class Extensions {
                public static void Deconstruct(this Sample sample, out int x, out int y) { x = 7; y = 2; }
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake(Sample sample) { var (x, y) = sample; return x + y; }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "x = 8", oldValue: "x = 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
    }
    [Fact]
    public void RecordWithReachesTheCopyConstructorBody() {
        const string Source = """
            namespace Fixture;
            public sealed record Sample {
                public Sample(int value) { Value = value; }
                private Sample(Sample original) { Value = original.Value + 7; }
                public int Value { get; init; }
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake(Sample sample) => (sample with { }).Value;
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "original.Value + 8", oldValue: "original.Value + 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Sample.#ctor(Fixture.Sample)")));
    }
    [Fact]
    public void ImplicitEnumValuesReachTheirPrecedingValue() {
        const string Source = """
            namespace Fixture;
            public enum Kind { Seed = 7, Used }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake() => (int)Kind.Used;
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Seed = 8", oldValue: "Seed = 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "F:Fixture.Kind.Seed")));
    }
    [InlineData("public enum Kind { First, Used }", "public enum Kind { Used, First }", "(int)Kind.Used")]
    [InlineData("public static int First = Next(); public static int Used = Next();", "public static int Used = Next(); public static int First = Next();", "Used")]
    [Theory]
    public void ExecutableDeclarationOrderStillMovesTheFingerprint(string originalMembers, string editedMembers, string expression) {
        var source = $$"""
            namespace Fixture;
            public static class Producer {
                private static int s_counter;
                private static int Next() => ++s_counter;
                {{originalMembers}}
                [Puck.Derivation("bake")]
                public static int Bake() => {{expression}};
            }
            """;
        var original = SingleSource(source: source);
        var edited = SingleSource(source: source.Replace(comparisonType: StringComparison.Ordinal, newValue: editedMembers, oldValue: originalMembers));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
    }
    [Fact]
    public void UsingCleanupReachesItsDisposeBody() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample : System.IDisposable {
                public void Dispose() { Producer.Value = 7; }
            }
            public static class Producer {
                public static int Value;
                [Puck.Derivation("bake")]
                public static int Bake() {
                    using (new Sample()) { }
                    return Value;
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "Value = 8", oldValue: "Value = 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Sample.Dispose")));
    }
    [Fact]
    public void DeconstructionReachesItsDeconstructBody() {
        const string Source = """
            namespace Fixture;
            public sealed class Sample {
                public void Deconstruct(out int x, out int y) {
                    x = 7;
                    y = 2;
                }
            }
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake(Sample sample) {
                    var (x, y) = sample;
                    return x + y;
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "x = 8", oldValue: "x = 7"));

        Assert.NotEqual(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.Contains(collection: original.Symbols, filter: static symbol => (!symbol.External && (symbol.Id == "M:Fixture.Sample.Deconstruct(System.Int32@,System.Int32@)")));
    }
    [Fact]
    public void GeneratedFingerprintDoesNotParticipateInItsOwnReach() {
        const string Source = """
            namespace Puck.SignedDistance.Baking {
                public static class DerivationFingerprint {
                    public const string Bake = "first";
                }
            }
            namespace Fixture {
                public static class Producer {
                    [Puck.Derivation("bake")]
                    public static string Bake() => Puck.SignedDistance.Baking.DerivationFingerprint.Bake;
                }
            }
            """;
        var original = SingleSource(source: Source);
        var edited = SingleSource(source: Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"second\"", oldValue: "\"first\""));

        Assert.Equal(actual: edited.Fingerprint, expected: original.Fingerprint);
        Assert.DoesNotContain(collection: original.Symbols, filter: static symbol => symbol.Id.Contains(comparisonType: StringComparison.Ordinal, value: "Puck.SignedDistance.Baking.DerivationFingerprint"));
    }
    [Fact]
    public void ExternalSymbolsAreListedWithTheirAssembly() {
        var result = SingleSource(source: """
            namespace Fixture;
            public static class Producer {
                [Puck.Derivation("bake")]
                public static int Bake(int value) => System.Math.Abs(value);
            }
            """);
        var external = Assert.Single(collection: result.Symbols, predicate: static symbol => (symbol.Id == "M:System.Math.Abs(System.Int32)"));

        Assert.True(condition: external.External);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: external.Assembly));
    }
    [Fact]
    public void CheckAcceptsGeneratedOutputAndRejectsStaleOutputWithoutWriting() {
        using var directory = new TemporaryDirectory(prefix: "puck-derivation-keys-check-");
        using var output = new StringWriter();
        DerivationResult[] derivations = [new(
            Fingerprint: new string(c: 'a', count: 64),
            Name: "bake",
            Symbols: []
        )];

        Assert.Equal(actual: DerivationsCommand.Write(derivations: derivations, repositoryRoot: directory.RootPath, check: false, output: output), expected: 0);
        var generated = File.ReadAllText(path: directory.PathOf(name: GeneratedPath));

        Assert.Contains(actualString: generated, expectedSubstring: derivations[0].Fingerprint);
        Assert.Equal(actual: DerivationsCommand.Write(derivations: derivations, repositoryRoot: directory.RootPath, check: true, output: output), expected: 0);
        directory.WriteText(name: GeneratedPath, text: "stale derivation\n");
        output.GetStringBuilder().Clear();

        Assert.Equal(actual: DerivationsCommand.Write(derivations: derivations, repositoryRoot: directory.RootPath, check: true, output: output), expected: 1);
        Assert.Contains(actualString: output.ToString(), expectedSubstring: "bake");
        Assert.Equal(actual: File.ReadAllText(path: directory.PathOf(name: GeneratedPath)), expected: "stale derivation\n");
    }
    [Fact]
    public void CheckRejectsMissingOutputWithoutCreatingIt() {
        using var directory = new TemporaryDirectory(prefix: "puck-derivation-keys-missing-");
        using var output = new StringWriter();
        DerivationResult[] derivations = [new(
            Fingerprint: new string(c: 'b', count: 64),
            Name: "bake",
            Symbols: []
        )];

        Assert.Equal(actual: DerivationsCommand.Write(derivations: derivations, repositoryRoot: directory.RootPath, check: true, output: output), expected: 1);
        Assert.Contains(actualString: output.ToString(), expectedSubstring: "bake");
        Assert.False(condition: File.Exists(path: directory.PathOf(name: GeneratedPath)));
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: directory.RootPath));
    }
}
