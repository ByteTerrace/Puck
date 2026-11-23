using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Puck.SignedDistance;

/// <summary>A canonical, admitted shade program. Only its expression compiler can construct one; callers cannot
/// supply op words, cost, flags, identity or unchecked addresses. Values are presentation-only.</summary>
public sealed partial class SdfShadeProgram {
    /// <summary>The maximum allocated instruction count in one phase, including rematerialization.</summary>
    public const uint MaxOps = 4096;
    /// <summary>The maximum weighted work across explicit phases. Dispatch overhead and helper loops are work, not instructions.</summary>
    public const uint MaxPhaseWork = (uint)(MaxWork / 128UL);
    /// <summary>The static register-file capacity per phase.</summary>
    public const int MaxRegisters = 16;
    /// <summary>The maximum number of runtime slots; slots are data, not simultaneously live registers.</summary>
    public const int MaxParameters = 256;
    /// <summary>The maximum light walk, including bound screens, admitted by the work model.</summary>
    public const uint MaxLights = (SdfLights.MaxLights + SdfProgramBuilder.MaxScreenSurfaces);
    /// <summary>Fixed stage work: filtering, probe gather, output sanitization and composition plumbing.</summary>
    public const uint EngineWork = 4096;
    /// <summary>Work of the default wrapped Lambert and GGX light response.</summary>
    public const uint DefaultLightWork = 512;
    /// <summary>Work of the default post-light composition.</summary>
    public const uint DefaultComposeWork = 256;
    /// <summary>A provisional CPU admission bound. Step 5 measures the dispatch throughput and sets its margin.</summary>
    public const ulong MaxWork = (1UL << 32);
    /// <summary>The first header vector holding the program's identity: <see cref="IdentityVectors"/> vectors of its
    /// SHA-256, which a specialized kernel compares to select the program's straight-line arm.</summary>
    public const int IdentityVector = (LayoutVector + 1);
    /// <summary>The header vectors the identity occupies, eight words.</summary>
    public const int IdentityVectors = 2;
    /// <summary>The header's vectors: a range for each phase, the constant and parameter ranges, then the identity.</summary>
    public const int HeaderVectors = (IdentityVector + IdentityVectors);
    /// <summary>The phases of a program (<see cref="SdfShadePhase"/>), each a header vector: the vector its instructions
    /// start at, their count, their work, and a word the stage reads before the phase runs (the surface phase's the
    /// program's reads, every other phase's its assigned outputs).</summary>
    public const int PhaseCount = 4;
    /// <summary>The header vector holding the constant range and the parameter layout's range.</summary>
    public const int LayoutVector = PhaseCount;

    // Register selectors use a mask. Work admission prices the actual phases at the maximum light walk.
    private const int RegisterBoundProof = 1 / ((MaxRegisters > 0 && (MaxRegisters & (MaxRegisters - 1)) == 0) ? 1 : 0);

    private readonly uint[] m_words;
    private readonly uint[] m_parameters;
    private readonly byte[] m_identityBytes;

