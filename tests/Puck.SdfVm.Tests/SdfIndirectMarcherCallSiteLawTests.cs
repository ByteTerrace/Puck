using System.Text.RegularExpressions;

using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Every mapCore and mapGradCore call inlines the complete field interpreter in DXIL, so a kernel's indirect queries
/// all reach the one indirect field query (<c>sdfIndirectServe</c>), called once by the procedure driver
/// (<c>sdfIndirectRun</c>), and each marcher procedure asks its sample at one point, running its resample and sign
/// witness as later phases of the same procedure. The views stage's field reads share one loop. The models below
/// replay the separate-call-site marchers and their phase loops over every answer pattern of a small field and hold
/// the sample sequence, the remaining allowance and the result equal.
/// </summary>
public sealed class SdfIndirectMarcherCallSiteLawTests {
    private const uint All = uint.MaxValue;
    private const float Epsilon = 0.001f;
    private const int Exit = 2;
    private const int Hit = 1;
    private const int Unresolved = 0;

    private static readonly float[] MarchAnswers = [float.NaN, -0.0005f, 0.0005f, 0.004f, 0.3f, -0.4f, 3f];
    private static readonly float[] SegmentAnswers = [float.NaN, -0.1f, 0.0005f, 0.004f, 0.3f, 5f];

