#:project ../src/Puck.Maths/Puck.Maths.csproj

// Performance gate for QuadraticAlgebra<TScalar> against every hand-written planar number system it can reproduce. The
// correctness gate (tools/quadratic-algebra-verifier.cs) proves the generic-with-hook is bit-identical to FixedComplex
// (fused 0,-1), FixedSplit (fused 0,+1), FixedDual (plain 0,0), and QuadraticExtensionField64 (0,d over F_p). Retention
// doctrine: a hand-written type may be dropped only if the generic matches BOTH correctness AND performance; this file
// supplies the performance numbers.
//
//   dotnet run -c Release tools/quadratic-algebra-bench.cs   (the -c Release must precede the path; unoptimized numbers
//                                                              are meaningless)
//
// Method: each scenario is a dependent-chain latency loop (values kept in-regime so the measured kernel is the closed
// fast/wide path, not a magnitude-induced branch) where feasible, or a throughput loop over pre-generated operand arrays
// where a dependent chain would explode magnitudes (wide complex) or the op returns a scalar (Norm, extension inverse).
// Timing is best-of-N over an M-iteration inner loop on a monotonic Stopwatch tick clock; ns/op is best-run / op-count.
// Every generic scenario is measured twice: (static) the algebra held in a static readonly field, so the JIT may fold
// P/Q to constants after tier-up; (local) the algebra received as a by-parameter argument in a NoInlining method, so it
// cannot. The three variants of a row are ROUND-ROBINED — one timed pass each per round, each keeping its own best —
// so a monotone clock or thermal drift across the row's measured window biases all three alike instead of only the
// variant that would otherwise be measured first. Around each measured pass the GC allocation delta is accumulated; a
// nonzero total prints FAIL beside its row and then, once every row is printed, throws — so the process exits nonzero.
//
// Measurement hygiene: run quiet (no concurrent builds/loads); if two runs of a scenario disagree by >10%, rerun. The
// kernels are measured as written — Int128 widening multiply is deliberate and not "fixed" here.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;
using QFixElem = Puck.Maths.QuadraticAlgebra<Puck.Maths.FixedQ4816>.Element;
using QQuatElem = Puck.Maths.QuadraticAlgebra<Puck.Maths.FixedQuaternion>.Element;

const long LatencyIterations = 20_000_000L;
const long QuaternionIterations = 8_000_000L;
const int WidePairCount = 1024;
const int WideReps = 20_000;
const int NormReps = 20_000;
const int BatchInverseReps = 4_000;

var rng = new Random(0x5140A);
var rows = new List<Row>();
var allocationFailures = new List<string>();

Console.WriteLine($"clock: Stopwatch.Frequency = {Stopwatch.Frequency:N0} Hz ({Bench.NsPerTick:F4} ns/tick), best-of-{Bench.Runs}");
Console.WriteLine();

// ---- 1. Complex multiply, narrow regime (unit-rotation dependent chain: raw ~2^16, fast path) ----
{
    var seed = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.3));
    var rot = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.017));
    var eSeed = new QFixElem(U: seed.Real, V: seed.Imaginary);
    var eStep = new QFixElem(U: rot.Real, V: rot.Imaginary);

    rows.Add(Bench.Compare(
        scenario: "1. complex mul narrow (latency)",
        ops: LatencyIterations,
        hand: () => Loops.ComplexHand(seed: seed, rot: rot, iterations: LatencyIterations),
        genericStatic: () => Loops.ComplexFusedStatic(seed: eSeed, step: eStep, iterations: LatencyIterations),
        genericLocal: () => Loops.ComplexFusedLocal(algebra: Alg.ComplexFused, seed: eSeed, step: eStep, iterations: LatencyIterations)
    ));
}

