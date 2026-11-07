using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Puck.SignedDistance;

/// <summary>A canonical, admitted shade program. Only its expression compiler can construct one; callers cannot
/// supply op words, cost, flags, identity or unchecked addresses. Values are presentation-only.</summary>
public sealed partial class SdfShadeProgram {
    /// <summary>The maximum weighted instruction work across the three explicit phases.</summary>
    public const uint MaxOps = 16384;
    /// <summary>The static register-file capacity per phase.</summary>
    public const int MaxRegisters = 8;
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

    // Compile-time proof: even a whole 8x8 group at the largest admitted light walk fits. A false inequality divides by zero.
    private const int WorkBoundProof = (1 / (((MaxRegisters > 0) && ((MaxRegisters & (MaxRegisters - 1)) == 0) && (MaxOps <= MaxWork) &&
        ((64UL * ((((MaxOps * (MaxLights + 2UL)) + (MaxLights * DefaultLightWork)) + DefaultComposeWork) + EngineWork)) <= MaxWork)) ? 1 : 0));

    private readonly uint[] m_words;
    private readonly uint[] m_parameters;

    private SdfShadeProgram(SdfShadeDefinition definition, uint[] words, uint[] parameters, SdfShadeType[] layout,
        SdfShadeInstruction[][] phases, SdfShadeBinding[] outputs, uint[] costs, SdfShadeReads reads, SdfShadeDemand demand) {
        _ = WorkBoundProof;
        Name = definition.Name; Definition = definition; m_words = words; m_parameters = parameters;
        ParameterLayout = Array.AsReadOnly(array: layout);
        Phases = Array.AsReadOnly(array: phases.Select(selector: p => Array.AsReadOnly(array: p)).ToArray());
        Outputs = Array.AsReadOnly(array: outputs); Costs = Array.AsReadOnly(array: costs); Reads = reads; Demand = demand;
        // Words include the parameter types, never their data. The table's identity covers templates and helpers.
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        hash.AppendData(data: System.Text.Encoding.UTF8.GetBytes(s: SdfShadeOps.Fingerprint));
        Span<byte> bytes = stackalloc byte[4];

        foreach (var word in words) {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: word);
            hash.AppendData(data: bytes);
        }
        Identity = Convert.ToHexStringLower(inArray: hash.GetHashAndReset());
    }

    /// <summary>Gets the author name for cost attribution, excluded from identity.</summary>
    public string Name { get; }
    /// <summary>Gets the canonical expression document: same semantics and parameter layout, independent of spelling.</summary>
    public SdfShadeDefinition Definition { get; }
    /// <summary>Gets the identity of canonical code, layout and current op semantics.</summary>
    public string Identity { get; }
    /// <summary>Gets the immutable parameter types in canonical slot order.</summary>
    public ReadOnlyCollection<SdfShadeType> ParameterLayout { get; }
    /// <summary>Gets the immutable instructions per phase.</summary>
    public ReadOnlyCollection<ReadOnlyCollection<SdfShadeInstruction>> Phases { get; }
    /// <summary>Gets output-to-register bindings.</summary>
    public ReadOnlyCollection<SdfShadeBinding> Outputs { get; }
    /// <summary>Gets explicit per-phase work, excluding the default phases.</summary>
    public ReadOnlyCollection<uint> Costs { get; }
    /// <summary>Gets reads derived from surviving input ops.</summary>
    public SdfShadeReads Reads { get; }
    /// <summary>Gets the conservative shadow-demand envelope.</summary>
    public SdfShadeDemand Demand { get; }
    /// <summary>Gets the complete owned vector slice, including header, ops, constants and parameter layout.</summary>
    public ReadOnlySpan<uint> Words => m_words;
    /// <summary>Gets runtime parameter data, four words per slot, packed outside the program slice.</summary>
    public ReadOnlySpan<uint> ParameterWords => m_parameters;

    /// <summary>Returns logical work per pixel with checked arithmetic, including default phases and engine work.</summary>
    public ulong PixelWork(uint lights) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lights, MaxLights);
        return checked((((Costs[0] + (lights * ((ulong)((Phases[1].Count == 0) ? DefaultLightWork : Costs[1])))) +
            ((Phases[2].Count == 0) ? DefaultComposeWork : Costs[2])) + EngineWork));
    }
    /// <summary>Checks a relative vector range without addition overflow. The allocation's outer boundary is separate.</summary>
    public static bool ContainsRange(uint sliceLength, uint offset, uint length) => ((offset <= sliceLength) && (length <= (sliceLength - offset)));
    /// <summary>Reads a word only within this program's slice; a refused read yields zero.</summary>
    public uint ReadWord(uint relativeWord) => ((relativeWord < m_words.Length) ? m_words[relativeWord] : 0u);
    /// <summary>Compiles expressions to canonical typed SSA, eliminates dead expressions, folds exact constants,
    /// interns common expressions and prices every live instruction before exposing a program.</summary>
    public static SdfShadeProgram Compile(SdfShadeDefinition definition) => new Compiler(source: definition).Compile();
}
/// <summary>A typed output assignment to one phase.</summary>
public sealed record SdfShadeBinding(SdfShadePhase Phase, string Name, SdfShadeOperand Value);
/// <summary>The maximum authored wrap and normal tilt. Unknown normal/light reach demands every side.</summary>
public readonly record struct SdfShadeDemand(float MaxWrap, float MaxTiltRadians, bool EverySide) {
    /// <summary>Returns the shadow-query cosine threshold including the material's unassigned base wrap.</summary>
    public float Cosine(float baseWrap) => (EverySide ? -1f :
        -MathF.Sin(x: MathF.Min(x: (MathF.PI / 2f), y: (MathF.Asin(x: Math.Clamp(MathF.Max(x: baseWrap, y: MaxWrap), 0f, 1f)) + MaxTiltRadians))));
}