    private SdfShadeProgram(SdfShadeDefinition definition, uint[] words, uint[] parameters, SdfShadeType[] layout,
        SdfShadeInstruction[][] phases, SdfShadeBinding[] outputs, uint[] costs, SdfShadeStraight straight, SdfShadeReads reads, SdfShadeDemand demand) {
        _ = RegisterBoundProof;
        Name = definition.Name; Definition = definition; m_words = words; m_parameters = parameters;
        ParameterLayout = Array.AsReadOnly(array: layout);
        Phases = Array.AsReadOnly(array: phases.Select(selector: p => Array.AsReadOnly(array: p)).ToArray());
        Outputs = Array.AsReadOnly(array: outputs); Costs = Array.AsReadOnly(array: costs); Straight = straight;
        Reads = reads; Demand = demand;
        // Words include the parameter types, never their data. The table's identity covers templates and helpers.
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        hash.AppendData(data: System.Text.Encoding.UTF8.GetBytes(s: SdfShadeOps.Fingerprint));
        Span<byte> bytes = stackalloc byte[4];

        foreach (var word in words) {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: word);
            hash.AppendData(data: bytes);
        }
        m_identityBytes = hash.GetHashAndReset();
        Identity = Convert.ToHexStringLower(inArray: m_identityBytes);
        // The identity vectors are zero while hashing, then carry the complete identity for GPU dispatch.
        // Only this admission door can write them; they never include runtime parameter values.
        for (var i = 0; i < (IdentityVectors * 4); i++) {
            m_words[(IdentityVector * 4) + i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m_identityBytes.AsSpan(i * 4, 4));
        }
    }

    /// <summary>Gets the author name for cost attribution, excluded from identity.</summary>
    public string Name { get; }
    /// <summary>Gets the canonical expression document: same semantics and parameter layout, independent of spelling.</summary>
    public SdfShadeDefinition Definition { get; }
    /// <summary>Gets the identity of canonical code, layout and current op semantics.</summary>
    public string Identity { get; }
    /// <summary>The canonical identity bytes for the generated pass block.</summary>
    public ReadOnlySpan<byte> IdentityBytes => m_identityBytes;
    /// <summary>Gets the immutable parameter types in canonical slot order.</summary>
    public ReadOnlyCollection<SdfShadeType> ParameterLayout { get; }
    /// <summary>Gets the immutable instructions per phase.</summary>
    public ReadOnlyCollection<ReadOnlyCollection<SdfShadeInstruction>> Phases { get; }
    /// <summary>Returns whether the program authors a phase: whether it holds an instruction there, as the kernels'
    /// <c>sdfShadeHas</c> reads the phase's header. An omitted phase takes the engine's default; an omitted surface phase
    /// keeps the material's layers.</summary>
    /// <param name="phase">The phase.</param>
    /// <returns><see langword="true"/> when the phase holds an instruction.</returns>
    public bool Authors(SdfShadePhase phase) => (Phases[(int)phase].Count != 0);
    /// <summary>Gets output-to-register bindings.</summary>
    public ReadOnlyCollection<SdfShadeBinding> Outputs { get; }
    /// <summary>Gets explicit per-phase work as the interpreter runs it, excluding the default phases.</summary>
    public ReadOnlyCollection<uint> Costs { get; }
    /// <summary>Gets the straight-line form a specialized kernel's arm emits.</summary>
    public SdfShadeStraight Straight { get; }
    /// <summary>Gets reads derived from surviving input ops.</summary>
    public SdfShadeReads Reads { get; }
    /// <summary>Gets the conservative shadow-demand envelope.</summary>
    public SdfShadeDemand Demand { get; }
    /// <summary>Gets the complete owned vector slice, including header, ops, constants and parameter layout.</summary>
    public ReadOnlySpan<uint> Words => m_words;
    /// <summary>Gets runtime parameter data, four words per slot, packed outside the program slice.</summary>
    public ReadOnlySpan<uint> ParameterWords => m_parameters;

    /// <summary>Returns logical work per pixel with checked arithmetic, including default phases and engine work.</summary>
    /// <param name="lights">The lights each pixel's walk may visit, at most <see cref="MaxLights"/>.</param>
    /// <param name="straight"><see langword="true"/> to price the specialized kernel's straight-line arm
    /// (<see cref="SdfShadeStraight.Costs"/>), <see langword="false"/> to price the interpreter (<see cref="Costs"/>).</param>
    /// <returns>The work.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lights"/> exceeds <see cref="MaxLights"/>.</exception>
    public ulong PixelWork(uint lights, bool straight = false) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, MaxLights);
        var costs = (straight ? Straight.Costs : Costs);
        // The stage computes the default terms for the outputs a phase leaves unassigned too.
        return PixelWork(costs: costs, lights: lights);
    }
    // The ink phase is priced as if every pixel ran it; the gate on its line inputs only lowers what executes.
    private static ulong PixelWork(IReadOnlyList<uint> costs, uint lights) =>
        checked(costs[0] + lights * ((ulong)DefaultLightWork + costs[1]) + DefaultComposeWork + costs[2] + costs[3] + EngineWork);
    /// <summary>Checks a relative vector range without addition overflow. The allocation's outer boundary is separate.</summary>
    public static bool ContainsRange(uint sliceLength, uint offset, uint length) => ((offset <= sliceLength) && (length <= (sliceLength - offset)));
    /// <summary>Reads a word only within this program's slice; a refused read yields zero.</summary>
    public uint ReadWord(uint relativeWord) => ((relativeWord < m_words.Length) ? m_words[relativeWord] : 0u);
}
/// <summary>The straight-line form of an admitted program, which a specialized kernel emits: each phase's canonical values
/// once, in dependency order, with operands naming earlier values of the phase by index and constants and shape
/// descriptors addressed in the program's words as its instructions address them. Only the compiler constructs one.</summary>
public sealed class SdfShadeStraight {
    internal SdfShadeStraight(SdfShadeInstruction[][] phases, SdfShadeBinding[] outputs, uint[] costs) {
        Phases = Array.AsReadOnly(array: phases.Select(selector: static p => Array.AsReadOnly(array: p)).ToArray());
        Outputs = Array.AsReadOnly(array: outputs); Costs = Array.AsReadOnly(array: costs);
    }

    /// <summary>Gets each phase's values in dependency order; a value's <see cref="SdfShadeInstruction.Result"/> index is
    /// its position.</summary>
    public ReadOnlyCollection<ReadOnlyCollection<SdfShadeInstruction>> Phases { get; }
    /// <summary>Gets each output's binding to the value it takes.</summary>
    public ReadOnlyCollection<SdfShadeBinding> Outputs { get; }
    /// <summary>Gets each phase's work as the arm runs it: its values' work without the interpreter's per-instruction
    /// <see cref="SdfShadeOps.DispatchWork"/>.</summary>
    public ReadOnlyCollection<uint> Costs { get; }
}
/// <summary>A typed output assignment to one phase.</summary>
public sealed record SdfShadeBinding(SdfShadePhase Phase, string Name, SdfShadeOperand Value);
/// <summary>The maximum authored wrap and normal tilt. Unknown normal/light reach demands every side.</summary>
public readonly record struct SdfShadeDemand(float MaxWrap, float MaxTiltRadians, bool EverySide) {
    /// <summary>Returns the shadow-query cosine threshold including the material's unassigned base wrap.</summary>
    public float Cosine(float baseWrap) => (EverySide ? -1f :
        -MathF.Sin(x: MathF.Min(x: (MathF.PI / 2f), y: (MathF.Asin(x: Math.Clamp(MathF.Max(x: baseWrap, y: MaxWrap), 0f, 1f)) + MaxTiltRadians))));
}
