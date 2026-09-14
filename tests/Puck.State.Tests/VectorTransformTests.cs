using System.Text.Json;
using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

public sealed class VectorTransformTests {
    [Fact]
    public void StateVector_Create_And_Base64Url_RoundTrip() {
        // Create an admissible unit vector of 8 dimensions: sqrt(127^2) = 127
        sbyte[] components = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: components, vector: out var vector, error: out var error), error);
        Assert.NotNull(vector);
        Assert.Equal(8, vector.Dimensions);
        Assert.True(vector.Components.SequenceEqual(components));

        var base64 = vector.ToBase64Url();
        Assert.True(StateVector.TryParseBase64Url(text: base64, dimensions: 8, vector: out var roundTrip, error: out var parseErr), parseErr);
        Assert.Equal(vector, roundTrip);
        Assert.Equal(vector.GetHashCode(), roundTrip.GetHashCode());

        var json = JsonSerializer.Serialize(value: vector);
        var deserialized = JsonSerializer.Deserialize<StateVector>(json: json);
        Assert.Equal(vector, deserialized);
    }

    [Fact]
    public void StateVector_Admission_Refuses_NonUnit() {
        // Zero vector
        sbyte[] zero = [0, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(StateVector.TryCreate(components: zero, vector: out _, error: out var err1));
        Assert.NotNull(err1);

        // Sub-unit vector
        sbyte[] small = [10, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(StateVector.TryCreate(components: small, vector: out _, error: out var err2));
        Assert.NotNull(err2);

        // Minus 128 is refused
        sbyte[] minus128 = [-128, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(StateVector.TryCreate(components: minus128, vector: out _, error: out var err3));
        Assert.NotNull(err3);
    }

    [Fact]
    public void VectorTransforms_Mix_Single_Term_Negation() {
        sbyte[] sourceComps = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: sourceComps, vector: out var vec, error: out _));

        ReadOnlyMemory<sbyte>[] vectors = [vec!.Components.ToArray()];
        int[] weights = [-1];
        Span<sbyte> destination = stackalloc sbyte[8];

        Assert.True(VectorTransforms.TryMix(vectors: vectors, weights: weights, destination: destination, refusal: out var refusal));
        Assert.Null(refusal);

        Assert.Equal(-127, destination[0]);
        for (var i = 1; i < 8; i++) {
            Assert.Equal(0, destination[i]);
        }
    }

    [Fact]
    public void VectorTransforms_Mix_Zero_Sum_Refused() {
        sbyte[] v1 = [127, 0, 0, 0, 0, 0, 0, 0];
        sbyte[] v2 = [-127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: v1, vector: out var vec1, error: out _));
        Assert.True(StateVector.TryCreate(components: v2, vector: out var vec2, error: out _));

        ReadOnlyMemory<sbyte>[] vectors = [vec1!.Components.ToArray(), vec2!.Components.ToArray()];
        int[] weights = [5, 5];
        Span<sbyte> destination = stackalloc sbyte[8];

        Assert.False(VectorTransforms.TryMix(vectors: vectors, weights: weights, destination: destination, refusal: out var refusal));
        Assert.Equal(RuleRefusal.VectorMixZero, refusal);
    }

    [Fact]
    public void VectorTransforms_Mix_Weight_Bounds() {
        sbyte[] v1 = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: v1, vector: out var vec1, error: out _));

        ReadOnlyMemory<sbyte>[] vectors = [vec1!.Components.ToArray()];
        Span<sbyte> destination = stackalloc sbyte[8];

        // Zero weight refused
        Assert.False(VectorTransforms.TryMix(vectors: vectors, weights: [0], destination: destination, refusal: out var refZero));
        Assert.Equal(RuleRefusal.VectorMixTerms, refZero);

        // Excess weight (> 1000) refused
        Assert.False(VectorTransforms.TryMix(vectors: vectors, weights: [1001], destination: destination, refusal: out var refExcess));
        Assert.Equal(RuleRefusal.VectorMixTerms, refExcess);

        // Under weight (< -1000) refused
        Assert.False(VectorTransforms.TryMix(vectors: vectors, weights: [-1001], destination: destination, refusal: out var refUnder));
        Assert.Equal(RuleRefusal.VectorMixTerms, refUnder);
    }

    [Fact]
    public void VectorTransforms_Mean_Basic_And_Empty() {
        Span<sbyte> destination = stackalloc sbyte[8];

        // Empty candidates refused
        Assert.False(VectorTransforms.TryMean(candidates: [], destination: destination, refusal: out var refEmpty));
        Assert.Equal(RuleRefusal.VectorMeanEmpty, refEmpty);

        // Two orthogonal unit vectors: average gives diagonal normalized
        sbyte[] v1 = [127, 0, 0, 0, 0, 0, 0, 0];
        sbyte[] v2 = [0, 127, 0, 0, 0, 0, 0, 0];
        ReadOnlyMemory<sbyte>[] candidates = [v1, v2];

        Assert.True(VectorTransforms.TryMean(candidates: candidates, destination: destination, refusal: out var refusal));
        Assert.Null(refusal);

        // Components 0 and 1 should be equal and positive
        Assert.Equal(destination[0], destination[1]);
        Assert.True(destination[0] > 80);
        Assert.True(SignedByteVectorFunctions.IsUnitAdmissible(components: destination));
    }

    [Fact]
    public void VectorTransforms_Nearest_Filtering_And_Ranking() {
        sbyte[] query = [127, 0, 0, 0, 0, 0, 0, 0];

        sbyte[] c1 = [127, 0, 0, 0, 0, 0, 0, 0];  // cosine = 1.0 (65536)
        sbyte[] c2 = [90, 90, 0, 0, 0, 0, 0, 0];   // cosine ~ 0.707
        sbyte[] c3 = [0, 127, 0, 0, 0, 0, 0, 0];   // cosine = 0.0 (0)
        sbyte[] c4 = [-127, 0, 0, 0, 0, 0, 0, 0]; // cosine = -1.0 (-65536)

        NearestCandidate[] candidates = [
            new(Key: CellName.Parse(candidate: "c1"), Components: c1),
            new(Key: CellName.Parse(candidate: "c2"), Components: c2),
            new(Key: CellName.Parse(candidate: "c3"), Components: c3),
            new(Key: CellName.Parse(candidate: "c4"), Components: c4)
        ];

        Span<VectorTransforms.NearestMatch> matches = new VectorTransforms.NearestMatch[4];

        // Top 2 nearest without filters
        var count = VectorTransforms.SelectNearest(
            candidates: candidates,
            query: query,
            isFixedScore: true,
            k: 2,
            threshold: null,
            excludeKey: null,
            farthest: false,
            results: matches
        );

        Assert.Equal(2, count);
        Assert.Equal("c1", matches[0].Key.Value);
        Assert.Equal("c2", matches[1].Key.Value);

        // Exclude c1
        count = VectorTransforms.SelectNearest(
            candidates: candidates,
            query: query,
            isFixedScore: true,
            k: 2,
            threshold: null,
            excludeKey: CellName.Parse(candidate: "c1"),
            farthest: false,
            results: matches
        );

        Assert.Equal(2, count);
        Assert.Equal("c2", matches[0].Key.Value);
        Assert.Equal("c3", matches[1].Key.Value);

        // Farthest (least similar)
        count = VectorTransforms.SelectNearest(
            candidates: candidates,
            query: query,
            isFixedScore: true,
            k: 2,
            threshold: null,
            excludeKey: null,
            farthest: true,
            results: matches
        );

        Assert.Equal(2, count);
        Assert.Equal("c4", matches[0].Key.Value);
        Assert.Equal("c3", matches[1].Key.Value);
    }

    [Fact]
    public void VectorTransforms_Remember_Rejects_Near_Duplicate() {
        sbyte[] existing = [127, 0, 0, 0, 0, 0, 0, 0];
        NearestCandidate[] cells = [
            new(Key: CellName.Parse(candidate: "old"), Components: existing)
        ];

        // Vector identical to existing: cosine = 1.0 (65536) >= 0.9 (58982)
        sbyte[] duplicate = [127, 0, 0, 0, 0, 0, 0, 0];
        var thresholdQ16 = 58982L; // ~0.9

        Assert.False(VectorTransforms.TryRemember(
            existingCells: cells,
            key: CellName.Parse(candidate: "new"),
            vector: duplicate,
            unlessWithinQ16: thresholdQ16,
            matchingKey: out var match
        ));
        Assert.Equal("old", match?.Value);

        // Orthogonal vector: cosine = 0.0 < 0.9
        sbyte[] distinct = [0, 127, 0, 0, 0, 0, 0, 0];
        Assert.True(VectorTransforms.TryRemember(
            existingCells: cells,
            key: CellName.Parse(candidate: "new"),
            vector: distinct,
            unlessWithinQ16: thresholdQ16,
            matchingKey: out _
        ));
    }

    [Fact]
    public void StateFrame_Vector_Write_Journal_Rewind() {
        var space = new StateSpace(Name: CellName.Parse(candidate: "lore"), Model: "test-model", Revision: "1", Dimensions: 8);

        sbyte[] defaultVector = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: defaultVector, vector: out var initVec, error: out _));

        var rows = new StateRow[] {
            new(
                Name: CellName.Parse(candidate: "stance"),
                Kind: CellKind.Vector,
                Domain: new StateDomain.Slot(),
                Space: "lore",
                Cells: [new(Key: StateRow.SlotKey, Vector: initVec)]
            )
        };

        var layout = new FrameLayout(
            rows: rows,
            topology: _ => null,
            spaces: name => (name == "lore") ? space : null
        );

        Assert.Equal(8, layout.VectorLength);
        Assert.Equal(1, layout.VectorCellCount);

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        Assert.True(frame.TryStoredVector(rowOrdinal: 0, key: StateRow.SlotKey, out var readComps));
        Assert.Equal(127, readComps[0]);

        // Begin journal scope and overwrite with a new vector
        sbyte[] newVector = [0, 127, 0, 0, 0, 0, 0, 0];
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryWriteVector(rowOrdinal: 0, key: StateRow.SlotKey, components: newVector, reason: out var reason), reason);
        Assert.True(frame.TryStoredVector(rowOrdinal: 0, key: StateRow.SlotKey, out var updatedComps));
        Assert.Equal(0, updatedComps[0]);
        Assert.Equal(127, updatedComps[1]);

        // Rewind journal scope: should restore original values
        frame.RewindJournalScope(mark: mark);

        Assert.True(frame.TryStoredVector(rowOrdinal: 0, key: StateRow.SlotKey, out var rewoundComps));
        Assert.Equal(127, rewoundComps[0]);
        Assert.Equal(0, rewoundComps[1]);
    }
}
