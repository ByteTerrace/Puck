using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Reed–Solomon over GF(2^8) at the QR code's thirty-symbol shape: the generator build and the syndrome sweep, whose
// consecutive roots now step by one field multiply each.
[MemoryDiagnoser]
public class ReedSolomonKernels {
    private const int CheckSymbolCount = 30;
    private const int MessageLength = 100;

    private BinaryField<byte> m_field;
    private byte[] m_generator = [];
    private byte[] m_message = [];
    private byte[] m_codeword = [];
    private byte[] m_syndromes = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_field = BinaryField<byte>.Create(degree: 8, reductionTail: 0x1D);
        m_generator = new byte[(CheckSymbolCount + 1)];
        m_message = new byte[MessageLength];
        m_codeword = new byte[(MessageLength + CheckSymbolCount)];
        m_syndromes = new byte[CheckSymbolCount];
        rng.NextBytes(buffer: m_message);
        ReedSolomon.BuildGenerator(field: m_field, firstRootExponent: 0, generator: m_generator, rootBase: ((byte)2));
        m_message.CopyTo(array: m_codeword, index: 0);
        ReedSolomon.ComputeCheckSymbols(checkSymbols: m_codeword.AsSpan(start: MessageLength), field: m_field, generator: m_generator, message: m_message);
    }
    [Benchmark]
    public byte BuildGenerator() {
        ReedSolomon.BuildGenerator(field: m_field, firstRootExponent: 0, generator: m_generator, rootBase: ((byte)2));

        return m_generator[1];
    }
    [Benchmark]
    public byte ComputeSyndromes() {
        ReedSolomon.ComputeSyndromes(codeword: m_codeword, field: m_field, firstRootExponent: 0, rootBase: ((byte)2), syndromes: m_syndromes);

        return m_syndromes[0];
    }
}
// The prime-field batch inverse (Montgomery-form kernel) against one inversion per element.
[MemoryDiagnoser]
public class PrimeFieldBatchInverse {
    private const int Count = 1024;

    private PrimeField64 m_field;
    private ulong[] m_values = [];
    private ulong[] m_scratch = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_field = PrimeField64.Create(modulus: NumberTheoreticTransform.Modulus);
        m_values = new ulong[Count];
        m_scratch = new ulong[Count];

        for (var i = 0; (i < Count); ++i) { m_values[i] = (m_field.Reduce(value: ((ulong)rng.NextInt64())) | 1UL); }
    }
    [Benchmark(Baseline = true)]
    public ulong OneByOne() {
        var sink = 0UL;

        for (var i = 0; (i < Count); ++i) { sink ^= m_field.Inverse(value: m_values[i]); }

        return sink;
    }
    [Benchmark]
    public ulong Batch() {
        m_values.CopyTo(array: m_scratch, index: 0);
        m_field.BatchInverse(values: m_scratch);

        return m_scratch[0];
    }
}