// ---- 2. Complex multiply, wide regime (raw >= 2^31 forces the Int128 path; throughput over operand pairs) ----
{
    var handA = new FixedComplex[WidePairCount];
    var handB = new FixedComplex[WidePairCount];
    var genA = new QFixElem[WidePairCount];
    var genB = new QFixElem[WidePairCount];

    for (var i = 0; (i < WidePairCount); ++i) {
        var a = new FixedComplex(Real: FixedQ4816.FromRawBits(value: WideRaw(rng)), Imaginary: FixedQ4816.FromRawBits(value: WideRaw(rng)));
        var b = new FixedComplex(Real: FixedQ4816.FromRawBits(value: WideRaw(rng)), Imaginary: FixedQ4816.FromRawBits(value: WideRaw(rng)));

        handA[i] = a;
        handB[i] = b;
        genA[i] = new QFixElem(U: a.Real, V: a.Imaginary);
        genB[i] = new QFixElem(U: b.Real, V: b.Imaginary);
    }

    rows.Add(Bench.Compare(
        scenario: "2. complex mul wide (throughput)",
        ops: ((long)WidePairCount * WideReps),
        hand: () => Loops.ComplexHandWide(a: handA, b: handB, reps: WideReps),
        genericStatic: () => Loops.ComplexFusedWideStatic(a: genA, b: genB, reps: WideReps),
        genericLocal: () => Loops.ComplexFusedWideLocal(algebra: Alg.ComplexFused, a: genA, b: genB, reps: WideReps)
    ));
}

// ---- 3a. Split multiply, narrow regime (unit squeeze / conjugate alternation keeps the chain bounded) ----
{
    var s = FixedSplit.FromRapidity(rapidity: FixedQ4816.FromDouble(value: 0.02));
    var sConj = s.Conjugate();
    var eStep = new QFixElem(U: s.U, V: s.V);
    var eConj = new QFixElem(U: sConj.U, V: sConj.V);

    rows.Add(Bench.Compare(
        scenario: "3a. split mul narrow (latency)",
        ops: LatencyIterations,
        hand: () => Loops.SplitHand(s: s, sConj: sConj, iterations: LatencyIterations),
        genericStatic: () => Loops.SplitFusedStatic(step: eStep, conj: eConj, iterations: LatencyIterations),
        genericLocal: () => Loops.SplitFusedLocal(algebra: Alg.SplitFused, step: eStep, conj: eConj, iterations: LatencyIterations)
    ));
}

// ---- 3b. Split Norm, narrow operands. Both kernels now gate on |raw| < 2^31 and accumulate the two Q32 squares in
//          Int64 here, so the row compares the same accumulation width on both sides: what differs is that the generic
//          reads P and Q off the descriptor per call and multiplies the root square by the integer Q, while the
//          hand-written kernel has the coefficient built in. Throughput over an operand array. ----
{
    var hand = new FixedSplit[WidePairCount];
    var gen = new QFixElem[WidePairCount];

    for (var i = 0; (i < WidePairCount); ++i) {
        var value = new FixedSplit(U: FixedQ4816.FromRawBits(value: NarrowRaw(rng)), V: FixedQ4816.FromRawBits(value: NarrowRaw(rng)));

        hand[i] = value;
        gen[i] = new QFixElem(U: value.U, V: value.V);
    }

    rows.Add(Bench.Compare(
        scenario: "3b. split norm narrow (throughput)",
        ops: ((long)WidePairCount * NormReps),
        hand: () => Loops.SplitNormHand(values: hand, reps: NormReps),
        genericStatic: () => Loops.SplitNormFusedStatic(values: gen, reps: NormReps),
        genericLocal: () => Loops.SplitNormFusedLocal(algebra: Alg.SplitFused, values: gen, reps: NormReps)
    ));
}

// ---- 4. Dual over FixedQ4816 (plain 0,0): the integer lane reads the coefficients as runtime values, so it still forms
//         the root product v₁·v₂ and multiplies it by the zero Q. Its V component carries no P·root term at all, so what
//         this row prices is that one dead multiply plus the per-call descriptor loads, not a whole extra term. ----
{
    var seed = new FixedDual<FixedQ4816>(Real: FixedQ4816.One, Dual: FixedQ4816.FromDouble(value: 0.5));
    var step = new FixedDual<FixedQ4816>(Real: FixedQ4816.One, Dual: FixedQ4816.FromRawBits(value: 1L));
    var eSeed = new QFixElem(U: seed.Real, V: seed.Dual);
    var eStep = new QFixElem(U: step.Real, V: step.Dual);

    rows.Add(Bench.Compare(
        scenario: "4. dual<FixedQ4816> mul (latency)",
        ops: LatencyIterations,
        hand: () => Loops.DualFixHand(seed: seed, step: step, iterations: LatencyIterations),
        genericStatic: () => Loops.DualFixStatic(seed: eSeed, step: eStep, iterations: LatencyIterations),
        genericLocal: () => Loops.DualFixLocal(algebra: Alg.DualFix, seed: eSeed, step: eStep, iterations: LatencyIterations)
    ));
}

