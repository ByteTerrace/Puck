using Puck.Tests.Shared;

namespace Puck.Abstractions.Tests;

// An applied update moves the one code revision every product of code is held against: a table of such products forgets
// them, and a persisted identity folds in only an update that touched what its product reads, unique to this process.
[Collection(name: nameof(AppliedCodeUpdateCollection))]
public sealed class CodeRevisionLawTests {
    private sealed class Read;
    private sealed class Unread {
        public sealed class Nested;
    }

    [Fact]
    public void AnAppliedUpdateFoldsIntoTheIdentityOfWhatItTouchedAlone() {
        var reads = static (Type type) => (type == typeof(Read));
        var before = CodeRevision.AppliedTo(reads: reads);
        var revision = CodeRevision.Current;
        Type[]? announced = null;
        void Observe(Type[]? types) => announced = types;

        CodeRevision.Applied += Observe;
        try {
            AppliedCodeUpdate.Apply(typeof(Unread));
        } finally {
            CodeRevision.Applied -= Observe;
        }

        Assert.Equal(expected: (revision + 1), actual: CodeRevision.Current);
        Assert.Equal(expected: [typeof(Unread)], actual: announced);
        // An update elsewhere leaves the identity of what the product reads as it was.
        Assert.Equal(expected: before, actual: CodeRevision.AppliedTo(reads: reads));

        // A lambda's or iterator's generated type reaches the type it is nested in.
        AppliedCodeUpdate.Apply(typeof(Unread.Nested));

        var touched = CodeRevision.AppliedTo(reads: static type => (type == typeof(Unread)));

        Assert.EndsWith(expectedEndString: $".{CodeRevision.Current}", actualString: touched);

        AppliedCodeUpdate.Apply(typeof(Read));

        var applied = CodeRevision.AppliedTo(reads: reads);

        Assert.NotEqual(expected: before, actual: applied);
        Assert.EndsWith(expectedEndString: $".{CodeRevision.Current}", actualString: applied);
        // The token is the process's: a later update keeps it and moves only the revision.
        Assert.Equal(expected: touched[..touched.IndexOf(value: '.', comparisonType: StringComparison.Ordinal)], actual: applied[..applied.IndexOf(value: '.', comparisonType: StringComparison.Ordinal)]);
        Assert.Equal(expected: applied, actual: CodeRevision.AppliedToAssemblies(assemblies: new HashSet<string>(collection: [typeof(Read).Assembly.GetName().Name!])));
        Assert.Equal(expected: string.Empty, actual: CodeRevision.AppliedToAssemblies(assemblies: new HashSet<string>(collection: ["Puck.Nowhere"])));
    }
    [Fact]
    public void AnUpdateTheRuntimeDidNotNameTouchesEverything() {
        AppliedCodeUpdate.Apply(types: null!);

        Assert.EndsWith(expectedEndString: $".{CodeRevision.Current}", actualString: CodeRevision.AppliedTo(reads: static _ => false));
    }
    [Fact]
    public void ATableForgetsWhatItHeldOnceAnUpdateIsApplied() {
        var table = new CodeDerivedTable<object, string>();
        var key = new object();
        var derivations = 0;
        string Derive(object _) => $"derived {++derivations}";

        Assert.False(condition: table.TryGetValue(key: key, value: out _));
        Assert.Equal(expected: "derived 1", actual: table.GetValue(key: key, derive: Derive));
        Assert.Equal(expected: "derived 1", actual: table.GetValue(key: key, derive: Derive));
        table.AddOrUpdate(key: key, value: "held");
        Assert.True(condition: table.TryGetValue(key: key, value: out var held));
        Assert.Equal(expected: "held", actual: held);

        AppliedCodeUpdate.Apply(typeof(Read));

        Assert.False(condition: table.TryGetValue(key: key, value: out _));
        Assert.Equal(expected: "derived 2", actual: table.GetValue(key: key, derive: Derive));
        Assert.Throws<ArgumentNullException>(testCode: () => table.GetValue(key: key, derive: null!));
        Assert.Throws<ArgumentNullException>(testCode: () => table.TryGetValue(key: null!, value: out _));
        Assert.Throws<ArgumentNullException>(testCode: () => table.AddOrUpdate(key: null!, value: "x"));
        Assert.Throws<ArgumentNullException>(testCode: () => CodeRevision.AppliedTo(reads: null!));
        Assert.Throws<ArgumentNullException>(testCode: () => CodeRevision.AppliedToAssemblies(assemblies: null!));
    }
}
