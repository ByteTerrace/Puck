using Microsoft.CodeAnalysis;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// The door a state holder is (<c>Puck.State.Versioned&lt;T&gt;</c> and its siblings) is held by the compiler and, for
/// what the compiler cannot see, by <see cref="DoorAnalyzer"/>. These cases compile each write shape against the
/// holders' own sources, linked into this assembly and compiled here, so a refusal is the real door's: a shape the
/// read side must refuse names the compiler error that refuses it, a shape only the analyzer can see names its rule,
/// and every shape the write side admits compiles with no diagnostic.
/// </summary>
public sealed class DoorAnalyzerTests {
    // The holders, compiled from their sources into an assembly a subject references as a project references Puck.State.
    private static readonly MetadataReference State = Emit(
        assemblyName: "Puck.State",
        references: [],
        sources: [.. typeof(DoorAnalyzerTests).Assembly.GetManifestResourceNames()
            .Where(predicate: static name => name.Contains(value: "DoorSources", comparisonType: StringComparison.Ordinal))
            .Select(selector: static name => new SourceFile(Name: name, Text: ReadResource(name: name)))]
    );
    // A project that declares a section and uses none of the reinterpreting members.
    private static readonly MetadataReference Sections = Emit(
        assemblyName: "Subject.Sections",
        references: [State],
        sources: [new SourceFile(Name: "Sections.cs", Text: "namespace Subject.Sections { public struct Elsewhere : Puck.State.IVersionedSection { public long Value; } }")]
    );

    private const string Types = """
        namespace Subject {
        using System.Collections.Immutable;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using Puck.State;

        public struct Inner { public long Raw; public void Bump() { Raw++; } public readonly long Peek() => Raw; }
        [InlineArray(4)] public struct Four { private long m_element; }
        public struct Pose : IVersionedSection { public long X; public Inner Carry; public Four Slots; }
        public struct SlotState { public long NormalImpulseRaw; public void Bump() { NormalImpulseRaw++; } }
        [InlineArray(16)] public struct Slots16 { private SlotState m_element; }
        public struct Contact : IVersionedSection { public int Partner; public Slots16 Slots; }
        public sealed class Body {
            private readonly SlotBinding m_binding = new();
            private readonly Versioned<Pose> m_pose;
            private readonly VersionedArray<Contact> m_contacts;

            public Body() { m_pose = new(binding: m_binding); m_contacts = new(binding: m_binding); }

            public ref readonly Pose Pose => ref m_pose.Read;
            public ref Pose PoseEdit => ref m_pose.Write;
            public ReadOnlySpan<Contact> Contacts => m_contacts.Read;
            public Span<Contact> ContactsEdit => m_contacts.Write;

            private static void Mutate(ref SlotState slot) { }
            private static void Mutate(ref Inner inner) { }
            private static void Mutate(ref long raw) { }

        """;