// ---- 5. Dual quaternion (plain 0,0 over FixedQuaternion): the decisive shape. The carrier sits outside the fused tier,
//         so the generic takes the general path, where the (0,0) degeneracy test returns early: three dispatched Hamilton
//         products (u₁u₂, u₁v₂, v₁u₂), each rounded per component, then one componentwise add. The hand-written type
//         forms the real Hamilton product once and fuses the dual part's two into ONE eight-leaf accumulation per
//         component under a single rounding, so this row prices the across-seam kernel against three rounded products. ----
{
    var rotSeed = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.6));
    var dualSeed = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.3));
    var rotStep = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One), angle: FixedQ4816.FromDouble(value: 0.02));
    // Step's dual part is zero, so the chain's real part stays a unit rotation and its dual part is only re-rotated
    // (magnitude preserved) — the operands stay in-regime while every Hamilton product still executes.
    var seed = new FixedDual<FixedQuaternion>(Real: rotSeed, Dual: dualSeed);
    var step = new FixedDual<FixedQuaternion>(Real: rotStep, Dual: FixedQuaternion.AdditiveIdentity);
    var eSeed = new QQuatElem(U: seed.Real, V: seed.Dual);
    var eStep = new QQuatElem(U: step.Real, V: step.Dual);

    rows.Add(Bench.Compare(
        scenario: "5. dual quaternion mul (latency)",
        ops: QuaternionIterations,
        hand: () => Loops.DualQuatHand(seed: seed, step: step, iterations: QuaternionIterations),
        genericStatic: () => Loops.DualQuatStatic(seed: eSeed, step: eStep, iterations: QuaternionIterations),
        genericLocal: () => Loops.DualQuatLocal(algebra: Alg.DualQuat, seed: eSeed, step: eStep, iterations: QuaternionIterations)
    ));
}

// ---- 6a. Extension field multiply: QuadraticExtensionField64.Multiply vs QuadraticAlgebra<ModP>.Multiply (latency) ----
{
    var field = PrimeField64.Create(modulus: Alg.Modulus);
    var extension = QuadraticExtensionField64.CreateCanonical(baseField: field);
    var xSeed = new QuadraticExtensionField64.Element(A: 123_456_789UL, B: 987_654_321UL);
    var xStep = new QuadraticExtensionField64.Element(A: 424_242_424UL, B: 111_111_113UL);
    var eSeed = new QuadraticAlgebra<ModP>.Element(U: new ModP(Value: xSeed.A, Modulus: Alg.Modulus), V: new ModP(Value: xSeed.B, Modulus: Alg.Modulus));
    var eStep = new QuadraticAlgebra<ModP>.Element(U: new ModP(Value: xStep.A, Modulus: Alg.Modulus), V: new ModP(Value: xStep.B, Modulus: Alg.Modulus));

    rows.Add(Bench.Compare(
        scenario: "6a. extension mul (latency)",
        ops: LatencyIterations,
        hand: () => Loops.ExtensionHand(extension: extension, seed: xSeed, step: xStep, iterations: LatencyIterations),
        genericStatic: () => Loops.ExtensionStatic(seed: eSeed, step: eStep, iterations: LatencyIterations),
        genericLocal: () => Loops.ExtensionLocal(algebra: Alg.ModAlg, seed: eSeed, step: eStep, iterations: LatencyIterations)
    ));

    // ---- 6b. Retention-critical extension operations with NO generic counterpart (structural gap, not a timing) ----
    var batch = new QuadraticExtensionField64.Element[WidePairCount];

    for (var i = 0; (i < WidePairCount); ++i) {
        // Non-zero base-field parts guarantee non-zero norm for every element (required by BatchInverse).
        batch[i] = new QuadraticExtensionField64.Element(A: (RandomResidue(rng) | 1UL), B: RandomResidue(rng));
    }

    var frobeniusResult = Bench.Measure(ops: LatencyIterations, loop: () => Loops.FrobeniusHand(extension: extension, seed: xSeed, iterations: LatencyIterations));
    var batchResult = Bench.Measure(ops: ((long)WidePairCount * BatchInverseReps), loop: () => Loops.BatchInverseHand(extension: extension, values: batch, reps: BatchInverseReps));

    if (0 != frobeniusResult.AllocDelta) { allocationFailures.Add(item: $"frobenius (latency): {frobeniusResult.AllocDelta} B"); }
    if (0 != batchResult.AllocDelta) { allocationFailures.Add(item: $"batchinverse 1024 (per-elt): {batchResult.AllocDelta} B"); }

    Console.WriteLine();
    Console.WriteLine("---- extension-only operations (QuadraticAlgebra has no counterpart) ----");
    Console.WriteLine($"  frobenius (latency)        : {frobeniusResult.NsPerOp,8:F3} ns/op   alloc {(0 == frobeniusResult.AllocDelta ? "PASS" : $"FAIL ({frobeniusResult.AllocDelta} B)")}");
    Console.WriteLine($"  batchinverse 1024 (per-elt): {batchResult.NsPerOp,8:F3} ns/op   alloc {(0 == batchResult.AllocDelta ? "PASS" : $"FAIL ({batchResult.AllocDelta} B)")}");
    Console.WriteLine("  NOTE: QuadraticAlgebra<T> exposes NO division / Inverse / BatchInverse / Pow surface at all —");
    Console.WriteLine("        Frobenius exists only as Conjugate. These are a STRUCTURAL retention gap, not a perf gap.");
}

