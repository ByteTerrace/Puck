using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws over <see cref="SdfMeshBakeClosure"/> beyond the bake's own roots: a walk from this assembly's fixture reaches what
/// its code runs (a callee in another assembly, an override behind an abstract call, a static constructor) and leaves out
/// what its code only carries, and names every shape of signature and operand the IL can hold.
/// </summary>
public sealed class SdfMeshBakeClosureLawTests {
    private static readonly (string Assembly, string Type, string? Method)[] FixtureRoot = [("Puck.SdfVm.Tests", "Puck.SdfVm.Tests.SdfMeshBakeClosureFixture", "Run")];

    [Fact]
    public void AWalkReachesWhatItsCodeRunsAndNoMore() {
        var closure = SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory, roots: FixtureRoot);
        var members = closure.Members.Keys;

        Assert.Contains(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm.Tests!Puck.SdfVm.Tests.SdfMeshBakeClosureFixture.Run`0(", comparisonType: StringComparison.Ordinal));
        // The override behind the abstract call, the static constructor of the static field read, and the callee in
        // another assembly.
        Assert.Contains(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm.Tests!Puck.SdfVm.Tests.SdfMeshBakeClosureFixture/Doubling.Apply", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm.Tests!Puck.SdfVm.Tests.SdfMeshBakeClosureFixture/Table..cctor", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm!Puck.SdfVm.SdfMeshGeometryBuilder..ctor", comparisonType: StringComparison.Ordinal));
        // A type only a parameter carries is named, never walked.
        Assert.DoesNotContain(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm.Tests!Puck.SdfVm.Tests.SdfMeshBakeClosureFixture/Carried", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: "root", actual: closure.Reasons[members.First(predicate: static member => member.Contains(value: "SdfMeshBakeClosureFixture.Run`0(", comparisonType: StringComparison.Ordinal))]);
        // The same build walks to the same closure.
        Assert.Equal(expected: closure.Hash, actual: SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory, roots: FixtureRoot).Hash);
        _ = Assert.Throws<KeyNotFoundException>(testCode: static () => SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory, roots: [("Puck.SdfVm.Tests", "Puck.SdfVm.Tests.NoSuchType", null)]));
        _ = Assert.Throws<ArgumentNullException>(testCode: static () => SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory, roots: null!));
    }
}

/// <summary>The fixture <see cref="SdfMeshBakeClosureLawTests"/> walks from: one method holding every shape of IL the
/// walk names.</summary>
public static unsafe class SdfMeshBakeClosureFixture {
    /// <summary>Runs every shape the walk names.</summary>
    /// <param name="carried">A value the method only carries.</param>
    /// <param name="choice">The case a switch takes.</param>
    /// <returns>A sum of what ran.</returns>
    public static long Run(Carried carried, int choice) {
        Transform transform = new Doubling();
        var grid = new int[2, 2];
        var bytes = Bytes;
        delegate*<int, int> pointer = &Twice;
        var total = (long)(transform.Apply(value: choice) + pointer(choice) + grid[1, 1] + bytes[7] + Table.Values[0]);

        fixed (int* first = &grid[0, 0]) { total += *first; }
        switch (choice) {
            case 0: total += 1; break;
            case 1: total += 2; break;
            case 2: total += 3; break;
            default: total += "fixture".Length; break;
        }
        try {
            total += new SdfMeshGeometryBuilder(dynamic: false).Materials.Count;
        } catch (InvalidOperationException) {
            total = -1;
        }

        return (total + ((carried is null) ? 0 : 1));
    }

    private static ReadOnlySpan<byte> Bytes => [1, 2, 3, 4, 5, 6, 7, 8];
    private static int Twice(int value) => (2 * value);

    /// <summary>A value <see cref="Run"/> carries and never calls into.</summary>
    public sealed class Carried {
        /// <summary>Gets a value no walk from <see cref="Run"/> reads.</summary>
        public int Value => Environment.ProcessorCount;
    }
    /// <summary>A transform called through its abstract member.</summary>
    public abstract class Transform {
        /// <summary>Transforms a value.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The transformed value.</returns>
        public abstract int Apply(int value);
    }
    /// <summary>The transform that doubles.</summary>
    public sealed class Doubling : Transform {
        /// <inheritdoc/>
        public override int Apply(int value) => (2 * value);
    }
    /// <summary>A table its static constructor fills.</summary>
    public static class Table {
        /// <summary>The table's values.</summary>
        public static readonly int[] Values = [Environment.ProcessorCount];
    }
}
