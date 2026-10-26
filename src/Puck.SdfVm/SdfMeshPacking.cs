using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>Presentation packing shared by every mesh-region writer. Positions use a bounds-relative 20-bit grid;
/// the measured rounding error must fit one 2048th of the contour cell diagonal. Authored meshes retain float positions.</summary>
public sealed class SdfMeshPacking {
    private static readonly ConditionalWeakTable<SdfMesh, SdfMeshPacking> Cache = new();
    /// <summary>The largest coordinate on the quantization grid.</summary>
    public const uint CoordinateMax = (1u << 20) - 1;
    /// <summary>Gets the mesh's immutable packing description.</summary>
    public static SdfMeshPacking Of(SdfMesh mesh) => Cache.GetValue(mesh, static value => new(value));
    // A group's chunks must decode their shared seam vertices identically in every draw.
    internal static IReadOnlyList<SdfMesh> SharePositionGrid(SdfMesh group, IReadOnlyList<SdfMesh> chunks) {
        var grid = new SdfMeshPacking(group, budget: chunks.Min(static chunk => chunk.BakeCell.Length() / 2048f));
        return chunks.Select(chunk => {
            var copy = chunk with { };
            Cache.Add(copy, new SdfMeshPacking(copy, grid));
            return copy;
        }).ToArray();
    }
    private SdfMeshPacking(SdfMesh mesh, SdfMeshPacking? grid = null, float? budget = null) {
        var low = mesh.Positions.Span[0];
        var high = low;
        foreach (var position in mesh.Positions.Span) {
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }
        Low = low;
        Step = (high - low) / CoordinateMax;
        var error = budget ?? mesh.BakeCell.Length() / 2048f;
        Quantized = error > 0 && float.IsFinite(error) && Step.Length() <= error;
        if (Quantized) {
            foreach (var position in mesh.Positions.Span) {
                var decoded = DecodePosition(EncodePosition(position));
                if (Vector3.Distance(position, decoded) > error) { Quantized = false; break; }
            }
        }
        if (grid is not null) { Low = grid.Low; Step = grid.Step; Quantized = grid.Quantized; }
        SmallIndices = mesh.Positions.Length <= 65536;
        PackedNormals = true;
        foreach (var normal in mesh.Normals.Span) {
            if (normal != Vector3.Zero && MathF.Abs(normal.LengthSquared() - 1f) > 0.00001f) { PackedNormals = false; break; }
        }
        ByteMaterials = FitsBytes(mesh.TriangleMaterials.Span);
        foreach (var level in mesh.Levels) { ByteMaterials &= FitsBytes(level.TriangleMaterials.Span); }
    }
    /// <summary>The position grid's origin.</summary>
    public Vector3 Low { get; }
    /// <summary>The position grid's step on each axis.</summary>
    public Vector3 Step { get; }
    /// <summary>Whether the position grid fits the bake's error budget.</summary>
    public bool Quantized { get; }
    /// <summary>Whether every vertex index fits an unsigned 16-bit integer.</summary>
    public bool SmallIndices { get; }
    /// <summary>Whether every triangle material fits a byte.</summary>
    public bool ByteMaterials { get; }
    /// <summary>Whether normals are unit directions (or zero), so octahedral packing preserves their interpolation.</summary>
    public bool PackedNormals { get; }
    /// <summary>The vertex stride: three packed words, plus float fallbacks where an attribute requires them.</summary>
    public int VertexWords => (Quantized ? 2 : 3) + (PackedNormals ? 1 : 3);
    /// <summary>Encodes a position on this mesh's grid.</summary>
    public ulong EncodePosition(Vector3 position) => Quantize(position.X, Low.X, Step.X)
        | ((ulong)Quantize(position.Y, Low.Y, Step.Y) << 20)
        | ((ulong)Quantize(position.Z, Low.Z, Step.Z) << 40);
    /// <summary>Decodes a position exactly as the vertex stages do.</summary>
    public Vector3 DecodePosition(ulong packed) => Low + Step * new Vector3(packed & CoordinateMax,
        (packed >> 20) & CoordinateMax, (packed >> 40) & CoordinateMax);
    /// <summary>Encodes a unit normal as two signed 16-bit octahedral coordinates; zero has its own sentinel.</summary>
    public static uint EncodeNormal(Vector3 normal) {
        if (normal == Vector3.Zero) { return 0x80008000u; }
        var (x, y) = SdfOctahedral.Encode(new Double3(normal.X, normal.Y, normal.Z));
        return (ushort)(short)Math.Round(x * 32767) | ((uint)(ushort)(short)Math.Round(y * 32767) << 16);
    }
    private static uint Quantize(float value, float low, float step) => step == 0 ? 0 :
        (uint)Math.Clamp(Math.Round(((double)value - low) / step), 0, CoordinateMax);
    private static bool FitsBytes(ReadOnlySpan<uint> values) {
        foreach (var value in values) { if (value > byte.MaxValue) { return false; } }
        return true;
    }
}
