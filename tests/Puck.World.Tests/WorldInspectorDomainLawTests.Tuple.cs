using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldInspectorDomainLawTests {
    [Fact]
    public void CoupledTupleNamesTheRejectedOperandAndHeldValuesUntilRecoveryWithoutSteadyAllocation() {
        var definition = new WorldDefinition();
        var guard = new WorldValueDomainGuard();
        var group = new WorldValueDomainGroup(definition: definition, guard: guard, path: "render.clouds");
        var tuple = new WorldValueTuple(name: "shape", operands: [
            new WorldValueTupleOperand("scale", new BindableScalar(literal: .5f), .5f, WorldValueDomain.Positive),
            new WorldValueTupleOperand("height", new BindableScalar(binding: "state.height"), 2f, WorldValueDomain.Positive),
        ], predicate: static values => float.IsFinite(f: (values[1] / values[0])), requirement: "height / scale must be finite");
        var source = new TupleValues { Height = float.MaxValue };
        var values = new WorldValueResolver(definition: definition, source: source, tick: default);

        Assert.Equal(new[] { .5f, 2f }, group.Tuple(tuple: tuple, values: values).ToArray());
        var diagnostic = Assert.Single(collection: guard.Diagnostics);

        Assert.Equal("[0.5, 3.4028235E+38]", diagnostic.Values);
        Assert.Equal("[0.5, 2]", diagnostic.UsedValues);
        Assert.Contains("[0.5, 3.4028235E+38]", diagnostic.ToString());
        Assert.Contains("held [0.5, 2]", diagnostic.ToString());
        var text = new WorldInspectorText();
        var snapshot = new WorldInspectorSnapshot { ReloadError = "none" };

        void Format() {
            _ = group.Tuple(tuple: tuple, values: values);
            text.Format(snapshot: in snapshot);
            text.Diagnostics(diagnostics: guard.Diagnostics);
            text.Finish();
        }
        Format();
        Assert.False(condition: text.Refused);
        var displayed = new string(value: text.Text).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: "\n");

        Assert.Contains(actualString: displayed, expectedSubstring: "[0.5, 3.4028235E+38]");
        Assert.Contains(actualString: displayed, expectedSubstring: "held [0.5, 2]");
        var checks = guard.Checks;

        for (var index = 0; (index < 100); index++) { Format(); }
        Assert.Equal(0L, AllocationWindow.Least(() => { for (var index = 0; (index < 100); index++) { Format(); } }));
        Assert.Equal(checks, guard.Checks);
        source.Height = 3;
        Format();
        Assert.Empty(collection: guard.Diagnostics);
        Assert.DoesNotContain("render.clouds.shape", new string(value: text.Text));
        Assert.Equal(new[] { .5f, 3f }, group.Tuple(tuple: tuple, values: values).ToArray());
    }

    private sealed class TupleValues : IWorldValueSource {
        public double Height { get; set; }

        public bool TryScalar(in StateBinding binding, out double value) { value = Height; return true; }
        public bool TryColor(in StateBinding binding, out Vector4 value) { value = default; return false; }
    }
}
