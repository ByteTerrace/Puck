using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;

using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using RuleExpressions = Puck.State.Rules.RuleExpressions;

namespace Puck.Cli.Bench;

/// <summary>Which numeric kind a State kernel scenario drives its entry point in.</summary>
public enum KernelKind {
    /// <summary>The Int carrier.</summary>
    Int,
    /// <summary>The raw Q48.16 carrier.</summary>
    Fixed,
}
/// <summary>How wide the operands a State kernel scenario feeds are.</summary>
public enum KernelOperands {
    /// <summary>Operands inside the machine-word lane, where no widened intermediate is needed.</summary>
    Small,
    /// <summary>Operands that force the widened intermediate and the checked narrowing.</summary>
    Wide,
}
/// <summary>Whether a State kernel scenario feeds operands the entry point accepts or refuses.</summary>
public enum KernelOutcome {
    /// <summary>Every operand evaluates and narrows.</summary>
    Successful,
    /// <summary>Every operand takes the entry point's refusal path.</summary>
    Refused,
}
/// <summary>How a compiled expression program reaches its operand values.</summary>
public enum KernelRead {
    /// <summary>A compile-time literal.</summary>
    Literal,
    /// <summary>A state cell under a key the program carries.</summary>
    Direct,
    /// <summary>A state cell under a key another cell's value resolves.</summary>
    Indirect,
}
/// <summary>Whether a compiled expression program's tokens depend on each other's results.</summary>
public enum KernelShape {
    /// <summary>Each operation consumes the previous one's result.</summary>
    DependentChain,
    /// <summary>Independent operand pairs, folded only at the end.</summary>
    IndependentEvaluations,
}
// The isolated arithmetic entry points the reference schedule prices: one call per element, so the measurement
// carries the entry point's own dispatch, stack traffic, checks and refusal path and nothing else. Operand arrays
// are filled from a seeded generator so no operand folds at compile time.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 4)]
public class StateArithmeticKernels {
    private const int Count = 1024;

    private long[] m_left = [];
    private long[] m_right = [];
    private long[] m_offsets = [];
    private long[] m_widths = [];

    /// <summary>Gets or sets the numeric kind the entry point is driven in.</summary>
    [Params(KernelKind.Int, KernelKind.Fixed)]
    public KernelKind Kind { get; set; }
    /// <summary>Gets or sets the operand width the scenario feeds.</summary>
    [Params(KernelOperands.Small, KernelOperands.Wide)]
    public KernelOperands OperandWidth { get; set; }
    /// <summary>Gets or sets whether the scenario feeds accepted or refused operands.</summary>
    [Params(KernelOutcome.Successful, KernelOutcome.Refused)]
    public KernelOutcome Outcome { get; set; }

    /// <summary>Fills the operand arrays for the selected scenario.</summary>
    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_left = new long[Count];
        m_right = new long[Count];
        m_offsets = new long[Count];
        m_widths = new long[Count];