// ---- results table ----
Console.WriteLine();
Console.WriteLine("==== results (ns/op) ====");
Console.WriteLine($"{"scenario",-36} {"hand",10} {"gen(static)",12} {"gen(local)",12} {"alloc",8}");
Console.WriteLine(new string(c: '-', count: 84));

foreach (var row in rows) {
    var allocated = ((0 != row.HandAlloc) || (0 != row.StaticAlloc) || (0 != row.LocalAlloc));

    if (allocated) { allocationFailures.Add(item: $"{row.Scenario}: hand {row.HandAlloc} B, static {row.StaticAlloc} B, local {row.LocalAlloc} B"); }

    Console.WriteLine($"{row.Scenario,-36} {row.HandNs,10:F3} {row.StaticNs,12:F3} {row.LocalNs,12:F3} {(allocated ? "FAIL" : "PASS"),8}");
}

Console.WriteLine();
Console.WriteLine("==== ratio summary (generic / hand-written; >1 means the generic is slower) ====");
Console.WriteLine($"{"retention candidate",-36} {"static/hand",12} {"local/hand",12}");
Console.WriteLine(new string(c: '-', count: 62));

foreach (var row in rows) {
    Console.WriteLine($"{row.Scenario,-36} {(row.StaticNs / row.HandNs),12:F2} {(row.LocalNs / row.HandNs),12:F2}");
}

Console.WriteLine();
Console.WriteLine($"sink guard: {Bench.Sink}");

// The allocation gate: the whole table prints first so a failing run still reports every number, then a nonzero delta
// anywhere fails the process. The retention verdicts rest on "allocation-free on both sides", which a printed word in
// a table cannot enforce.
if (0 != allocationFailures.Count) {
    throw new InvalidOperationException(message: $"allocation gate FAILED — every measured region must allocate zero bytes: {string.Join(separator: "; ", values: allocationFailures)}");
}

// Raw with |value| in [2^31, 2^40): forces the Int128 wide path in both the fused kernel and the hand-written type.
static long WideRaw(Random rng) {
    var magnitude = rng.NextInt64(minValue: (1L << 31), maxValue: (1L << 40));

    return ((0 == rng.Next(maxValue: 2)) ? magnitude : -magnitude);
}

// Raw with |value| < 2^31: the narrow window FixedSplit.Norm and NormFusedInteger both gate on, so each accumulates
// its two Q32 squares in Int64.
static long NarrowRaw(Random rng) =>
    rng.NextInt64(minValue: -((1L << 31) - 1L), maxValue: ((1L << 31) - 1L));

static ulong RandomResidue(Random rng) =>
    ((ulong)rng.NextInt64(minValue: 0L, maxValue: (long)Alg.Modulus));

// A single measured row: hand-written vs the two generic placements.
internal readonly record struct Row(
    string Scenario,
    double HandNs,
    long HandAlloc,
    double StaticNs,
    long StaticAlloc,
    double LocalNs,
    long LocalAlloc
);