    [InlineData("indirect/sdf-indirect-march.hlsli", "uint sdfIndirectMarchStep(")]
    [InlineData("indirect/sdf-indirect-march.hlsli", "uint sdfIndirectSegmentStep(")]
    [InlineData("indirect/sdf-indirect-alternatives.hlsli", "uint sdfIndirectConeBounceStep(")]
    [InlineData("indirect/sdf-indirect-cells.hlsli", "uint sdfIndirectLaunchStep(")]
    [Theory]
    public void EachIndirectMarcherHasOneFieldSampleSite(string path, string signature) {
        Assert.Single(collection: Regex.Matches(input: Body(path: path, signature: signature), pattern: @"\bsdfIndirectAsk\s*\("));
    }
    [Fact]
    public void TheIndirectQueriesReachOneInterpreterCallSite() {
        var serve = Body(path: "indirect/sdf-indirect-field.hlsli", signature: "void sdfIndirectServe(");

        Assert.Single(collection: Regex.Matches(input: serve, pattern: @"\bmapCore\s*\("));
        Assert.Single(collection: Regex.Matches(input: serve, pattern: @"\bmapGradCore\s*\("));
        Assert.Single(collection: Regex.Matches(input: Body(path: "indirect/sdf-indirect-run.hlsli", signature: "void sdfIndirectRun("),
            pattern: @"\bsdfIndirectServe\s*\("));
        // No indirect module calls the field anywhere else.
        var root = RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);
        var callers = Directory.EnumerateFiles(path: Path.Combine(path1: root, path2: "indirect"), searchPattern: "*.hlsli")
            .Where(predicate: path => Regex.IsMatch(input: File.ReadAllText(path: path), pattern: @"\bmap(Core|GradCore|Masked|Distance|DistanceMasked|Grad|GradMasked)?\s*\("))
            .Select(selector: static path => Path.GetFileName(path: path))
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(actual: callers, expected: new[] { "sdf-indirect-field.hlsli" });
    }
    [Fact]
    public void TheViewsStageReadsTheFieldThroughOneLoop() {
        Assert.Single(collection: Regex.Matches(input: Body(path: "passes/sdf-views-field.hlsli", signature: "SdfViewsFieldReads sdfViewsFieldReads("),
            pattern: @"\bmap(Core|Masked|Distance|DistanceMasked)?\s*\("));
        Assert.Single(collection: Regex.Matches(input: Body(path: "passes/sdf-hit-stages.hlsli", signature: "float3 sdfViewsStage("),
            pattern: @"\bsdfViewsFieldReads\s*\("));
        Assert.DoesNotMatch(expectedRegexPattern: @"\bmap(Core|Masked|Distance|DistanceMasked|Grad|GradMasked)?\s*\(",
            actualString: Body(path: "passes/sdf-light-stage.hlsli", signature: "float3 sdfLightStage("));
    }
    [Fact]
    public void TheViewsStageCallsTheDebugViewOnce() {
        Assert.Single(collection: Regex.Matches(input: Body(path: "passes/sdf-hit-stages.hlsli", signature: "float3 sdfViewsStage("),
            pattern: @"\bsdfDebugView\s*\("));
    }
    [Fact]
    public void TheMarchPhaseLoopReplaysTheSeparateCallSites() {
        var runs = 0;

        foreach (var radius in new[] { 0f, 0.01f }) {
            foreach (var mask in new[] { All, 5u }) {
                for (var pattern = 0; (pattern < 2401); pattern++) {
                    var answers = Digits(choices: MarchAnswers, count: 4, pattern: pattern);

                    for (var gradients = 0; (gradients < 4); gradients++) {
                        bool[] normals = [((gradients & 1) != 0), ((gradients & 2) != 0)];

                        for (var budget = 0; (budget <= 14); budget++) {
                            var expected = SeparateMarch(answers: answers, budget: budget, mask: mask, normals: normals, radius: radius);
                            var actual = PhaseMarch(answers: answers, budget: budget, mask: mask, normals: normals, radius: radius);

                            Assert.Equal(expected: expected.Queries, actual: actual.Queries);
                            Assert.Equal(expected: (expected.Budget, expected.Kind, expected.Distance, expected.Material, expected.Steps),
                                actual: (actual.Budget, actual.Kind, actual.Distance, actual.Material, actual.Steps));
                            runs++;
                        }
                    }
                }
            }
        }
        Assert.Equal(actual: runs, expected: ((((2 * 2) * 2401) * 4) * 15));
    }
    [Fact]
    public void TheSegmentPhaseLoopReplaysTheSeparateCallSites() {
        for (var pattern = 0; (pattern < 1296); pattern++) {
            var answers = Digits(choices: SegmentAnswers, count: 4, pattern: pattern);

            for (var budget = 0; (budget <= 20); budget++) {
                var expected = SeparateSegment(answers: answers, budget: budget, length: 0.5f);
                var actual = PhaseSegment(answers: answers, budget: budget, length: 0.5f);

                Assert.Equal(expected: expected.Queries, actual: actual.Queries);
                Assert.Equal(expected: (expected.Budget, expected.Clear, expected.Blocked, expected.Steps),
                    actual: (actual.Budget, actual.Clear, actual.Blocked, actual.Steps));
            }
        }
    }

    // The field answers its queries in order from the cycled pattern; the query's identity is its point and mask.
    private sealed class Field(float[] answers, bool[] normals) {
        public int Gradients { get; private set; }
        public List<(float Along, float Offset, uint Mask)> Queries { get; } = [];

        public float Sample(float along, float offset, uint mask) {
            Queries.Add(item: (along, offset, mask));
            return answers[((Queries.Count - 1) % answers.Length)];
        }
        public bool Gradient(float along) {
            Queries.Add(item: (along, float.NegativeInfinity, 0u));
            return normals[(Gradients++ % normals.Length)];
        }
    }
    private sealed record MarchResult(List<(float Along, float Offset, uint Mask)> Queries, int Budget, int Kind, float Distance, int Material, int Steps);
    private sealed record SegmentResult(List<(float Along, float Offset, uint Mask)> Queries, int Budget, bool Clear, float Blocked, int Steps);

    private const float Far = 1.5f;
    private const int MarchSteps = 6;
    private const float Reach = 0.5f;

    private static bool Masked(float travelled, uint mask) => ((mask != All) && (travelled < Reach));
    private static float Advance(float clearance, float travelled, uint mask) =>
        MathF.Max(x: 0f, y: MathF.Min(x: clearance, y: ((Masked(mask: mask, travelled: travelled) ? MathF.Min(x: Reach, y: Far) : Far) - travelled)));
    // sdfIndirectMarch with its step sample, full-field resample and witness at three call sites.
    private static MarchResult SeparateMarch(float[] answers, bool[] normals, uint mask, float radius, int budget) {
        var field = new Field(answers: answers, normals: normals);
        var distance = 0f;
        var steps = 0;
        var gradientUsed = false;
        var normal = false;

        MarchResult Done(int kind, int material = 0) => new(Queries: field.Queries, Budget: budget, Kind: kind, Distance: distance, Material: material, Steps: steps);

        for (var step = 0; ((step < MarchSteps) && (budget > 0)); step++) {
            budget--;
            steps++;
            var activeMask = (Masked(mask: mask, travelled: distance) ? mask : All);
            var sample = field.Sample(along: distance, mask: activeMask, offset: 0f);
            var material = field.Queries.Count;
            var clearance = (sample - radius);

            if (!float.IsFinite(f: sample) || ((sample < 0f) && (distance == 0f))) { return Done(kind: Unresolved); }
            if (MathF.Abs(x: sample) <= (radius + Epsilon)) {
                if (activeMask != All) {
                    if (budget == 0) { return Done(kind: Unresolved); }
                    budget--;
                    sample = field.Sample(along: distance, mask: All, offset: 0f);
                    material = field.Queries.Count;
                    clearance = (sample - radius);
                    if (!float.IsFinite(f: sample) || ((sample < 0f) && (distance == 0f))) { return Done(kind: Unresolved); }
                }
                if (!gradientUsed && (budget > 0)) { budget--; normal = field.Gradient(along: distance); gradientUsed = true; }
                if (normal && (budget > 0)) {
                    budget--;
                    var offset = (((sample < 0f) ? 1f : -1f) * (radius + Epsilon));
                    var witness = field.Sample(along: distance, mask: All, offset: offset);

                    if (float.IsFinite(f: witness) && ((sample < 0f) ? (witness > 0f) : (witness <= 0f))) { return Done(kind: Hit, material: material); }
                }
            }
            if ((distance >= Far) && (sample > radius)) { return Done(kind: Exit); }
            var advance = Advance(clearance: clearance, mask: mask, travelled: distance);

            if (advance <= 0f) { return Done(kind: Unresolved); }
            var strictlyClearEnd = (clearance > (Far - distance));

            distance += advance;
            if ((distance >= Far) && strictlyClearEnd) { return Done(kind: Exit); }
        }
        return Done(kind: Unresolved);
    }
    // The kernel's phase loop around its one call site.
    private static MarchResult PhaseMarch(float[] answers, bool[] normals, uint mask, float radius, int budget) {
        const int StepPhase = 0;
        const int ResamplePhase = 1;
        const int WitnessPhase = 2;
        var field = new Field(answers: answers, normals: normals);
        var distance = 0f;
        var steps = 0;
        var gradientUsed = false;
        var normal = false;
        var step = 0;
        var phase = StepPhase;
        var activeMask = All;
        var witnessOffset = 0f;
        var sample = 0f;
        var material = 0;
        var clearance = 0f;

        MarchResult Done(int kind, int hitMaterial = 0) => new(Queries: field.Queries, Budget: budget, Kind: kind, Distance: distance, Material: hitMaterial, Steps: steps);

        for (; ; ) {
            var offset = 0f;
            var queryMask = All;

            if (phase == StepPhase) {
                if (!((step < MarchSteps) && (budget > 0))) { break; }
                budget--;
                steps++;
                activeMask = (Masked(mask: mask, travelled: distance) ? mask : All);
                queryMask = activeMask;
            } else if (phase == WitnessPhase) {
                offset = witnessOffset;
            }
            var query = field.Sample(along: distance, mask: queryMask, offset: offset);

            if (phase == WitnessPhase) {
                if (float.IsFinite(f: query) && ((sample < 0f) ? (query > 0f) : (query <= 0f))) { return Done(hitMaterial: material, kind: Hit); }
            } else {
                sample = query;
                material = field.Queries.Count;
                clearance = (sample - radius);
                if (!float.IsFinite(f: sample) || ((sample < 0f) && (distance == 0f))) { return Done(kind: Unresolved); }
                if ((phase == ResamplePhase) || (MathF.Abs(x: sample) <= (radius + Epsilon))) {
                    if ((phase == StepPhase) && (activeMask != All)) {
                        if (budget == 0) { return Done(kind: Unresolved); }
                        budget--;
                        phase = ResamplePhase;
                        continue;
                    }
                    if (!gradientUsed && (budget > 0)) { budget--; normal = field.Gradient(along: distance); gradientUsed = true; }
                    if (normal && (budget > 0)) {
                        budget--;
                        witnessOffset = (((sample < 0f) ? 1f : -1f) * (radius + Epsilon));
                        phase = WitnessPhase;
                        continue;
                    }
                }
            }
            phase = StepPhase;
            step++;
            if ((distance >= Far) && (sample > radius)) { return Done(kind: Exit); }
            var advance = Advance(clearance: clearance, mask: mask, travelled: distance);

            if (advance <= 0f) { return Done(kind: Unresolved); }
            var strictlyClearEnd = (clearance > (Far - distance));

            distance += advance;
            if ((distance >= Far) && strictlyClearEnd) { return Done(kind: Exit); }
        }
        return Done(kind: Unresolved);
    }

    private const int SegmentSteps = 6;

    // sdfIndirectSegment with its step sample and bracket witness at two call sites.
    private static SegmentResult SeparateSegment(float[] answers, float length, int budget) {
        var field = new Field(answers: answers, normals: [false]);
        var travel = 0f;
        var steps = 0;

        SegmentResult Done(bool clear, float blocked = float.NaN) => new(Queries: field.Queries, Budget: budget, Clear: clear, Blocked: blocked, Steps: steps);

        for (var step = 0; ((step < SegmentSteps) && (budget > 0)); step++) {
            budget--;
            steps++;
            var sample = field.Sample(along: travel, mask: All, offset: 0f);
            var clearance = sample;

            if (sample <= 0f) { return Done(blocked: travel, clear: false); }
            if (!float.IsFinite(f: clearance) || (clearance <= 0f)) { return Done(clear: false); }
            if (clearance > (length - travel)) { return Done(clear: true); }
            if (travel >= length) { return Done(clear: true); }
            if ((sample <= Epsilon) && (budget > 0)) {
                var bracketEnd = MathF.Min(x: length, y: (travel + Epsilon));

                if (bracketEnd > travel) {
                    budget--;
                    steps++;
                    var inside = field.Sample(along: bracketEnd, mask: All, offset: 0f);

                    if (float.IsFinite(f: inside) && (inside <= 0f)) {
                        var weight = (sample / (sample - inside));

                        return Done(clear: false, blocked: float.Lerp(value1: travel, value2: bracketEnd, amount: Math.Clamp(max: 1f, min: 0f, value: weight)));
                    }
                }
            }
            travel = MathF.Min(x: length, y: (travel + clearance));
        }
        return Done(clear: false);
    }
    // The kernel's phase loop around its one call site.
    private static SegmentResult PhaseSegment(float[] answers, float length, int budget) {
        var field = new Field(answers: answers, normals: [false]);
        var travel = 0f;
        var steps = 0;
        var step = 0;
        var bracket = false;
        var bracketEnd = 0f;
        var sample = 0f;
        var clearance = 0f;

        SegmentResult Done(bool clear, float blocked = float.NaN) => new(Queries: field.Queries, Budget: budget, Clear: clear, Blocked: blocked, Steps: steps);

        for (; ; ) {
            float position;

            if (!bracket) {
                if (!((step < SegmentSteps) && (budget > 0))) { break; }
                budget--;
                steps++;
                position = travel;
            } else {
                position = bracketEnd;
            }
            var query = field.Sample(along: position, mask: All, offset: 0f);

            if (bracket) {
                bracket = false;
                if (float.IsFinite(f: query) && (query <= 0f)) {
                    var weight = (sample / (sample - query));

                    return Done(clear: false, blocked: float.Lerp(value1: travel, value2: bracketEnd, amount: Math.Clamp(max: 1f, min: 0f, value: weight)));
                }
            } else {
                sample = query;
                clearance = sample;
                if (sample <= 0f) { return Done(blocked: position, clear: false); }
                if (!float.IsFinite(f: clearance) || (clearance <= 0f)) { return Done(clear: false); }
                if (clearance > (length - travel)) { return Done(clear: true); }
                if (travel >= length) { return Done(clear: true); }
                if ((sample <= Epsilon) && (budget > 0)) {
                    bracketEnd = MathF.Min(x: length, y: (travel + Epsilon));
                    if (bracketEnd > travel) {
                        budget--;
                        steps++;
                        bracket = true;
                        continue;
                    }
                }
            }
            travel = MathF.Min(x: length, y: (travel + clearance));
            step++;
        }
        return Done(clear: false);
    }
    private static float[] Digits(int pattern, float[] choices, int count) {
        var digits = new float[count];

        for (var index = 0; (index < count); index++) {
            digits[index] = choices[(pattern % choices.Length)];
            pattern /= choices.Length;
        }
        return digits;
    }
    // A function's body, from its signature to the closing brace at the start of a line.
    private static string Body(string path, string signature) {
        var source = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"src/Puck.SdfVm/Assets/Shaders/Sdf/{path}"));
        var start = source.IndexOf(comparisonType: StringComparison.Ordinal, value: signature);

        Assert.True(condition: (start >= 0), userMessage: $"{path} declares {signature}");
        var end = source.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: start, value: "\n}");

        Assert.True(condition: (end > start));
        return source[start..end];
    }
}
