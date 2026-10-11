using System.Globalization;

namespace Puck.SignedDistance;

/// <summary>
/// What a program's field code needs: its distinct chains (<see cref="SdfChain"/>) and its vocabulary, the shape types,
/// shape blends and pop compose forms its instructions use, including those its rigid-plan leaves and compiled part
/// programs evaluate, since both read the program's own instructions. Data never enters it, so a program keeps its set
/// through every edit that moves, scales or recolors what it draws; a new shape type, blend, compose form or
/// code-selecting lane (<see cref="SdfOpRoles.CodeLanesOf"/>) changes it.
/// <para>Every list is in ascending order (chains by <see cref="SdfChain.Identity"/>), so two programs with the same
/// chains and vocabulary hold equal sets.</para>
/// </summary>
public sealed class SdfSpecializationSet : IEquatable<SdfSpecializationSet> {
    private readonly SdfChain[] m_chains;
    private readonly SdfBlendOp[] m_blends;
    private readonly SdfBlendOp[] m_composes;
    private readonly SdfShapeType[] m_shapes;

    internal SdfSpecializationSet(SdfChain[] chains, SdfShapeType[] shapes, SdfBlendOp[] blends, SdfBlendOp[] composes) {
        m_chains = chains;
        m_shapes = shapes;
        m_blends = blends;
        m_composes = composes;
    }

    /// <summary>Gets the blends the program's shapes compose with, ascending.</summary>
    public IReadOnlyList<SdfBlendOp> Blends => m_blends;
    /// <summary>Gets the program's distinct chains, ascending by identity.</summary>
    public IReadOnlyList<SdfChain> Chains => m_chains;
    /// <summary>Gets the compose forms the program's field-scope pops use, ascending.</summary>
    public IReadOnlyList<SdfBlendOp> Composes => m_composes;
    /// <summary>Gets the shape types the program's shapes use, ascending.</summary>
    public IReadOnlyList<SdfShapeType> Shapes => m_shapes;

    /// <summary>Describes the set in one line: its chain count, then each vocabulary list.</summary>
    /// <returns>The description, such as <c>7 distinct chain(s), shapes 2 (Box Sphere), blends 1 (Union), composes 0</c>.</returns>
    public string Describe() =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{m_chains.Length} distinct chain(s), shapes {Listed(values: m_shapes)}, blends {Listed(values: m_blends)}, composes {Listed(values: m_composes)}"
        );
    /// <inheritdoc/>
    public bool Equals(SdfSpecializationSet? other) =>
        ((other is not null) &&
        m_chains.AsSpan().SequenceEqual(other: other.m_chains) &&
        m_shapes.AsSpan().SequenceEqual(other: other.m_shapes) &&
        m_blends.AsSpan().SequenceEqual(other: other.m_blends) &&
        m_composes.AsSpan().SequenceEqual(other: other.m_composes));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as SdfSpecializationSet));
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        foreach (var chain in m_chains) {
            hash.Add(value: chain.Identity);
        }

        hash.Add(value: m_shapes.Length);
        hash.Add(value: m_blends.Length);
        hash.Add(value: m_composes.Length);

        return hash.ToHashCode();
    }
    /// <inheritdoc/>
    public override string ToString() => Describe();

    private static string Listed<T>(T[] values) where T : struct, Enum =>
        ((values.Length == 0)
            ? "0"
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{values.Length} ({string.Join(separator: ' ', values: values)})"
            ));
}