// The static-readonly algebra descriptors (variant "static"): initonly fields the JIT may treat as constant.
internal static class Alg {
    public const ulong Modulus = 1_000_000_007UL;

    public static readonly QuadraticAlgebra<FixedQ4816> ComplexFused = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.NegativeOne);
    public static readonly QuadraticAlgebra<FixedQ4816> SplitFused = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.One);
    public static readonly QuadraticAlgebra<FixedQ4816> DualFix = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.Zero);
    public static readonly QuadraticAlgebra<FixedQuaternion> DualQuat = QuadraticAlgebra<FixedQuaternion>.Create(p: FixedQuaternion.AdditiveIdentity, q: FixedQuaternion.AdditiveIdentity);
    public static readonly ulong NonSquare = QuadraticExtensionField64.SmallestNonSquare(baseField: PrimeField64.Create(modulus: Modulus));
    public static readonly QuadraticAlgebra<ModP> ModAlg = QuadraticAlgebra<ModP>.Create(p: new ModP(Value: 0UL, Modulus: Modulus), q: new ModP(Value: NonSquare, Modulus: Modulus));
}

internal static class Bench {
    // The one protocol constant: every measured variant, single-row or compared, reports the best of this many timed
    // passes. The banner prints it, so re-tuning it here moves the banner and every row together.
    public const int Runs = 9;

    // Untimed passes before the first measurement, to drive the hot inner loop through OSR to Tier-1.
    private const int WarmupPasses = 3;

    public static readonly double NsPerTick = (1_000_000_000.0 / Stopwatch.Frequency);

    public static long Sink;

    public static (double NsPerOp, long AllocDelta) Measure(long ops, Func<long> loop) {
        var guard = 0L;
        var allocated = 0L;
        var best = double.MaxValue;

        Warmup(loop: loop, guard: ref guard);

        for (var run = 0; (run < Runs); ++run) {
            TimeOnce(loop: loop, guard: ref guard, best: ref best, allocated: ref allocated);
        }

        Sink ^= guard;

        return ((best / ops), allocated);
    }

    public static Row Compare(string scenario, long ops, Func<long> hand, Func<long> genericStatic, Func<long> genericLocal) {
        var loops = (Func<long>[])[hand, genericStatic, genericLocal];
        var best = (double[])[double.MaxValue, double.MaxValue, double.MaxValue];
        var allocated = new long[loops.Length];
        var guard = 0L;

        foreach (var loop in loops) { Warmup(loop: loop, guard: ref guard); }

        // Round-robin: one timed pass per variant per round, each variant keeping its own best. Taking the minimum
        // within a variant cannot correct drift BETWEEN variants, so measuring the three to completion in sequence
        // would hand the earliest-measured one the coldest, highest-boost machine state; interleaving spreads any
        // monotone drift across all three.
        for (var run = 0; (run < Runs); ++run) {
            for (var index = 0; (index < loops.Length); ++index) {
                TimeOnce(loop: loops[index], guard: ref guard, best: ref best[index], allocated: ref allocated[index]);
            }
        }

        Sink ^= guard;

        return new Row(
            Scenario: scenario,
            HandNs: (best[0] / ops),
            HandAlloc: allocated[0],
            StaticNs: (best[1] / ops),
            StaticAlloc: allocated[1],
            LocalNs: (best[2] / ops),
            LocalAlloc: allocated[2]
        );
    }

    // One timed pass: the allocation counter is read outside the tick window, so only the loop itself is timed.
    private static void TimeOnce(Func<long> loop, ref long guard, ref double best, ref long allocated) {
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        guard ^= loop();

        var elapsed = ((Stopwatch.GetTimestamp() - start) * NsPerTick);

        allocated += (GC.GetAllocatedBytesForCurrentThread() - allocBefore);
        best = Math.Min(val1: best, val2: elapsed);
    }

    private static void Warmup(Func<long> loop, ref long guard) {
        for (var pass = 0; (pass < WarmupPasses); ++pass) { guard ^= loop(); }
    }
}

internal static class Loops {
    // ---- complex, narrow ----
    public static long ComplexHand(FixedComplex seed, FixedComplex rot, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = (accumulator * rot);
            sink ^= accumulator.Real.Value;
        }