        for (var i = 0; (i < Count); ++i) {
            m_left[i] = ((OperandWidth == KernelOperands.Small)
                ? Operands.NarrowRaw(rng: rng)
                : Operands.WideRaw(rng: rng)
            );
            m_right[i] = ((Outcome == KernelOutcome.Refused)
                ? 0L
                : (((OperandWidth == KernelOperands.Small)
                ? Operands.NarrowRaw(rng: rng)
                : Operands.WideRaw(rng: rng)) | 1L)
            );
            m_offsets[i] = ((Outcome == KernelOutcome.Refused)
                ? 64L
                : rng.NextInt64(
                    maxValue: 64L,
                    minValue: 0L
                )
            );
            m_widths[i] = ((Outcome == KernelOutcome.Refused)
                ? 0L
                : (64L - m_offsets[i])
            );
        }
        if (Outcome != KernelOutcome.Refused) {
            return;
        }
        // Negate and Abs refuse only at the carrier's minimum, which no operand distribution reaches by chance.
        for (var i = 0; (i < Count); ++i) { m_left[i] = long.MinValue; }
    }
    /// <summary>Drives the binary entry point's add arm.</summary>
    [Benchmark(Baseline = true)]
    public long BinaryAdd() => Binary(operation: ExpressionOp.Add);
    /// <summary>Drives the binary entry point's divide arm, whose Fixed lowering calls into Puck.Maths.</summary>
    [Benchmark]
    public long BinaryDivide() => Binary(operation: ExpressionOp.Divide);
    /// <summary>Drives the binary entry point's multiply arm.</summary>
    [Benchmark]
    public long BinaryMultiply() => Binary(operation: ExpressionOp.Multiply);
    /// <summary>Drives the binary entry point's parallel-bit-extract arm, whose fallback loops over the mask.</summary>
    [Benchmark]
    public long BinaryParallelBitExtract() => Binary(operation: ExpressionOp.ParallelBitExtract);
    /// <summary>Drives the binary entry point's shift arm, whose shift count is range-checked.</summary>
    [Benchmark]
    public long BinaryShiftLeft() => Binary(operation: ExpressionOp.ShiftLeft);
    /// <summary>Drives the bit-field extract entry point.</summary>
    [Benchmark]
    public long BitField() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) {
            sink ^= (ExpressionArithmetic.TryBitField(
                field: out var field,
                offset: m_offsets[i],
                value: m_left[i],
                width: m_widths[i]
            )
                ? field
                : 1L
            );
        }

        return sink;
    }
    /// <summary>Drives the bit-field insert entry point.</summary>
    [Benchmark]
    public long BitInsert() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) {
            sink ^= (ExpressionArithmetic.TryBitInsert(
                field: m_right[i],
                inserted: out var inserted,
                offset: m_offsets[i],
                value: m_left[i],
                width: m_widths[i]
            )
                ? inserted
                : 1L
            );
        }

        return sink;
    }
    /// <summary>Drives the unary entry point's magnitude arm.</summary>
    [Benchmark]
    public long UnaryAbs() => Unary(operation: ExpressionOp.Abs);
    /// <summary>Drives the unary entry point's bit-reversal arm.</summary>
    [Benchmark]
    public long UnaryBitReverse() => Unary(operation: ExpressionOp.BitReverse);
    /// <summary>Drives the unary entry point's negation arm.</summary>
    [Benchmark]
    public long UnaryNegate() => Unary(operation: ExpressionOp.Negate);
    /// <summary>Drives the unary entry point's set-bit-count arm.</summary>
    [Benchmark]
    public long UnaryPopCount() => Unary(operation: ExpressionOp.PopCount);

    private long Binary(ExpressionOp operation) {
        var kind = ((Kind == KernelKind.Int)
            ? CellKind.Int
            : CellKind.Fixed
        );
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) {
            sink ^= (ExpressionArithmetic.TryBinary(
                operation: operation,
                kind: kind,
                left: m_left[i],
                right: m_right[i],
                value: out var value
            )
                ? value
                : 1L
            );
        }

        return sink;
    }
    private long Unary(ExpressionOp operation) {
        var kind = ((Kind == KernelKind.Int)
            ? CellKind.Int
            : CellKind.Fixed
        );
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) {
            sink ^= (ExpressionArithmetic.TryUnary(
                operation: operation,
                kind: kind,
                operand: m_left[i],
                value: out var value
            )
                ? value
                : 1L
            );
        }

        return sink;
    }
}
// The compiled-program evaluator at the capacities a document may actually reach: zero, one, every power of two
// below the declared maximum with its neighbours, and the maximum itself. Data preparation stays outside the timed
// body; only the evaluation is measured.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class StateExpressionKernels {
    private CompiledExpressionToken[] m_program = [];
    private StateKernelReader? m_reader;

    /// <summary>Gets or sets the token count the compiled program carries.</summary>
    [Params(0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256)]
    public int Tokens { get; set; }
    /// <summary>Gets or sets how the program reaches its operand values.</summary>
    [Params(KernelRead.Literal, KernelRead.Direct, KernelRead.Indirect)]
    public KernelRead Read { get; set; }
    /// <summary>Gets or sets whether the program's operations depend on each other.</summary>
    [Params(KernelShape.DependentChain, KernelShape.IndependentEvaluations)]
    public KernelShape Shape { get; set; }

    /// <summary>Builds the compiled program and the reader it evaluates against.</summary>
    [GlobalSetup]
    public void Setup() {
        m_reader = new StateKernelReader(cells: StateKernelReader.CellCount);
        m_program = StateKernelPrograms.Build(
            read: Read,
            reader: m_reader,
            shape: Shape,
            tokens: Tokens
        );
    }
    /// <summary>Evaluates the compiled program once.</summary>
    [Benchmark]
    public long Evaluate() {
        _ = RuleExpressions.TryEvaluate(
            fault: out _,
            kind: CellKind.Int,
            program: m_program,
            reader: m_reader,
            value: out var value
        );

        return value;
    }
}
// The bulk shapes a rule pass drives over a row's cells, at the same capacity breakpoints: a contiguous scan, a
// contiguous copy, a contiguous clear, and the runtime sort a latch's key ordering calls. Each capacity is a
// separate scenario so the per-element and setup terms can be separated rather than fitted to one convenient size.
[MemoryDiagnoser]
public class StateBulkKernels {
    private long[] m_destination = [];
    private long[] m_source = [];
    private long[] m_keys = [];
    private long[] m_scratch = [];

    /// <summary>Gets or sets the element count the scenario operates over.</summary>
    [Params(0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 512, 513, 1023, 1024)]
    public int Capacity { get; set; }

    /// <summary>Fills the source arrays for the selected capacity.</summary>
    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_destination = new long[Capacity];
        m_source = new long[Capacity];
        m_keys = new long[Capacity];
        m_scratch = new long[Capacity];

        for (var i = 0; (i < Capacity); ++i) {
            m_source[i] = Operands.NarrowRaw(rng: rng);
            m_keys[i] = Operands.NarrowRaw(rng: rng);
        }
    }
    /// <summary>Clears a contiguous block.</summary>
    [Benchmark]
    public long Clear() {
        m_destination.AsSpan().Clear();

        return m_destination.Length;
    }
    /// <summary>Copies a contiguous block.</summary>
    [Benchmark]
    public long Copy() {
        m_source.AsSpan().CopyTo(destination: m_destination);

        return m_destination.Length;
    }
    /// <summary>Scans a contiguous block, folding every element.</summary>
    [Benchmark]
    public long Scan() {
        var sink = 0L;

        foreach (var value in m_source) { sink += value; }

        return sink;
    }
    /// <summary>Sorts a contiguous block through the runtime sort a latch's key ordering calls.</summary>
    [Benchmark]
    public long Sort() {
        m_keys.AsSpan().CopyTo(destination: m_scratch);
        m_scratch.AsSpan().Sort();

        return ((m_scratch.Length == 0)
            ? 0L
            : m_scratch[0]
        );
    }
}
