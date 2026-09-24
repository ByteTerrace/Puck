using System.Buffers;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="WorkCounterReport"/>: a source's section is its name, then a <c>&lt;kind&gt; &lt;value&gt;</c>
/// line per declared kind in the source's order; its JSON carries the same counts under the full kind names; and a
/// filter selects whole dotted segments.
/// </summary>
public sealed class WorkCounterReportLawTests {
    private static readonly WorkKind Loads = new(name: "world.boot.loads", unit: "count", workClass: WorkClass.Deterministic);
    private static readonly WorkKind Bytes = new(name: "world.boot.bytes", unit: "bytes", workClass: WorkClass.Deterministic);

    private sealed class Source : IWorkCounterSource {
        public string Name => "world.boot";
        public ReadOnlySpan<WorkKind> WorkKinds => new[] { Loads, Bytes };

        public bool TryRead(WorkKind kind, out long value) {
            value = (ReferenceEquals(objA: kind, objB: Loads)
                ? 3L
                : 4096L
            );

            return true;
        }
    }

    [Fact]
    public void ASectionIsTheNameThenOneLinePerKindInOrder() =>
        Assert.Equal(
            expected: "world.boot\nworld.boot.loads 3\nworld.boot.bytes 4096\n",
            actual: WorkCounterReport.AppendSection(
                builder: new StringBuilder(),
                source: new Source()
            ).ToString()
        );
    [Fact]
    public void TheJsonCarriesTheSameCounts() {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer)) {
            WorkCounterReport.WriteSource(
                source: new Source(),
                writer: writer
            );
        }

        Assert.Equal(
            expected: """{"name":"world.boot","counts":{"world.boot.loads":3,"world.boot.bytes":4096}}""",
            actual: Encoding.UTF8.GetString(bytes: buffer.WrittenSpan)
        );
    }
    [InlineData("world.boot", "world.boot", true)]
    [InlineData("world.boot", "world", true)]
    [InlineData("world.boot.io", "world.boot", true)]
    [InlineData("world.boot", "wor", false)]
    [InlineData("world.boot", "world.boo", false)]
    [InlineData("world", "world.boot", false)]
    [InlineData("gpu", "gpu", true)]
    [Theory]
    public void AFilterSelectsWholeSegments(string name, string filter, bool matches) =>
        Assert.Equal(
            expected: matches,
            actual: WorkCounterReport.Matches(
                filter: filter,
                name: name
            )
        );
    [InlineData("world.boot", true)]
    [InlineData("gpu.sdf-engine", true)]
    [InlineData("gpu", false)]
    [InlineData("World.boot", false)]
    [InlineData("world..boot", false)]
    [Theory]
    public void ANameIsTwoOrMoreDottedSegments(string name, bool valid) =>
        Assert.Equal(
            expected: valid,
            actual: WorkKind.IsName(text: name)
        );
}