        return sink;
    }
    public static long ComplexFusedStatic(QFixElem seed, QFixElem step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = Alg.ComplexFused.Multiply(left: accumulator, right: step);
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long ComplexFusedLocal(QuadraticAlgebra<FixedQ4816> algebra, QFixElem seed, QFixElem step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.U.Value;
        }

        return sink;
    }

    // ---- complex, wide (throughput) ----
    public static long ComplexHandWide(FixedComplex[] a, FixedComplex[] b, int reps) {
        var sink = 0L;

        for (var r = 0; (r < reps); ++r) {
            for (var i = 0; (i < a.Length); ++i) {
                sink ^= (a[i] * b[i]).Real.Value;
            }
        }

        return sink;
    }
    public static long ComplexFusedWideStatic(QFixElem[] a, QFixElem[] b, int reps) {
        var sink = 0L;

        for (var r = 0; (r < reps); ++r) {
            for (var i = 0; (i < a.Length); ++i) {
                sink ^= Alg.ComplexFused.Multiply(left: a[i], right: b[i]).U.Value;
            }
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long ComplexFusedWideLocal(QuadraticAlgebra<FixedQ4816> algebra, QFixElem[] a, QFixElem[] b, int reps) {
        var sink = 0L;

        for (var r = 0; (r < reps); ++r) {
            for (var i = 0; (i < a.Length); ++i) {
                sink ^= algebra.Multiply(left: a[i], right: b[i]).U.Value;
            }
        }

        return sink;
    }

    // ---- split, narrow (latency; alternate by conjugate to keep the chain bounded) ----
    public static long SplitHand(FixedSplit s, FixedSplit sConj, long iterations) {
        var accumulator = FixedSplit.MultiplicativeIdentity;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = (accumulator * (((n & 1L) == 0L) ? s : sConj));
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    public static long SplitFusedStatic(QFixElem step, QFixElem conj, long iterations) {
        var accumulator = new QFixElem(U: FixedQ4816.One, V: FixedQ4816.Zero);
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = Alg.SplitFused.Multiply(left: accumulator, right: (((n & 1L) == 0L) ? step : conj));
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long SplitFusedLocal(QuadraticAlgebra<FixedQ4816> algebra, QFixElem step, QFixElem conj, long iterations) {
        var accumulator = new QFixElem(U: FixedQ4816.One, V: FixedQ4816.Zero);
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: (((n & 1L) == 0L) ? step : conj));
            sink ^= accumulator.U.Value;
        }

        return sink;
    }

    // ---- split Norm (throughput) ----
    public static long SplitNormHand(FixedSplit[] values, int reps) {
        var sink = 0L;

        for (var r = 0; (r < reps); ++r) {
            for (var i = 0; (i < values.Length); ++i) {
                sink ^= values[i].Norm.Value;
            }
        }

        return sink;
    }
    public static long SplitNormFusedStatic(QFixElem[] values, int reps) {
        var sink = 0L;

        for (var r = 0; (r < reps); ++r) {
            for (var i = 0; (i < values.Length); ++i) {
                sink ^= Alg.SplitFused.Norm(value: values[i]).Value;
            }
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long SplitNormFusedLocal(QuadraticAlgebra<FixedQ4816> algebra, QFixElem[] values, int reps) {
        var sink = 0L;

        for (var r = 0; (r < reps); ++r) {
            for (var i = 0; (i < values.Length); ++i) {
                sink ^= algebra.Norm(value: values[i]).Value;
            }
        }

        return sink;
    }

    // ---- dual over FixedQ4816 (latency) ----
    public static long DualFixHand(FixedDual<FixedQ4816> seed, FixedDual<FixedQ4816> step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = (accumulator * step);
            sink ^= accumulator.Dual.Value;
        }

        return sink;
    }
    public static long DualFixStatic(QFixElem seed, QFixElem step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = Alg.DualFix.Multiply(left: accumulator, right: step);
            sink ^= accumulator.V.Value;
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long DualFixLocal(QuadraticAlgebra<FixedQ4816> algebra, QFixElem seed, QFixElem step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.V.Value;
        }

        return sink;
    }

    // ---- dual quaternion (latency) ----
    public static long DualQuatHand(FixedDual<FixedQuaternion> seed, FixedDual<FixedQuaternion> step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = (accumulator * step);
            sink ^= accumulator.Dual.W.Value;
        }

        return sink;
    }
    public static long DualQuatStatic(QQuatElem seed, QQuatElem step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = Alg.DualQuat.Multiply(left: accumulator, right: step);
            sink ^= accumulator.V.W.Value;
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long DualQuatLocal(QuadraticAlgebra<FixedQuaternion> algebra, QQuatElem seed, QQuatElem step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.V.W.Value;
        }

        return sink;
    }

    // ---- extension field (latency) ----
    public static long ExtensionHand(QuadraticExtensionField64 extension, QuadraticExtensionField64.Element seed, QuadraticExtensionField64.Element step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = extension.Multiply(left: accumulator, right: step);
            sink ^= unchecked((long)accumulator.B);
        }

        return sink;
    }
    public static long ExtensionStatic(QuadraticAlgebra<ModP>.Element seed, QuadraticAlgebra<ModP>.Element step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = Alg.ModAlg.Multiply(left: accumulator, right: step);
            sink ^= unchecked((long)accumulator.V.Value);
        }

        return sink;
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    public static long ExtensionLocal(QuadraticAlgebra<ModP> algebra, QuadraticAlgebra<ModP>.Element seed, QuadraticAlgebra<ModP>.Element step, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= unchecked((long)accumulator.V.Value);
        }

        return sink;
    }

    // ---- extension-only operations ----
    public static long FrobeniusHand(QuadraticExtensionField64 extension, QuadraticExtensionField64.Element seed, long iterations) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0L; (n < iterations); ++n) {
            accumulator = extension.Frobenius(value: accumulator);
            sink ^= unchecked((long)accumulator.B);
        }

        return sink;
    }
    public static long BatchInverseHand(QuadraticExtensionField64 extension, QuadraticExtensionField64.Element[] values, int reps) {
        var sink = 0L;

        // Inverting the inverses returns the original set, so the span stays non-zero across reps with no reseeding.
        for (var r = 0; (r < reps); ++r) {
            extension.BatchInverse(values: values.AsSpan());
            sink ^= unchecked((long)values[0].A);
        }

        return sink;
    }
}