    private static string ReadResource(string name) {
        using var stream = typeof(DoorAnalyzerTests).Assembly.GetManifestResourceStream(name: name)!;
        using var reader = new StreamReader(stream: stream);

        return reader.ReadToEnd();
    }
    private static IEnumerable<MetadataReference> Runtime() =>
        Harness.RuntimeReferences.Where(predicate: static reference => !(reference.Display ?? string.Empty).Contains(value: "xunit", comparisonType: StringComparison.OrdinalIgnoreCase));
    private static MetadataReference Emit(string assemblyName, MetadataReference[] references, SourceFile[] sources) {
        var compilation = Harness.Compile(assemblyName: assemblyName, sources: sources).WithReferences(references: [.. Runtime(), .. references]);
        using var image = new MemoryStream();
        var result = compilation.Emit(peStream: image);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "; ", values: result.Diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error))));
        return MetadataReference.CreateFromImage(peImage: image.ToArray());
    }
    private static AnalysisResult Analyze(string source, params MetadataReference[] references) {
        var compilation = Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source))
            .WithReferences(references: [.. Runtime(), State, .. references]);
        var result = Harness.Analyze(analyzer: new DoorAnalyzer(), compilation: compilation);

        Assert.Empty(collection: result.WithId(id: "AD0001"));
        return result;
    }
    // One statement inside a body method, against the holders and the sections above.
    private static AnalysisResult Statement(string statement) =>
        Analyze(source: (Types + "    internal void Case() {\n" + statement + "\n    }\n}\n}\n"));
    private static string[] CompilerErrors(AnalysisResult result) =>
        [.. result.Compiler.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)).Select(selector: static diagnostic => diagnostic.Id).Distinct().Order(comparer: StringComparer.Ordinal)];

    // THE LAW: every write shape through a holder's read side is refused by the compiler. Each row is a shape a plain
    // field admits; the read side is a readonly reference, so each names a compiler error.
    [InlineData("Pose.X = 1;", "CS8332")]
    [InlineData("Pose.X += 1;", "CS8332")]
    [InlineData("Pose.Carry.Raw = 1;", "CS8332")]
    [InlineData("Mutate(ref Pose.Carry);", "CS8330")]
    [InlineData("Mutate(ref Pose.X);", "CS8330")]
    [InlineData("Pose.Slots[0] = 1;", "CS8331")]
    [InlineData("ref var element = ref Pose.Slots[0];", "CS8329")]
    [InlineData("Pose = default;", "CS8331")]
    [InlineData("ref var whole = ref Pose;", "CS8329")]
    [InlineData("m_pose = new(binding: m_binding);", "CS0191")]
    [InlineData("Contacts[0].Partner = 1;", "CS8332")]
    [InlineData("Contacts[0].Slots[0].NormalImpulseRaw = 1;", "CS8332")]
    [InlineData("ref var slot = ref Contacts[0].Slots[0];", "CS8329")]
    [InlineData("Mutate(ref Contacts[0].Slots[0]);", "CS8329")]
    [InlineData("Contacts[0].Slots[0] = default;", "CS8331")]
    [InlineData("Contacts[0] = default;", "CS8331")]
    [InlineData("Span<SlotState> slots = Contacts[0].Slots;", "CS9164")]
    [Theory]
    public void TheCompilerRefusesEveryWriteThroughTheReadSide(string statement, string error) {
        Assert.Equal(expected: [error], actual: CompilerErrors(result: Statement(statement: statement)));
    }
    // THE LAW: the same shapes through the write side compile and report nothing, and the two reads a reader needs
    // (a readonly ref local and a read-only span of an inline array) compile too.
    [InlineData("PoseEdit.X = 1;")]
    [InlineData("PoseEdit.X += 1;")]
    [InlineData("PoseEdit.Carry.Raw = 1;")]
    [InlineData("PoseEdit.Carry.Bump();")]
    [InlineData("Mutate(ref PoseEdit.Carry);")]
    [InlineData("PoseEdit.Slots[0] = 1;")]
    [InlineData("PoseEdit = default;")]
    [InlineData("ContactsEdit[0].Partner = 1;")]
    [InlineData("ContactsEdit[0].Slots[0].Bump();")]
    [InlineData("Mutate(ref ContactsEdit[0].Slots[0]);")]
    [InlineData("m_contacts.Resize(count: 2)[1] = default;")]
    [InlineData("ref readonly var slot = ref Contacts[0].Slots[0]; _ = slot.NormalImpulseRaw;")]
    [InlineData("ReadOnlySpan<SlotState> slots = Contacts[0].Slots; _ = slots.Length;")]
    [InlineData("_ = Pose.Carry.Peek();")]
    [InlineData("var copy = Pose.Carry; copy.Bump();")]
    [Theory]
    public void TheWriteSideAndTheReadsCompileWithNoDiagnostic(string statement) {
        var result = Statement(statement: statement);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    // THE LAW: a mutating method invoked through the read side compiles, runs on a copy, and is DOOR001.
    [InlineData("Pose.Carry.Bump();")]
    [InlineData("m_pose.Read.Carry.Bump();")]
    [InlineData("Contacts[0].Slots[0].Bump();")]
    [InlineData("m_contacts.Read[0].Slots[0].Bump();")]
    [InlineData("ref readonly var pose = ref Pose; pose.Carry.Bump();")]
    [Theory]
    public void AMutatingCallThroughTheReadSideIsDoor001(string statement) {
        var result = Statement(statement: statement);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: ["DOOR001"], actual: result.Ids);
    }
    // THE LAW: a member that turns a readonly reference into a writable one is DOOR002 in a project that declares a
    // section, whatever it is called on.
    [InlineData("Unsafe.AsRef(in Pose).X = 1;")]
    [InlineData("MemoryMarshal.GetReference(Contacts).Partner = 1;")]
    [InlineData("var bytes = new byte[4]; MemoryMarshal.GetArrayDataReference(bytes) = 1;")]
    [InlineData("var text = ImmutableArray.Create(1); ImmutableCollectionsMarshal.AsArray(text)![0] = 2;")]
    [Theory]
    public void AReinterpretingCallInAProjectThatDeclaresASectionIsDoor002(string statement) {
        var result = Statement(statement: statement);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: ["DOOR002"], actual: result.Ids);
    }
    // THE LAW: in a project that declares no section, a reinterpreting call is DOOR002 only when its type argument is
    // a section or an open type parameter; over a closed type that is no section it passes.
    [InlineData("static void Case(ReadOnlySpan<Subject.Sections.Elsewhere> span) { MemoryMarshal.GetReference(span).Value = 1; }", true)]
    [InlineData("static void Case<T>(ReadOnlySpan<T> span) where T : struct { MemoryMarshal.GetReference(span) = default; }", true)]
    [InlineData("static void Case(in Subject.Sections.Elsewhere value) { Unsafe.AsRef(in value).Value = 1; }", true)]
    [InlineData("static void Case(ReadOnlySpan<int> span) { MemoryMarshal.GetReference(span) = 1; }", false)]
    [InlineData("static System.Collections.Immutable.ImmutableArray<int> Case(int[] offsets) => ImmutableCollectionsMarshal.AsImmutableArray(offsets);", false)]
    [Theory]
    public void AReinterpretingCallElsewhereIsDoor002OnlyOverASectionOrAnOpenTypeParameter(string member, bool refused) {
        var result = Analyze(
            references: Sections,
            source: ("using System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\nnamespace Subject { internal static class Cases { " + member + " } }")
        );

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: (refused ? ["DOOR002"] : []), actual: result.Ids);
    }
    // THE LAW: a project that cannot see the holder types is held to nothing.
    [Fact]
    public void AProjectThatCannotSeeTheHoldersIsHeldToNothing() {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(Name: "Subject.cs", Text: "using System.Runtime.InteropServices;\ninternal static class Cases { static void Case<T>(ReadOnlySpan<T> span) where T : struct { MemoryMarshal.GetReference(span) = default; } }")
        ).WithReferences(references: Runtime());
        var result = Harness.Analyze(analyzer: new DoorAnalyzer(), compilation: compilation);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
    // THE LAW: a section must hold no reference to sit in a section holder, and a static encoder or derivation
    // cannot read an instance field; both are the compiler's.
    [InlineData("public struct Loose : IVersionedSection { public object Field; } internal sealed class Owner { private readonly Versioned<Loose> m_loose = new(binding: new SlotBinding()); }", "CS8377")]
    [InlineData("public struct Table : IVersionedSection { public long[] Rows; } internal sealed class Owner { private readonly Versioned<Table> m_table = new(binding: new SlotBinding()); }", "CS8377")]
    [InlineData("internal sealed class Owner { private long m_field; static long Encode(in Pose pose) => (pose.X + m_field); }", "CS0120")]
    [Theory]
    public void TheCompilerRefusesAReferenceInASectionAndAnInstanceReadInAStaticFunction(string members, string error) {
        var result = Analyze(source: (Types + "}\n" + members + "\n}\n"));

        Assert.Contains(expected: error, collection: CompilerErrors(result: result));
    }
    // THE LAW: a record-holder section's fields are deeply immutable. A mutable collection, an array or a mutable
    // struct that holds one is DOOR003; a string, an immutable array and a readonly struct of those pass.
    [InlineData("public List<int> Rows;", true)]
    [InlineData("public int[] Rows;", true)]
    [InlineData("public Mutable Rows;", true)]
    [InlineData("public string Name; public ImmutableArray<int> Rows; public Frozen Key; public long Raw; public int? Maybe;", false)]
    [Theory]
    public void ARecordSectionFieldThatIsNotImmutableIsDoor003(string fields, bool refused) {
        var result = Analyze(source: (Types + "}\n"
            + "public struct Mutable { public int[] Rows; }\n"
            + "public readonly struct Frozen { public readonly string Name; public readonly ImmutableArray<long> Values; }\n"
            + "public record struct Named : IVersionedSection { " + fields + " }\n"
            + "internal sealed class Owner { private readonly VersionedRecord<Named> m_named = new(binding: new SlotBinding()); internal ulong Version => m_named.Version; }\n}\n"));

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: (refused ? ["DOOR003"] : []), actual: result.Ids);
    }
    // THE LAW: a writable reference from the write side is used within one statement or handed to one call. Bound to
    // a ref local, a span local or a ref field it is DOOR005, through the holder or through an accessor that hands
    // its write side on.
    [InlineData("ref var pose = ref m_pose.Write; pose.X = 1;")]
    [InlineData("ref var pose = ref PoseEdit; pose.X = 1;")]
    [InlineData("ref var carry = ref PoseEdit.Carry; carry.Raw = 1;")]
    [InlineData("var contacts = m_contacts.Write; contacts[0].Partner = 1;")]
    [InlineData("Span<Contact> contacts = ContactsEdit; contacts[0].Partner = 1;")]
    [InlineData("ref var contact = ref ContactsEdit[0]; contact.Partner = 1;")]
    [InlineData("var grown = m_contacts.Resize(count: 2); grown[0].Partner = 1;")]
    [Theory]
    public void AKeptWriteReferenceIsDoor005(string statement) {
        var result = Statement(statement: statement);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: ["DOOR005"], actual: result.Ids);
    }
    // THE LAW: a test assembly is held to none of the rules.
    [Fact]
    public void ATestAssemblyIsHeldToNoRule() {
        var compilation = Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: "Subject.cs", Text: (Types + "    internal void Case() {\nPose.Carry.Bump();\n    }\n}\n}\n")))
            .WithReferences(references: [.. Harness.RuntimeReferences, State]);
        var result = Harness.Analyze(analyzer: new DoorAnalyzer(), compilation: compilation);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.Ids);
    }
}
