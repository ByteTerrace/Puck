using Puck.Physics.Fields;

namespace Puck.Physics.Tests;

/// <summary>Pins the field lattice kernel's own architecture boundary: it lives in <c>Puck.Physics</c> and the
/// assembly that carries it references no <c>Puck.World*</c> project, so the reaction integrator that used to sit
/// in <c>Puck.World.Server</c> cannot silently regain a document or server dependency.</summary>
public sealed class FieldLatticeArchitectureLawTests {
    [Fact]
    public void TheAssemblyCarryingFieldLatticeReferencesNoWorldProject() {
        var referenced = typeof(FieldLattice).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            collection: referenced,
            filter: static name => ((name.Name is { } value) && value.StartsWith(value: "Puck.World", comparisonType: StringComparison.Ordinal))
        );
    }
}