// A residue in F_p carried as a value plus its modulus (copied from tools/quadratic-algebra-verifier.cs), so the
// generic-math operators reduce without a static modulus. Identity elements carry modulus zero; each binary operation
// adopts the operative (non-zero) modulus of its operands.
internal readonly record struct ModP(ulong Value, ulong Modulus)
    : IAdditionOperators<ModP, ModP, ModP>,
      ISubtractionOperators<ModP, ModP, ModP>,
      IMultiplyOperators<ModP, ModP, ModP>,
      IUnaryNegationOperators<ModP, ModP>,
      IAdditiveIdentity<ModP, ModP>,
      IMultiplicativeIdentity<ModP, ModP> {
    static ModP IAdditiveIdentity<ModP, ModP>.AdditiveIdentity => new(Value: 0UL, Modulus: 0UL);
    static ModP IMultiplicativeIdentity<ModP, ModP>.MultiplicativeIdentity => new(Value: 1UL, Modulus: 0UL);

    public static ModP operator +(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked(left.Value + right.Value), Modulus: 0UL); }

        var sum = (left.Value + right.Value);

        return new(Value: ((sum >= modulus) ? (sum - modulus) : sum), Modulus: modulus);
    }
    public static ModP operator -(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked(left.Value - right.Value), Modulus: 0UL); }

        return new(Value: ((left.Value >= right.Value) ? (left.Value - right.Value) : ((left.Value + modulus) - right.Value)), Modulus: modulus);
    }
    public static ModP operator *(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked(left.Value * right.Value), Modulus: 0UL); }

        return new(Value: ((ulong)(((UInt128)left.Value * right.Value) % modulus)), Modulus: modulus);
    }
    public static ModP operator -(ModP value) {
        if ((0UL == value.Modulus) || (0UL == value.Value)) { return new(Value: unchecked(0UL - value.Value), Modulus: value.Modulus); }

        return new(Value: (value.Modulus - value.Value), Modulus: value.Modulus);
    }

    private static ulong Operative(ModP left, ModP right) =>
        Math.Max(val1: left.Modulus, val2: right.Modulus);
}
