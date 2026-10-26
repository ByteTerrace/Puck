using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Compact mesh words preserve topology and materials exactly, and keep decoded geometry within its bake budget.</summary>
public sealed class SdfMeshPackingLawTests {
    [Fact]
    public void AdjacentChunksDecodeTheirSharedVertexIdentically() {
        var seam = new Vector3(1.234567f, 2.345678f, 3.456789f);
        var first = new SdfMesh(new Vector3[] { Vector3.Zero, seam, new(8, 7, 6) }, new uint[] { 0, 1, 2 }) { BakeCell = new(0.2f) };
        var second = new SdfMesh(new Vector3[] { new(-2, -3, -4), seam, new(12, 10, 9) }, new uint[] { 0, 1, 2 }) { BakeCell = new(0.2f) };
        var chunks = SdfMeshChunks.Join([first, second]).Chunks;
        var a = SdfMeshPacking.Of(chunks[0]);
        var b = SdfMeshPacking.Of(chunks[1]);
        Assert.True(a.Quantized && b.Quantized);
        Assert.Equal(a.DecodePosition(a.EncodePosition(seam)), b.DecodePosition(b.EncodePosition(seam)));
        Assert.Equal(a.Low, b.Low);
        Assert.Equal(a.Step, b.Step);
    }

    [Fact]
    public void ShaderDecodedPositionsStayWithinTheBakeBudget() {
        Vector3[] positions = [new(-13.75f, 2.125f, -4f), new(27.33f, 9.2f, -4f), new(0.12345f, 3.45678f, -4f)];
        var mesh = new SdfMesh(positions, new uint[] { 0, 1, 2 }) { BakeCell = new Vector3(0.25f) };
        var packing = SdfMeshPacking.Of(mesh);
        Assert.True(packing.Quantized);
        Assert.Same(packing, SdfMeshPacking.Of(mesh));
        var words = Region(mesh);
        Assert.NotEqual(0u, words[SdfMeshRecord.FlagsWord] & SdfMeshRecord.QuantizedFlag);
        for (var vertex = 0; vertex < positions.Length; vertex++) {
            var start = (int)words[SdfMeshRecord.VertexWord] + vertex * 3;
            var a = words[start];
            var b = words[start + 1];
            var grid = new Vector3(a & 0xfffffu, ((a >> 20) | (b << 12)) & 0xfffffu, (b >> 8) & 0xfffffu);
            var decoded = ReadVector(words, SdfMeshRecord.PositionLowWord) + grid * ReadVector(words, SdfMeshRecord.PositionStepWord);
            Assert.InRange(Vector3.Distance(positions[vertex], decoded), 0f, mesh.BakeCell.Length() / 2048f);
            Assert.Equal(-4f, decoded.Z);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.00000001f)]
    public void AGridWithoutEnoughBudgetRetainsFloatPositions(float cell) {
        Vector3[] positions = [new(-13.75f, 2.125f, -4f), new(27.33f, 9.2f, -4f), new(0.12345f, 3.45678f, -4f)];
        var mesh = new SdfMesh(positions, new uint[] { 0, 1, 2 }) { BakeCell = new Vector3(cell) };
        Assert.False(SdfMeshPacking.Of(mesh).Quantized);
        var words = Region(mesh);
        Assert.Equal(0u, words[SdfMeshRecord.FlagsWord] & SdfMeshRecord.QuantizedFlag);
        for (var vertex = 0; vertex < positions.Length; vertex++) {
            Assert.Equal(positions[vertex], ReadVector(words, (int)words[SdfMeshRecord.VertexWord] + vertex * 4));
        }
    }

    [Fact]
    public void OctahedralNormalsPreserveBothHemispheresAndTheAbsentNormal() {
        Assert.Equal(0x80008000u, SdfMeshPacking.EncodeNormal(Vector3.Zero));
        for (var x = -4; x <= 4; x++) {
            for (var y = -4; y <= 4; y++) {
                for (var z = -4; z <= 4; z++) {
                    if (x == 0 && y == 0 && z == 0) { continue; }
                    var normal = Vector3.Normalize(new Vector3(x, y, z));
                    var packed = SdfMeshPacking.EncodeNormal(normal);
                    Assert.NotEqual(0x80008000u, packed);
                    var oct = new Vector2((short)(packed & 65535u), (short)(packed >> 16)) / 32767f;
                    var decoded = new Vector3(oct, 1f - MathF.Abs(oct.X) - MathF.Abs(oct.Y));
                    var fold = MathF.Max(-decoded.Z, 0f);
                    decoded.X += decoded.X >= 0f ? -fold : fold;
                    decoded.Y += decoded.Y >= 0f ? -fold : fold;
                    decoded = Vector3.Normalize(decoded);
                    Assert.InRange(Vector3.Distance(normal, decoded), 0f, 0.00013f);
                }
            }
        }
    }

    [Theory]
    [InlineData(0f, 6)]
    [InlineData(0.25f, 5)]
    public void NonUnitNormalsRetainTheirMagnitudeForInterpolation(float cell, int stride) {
        Vector3[] normals = [new(0.25f, -2f, 3f), Vector3.UnitY, Vector3.Zero];
        var mesh = new SdfMesh(new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, new uint[] { 0, 1, 2 }, normals) {
            BakeCell = new Vector3(cell),
        };
        var packing = SdfMeshPacking.Of(mesh);
        Assert.False(packing.PackedNormals);
        Assert.Equal(stride, packing.VertexWords);
        var words = Region(mesh);
        Assert.NotEqual(0u, words[SdfMeshRecord.FlagsWord] & SdfMeshRecord.WideNormalsFlag);
        for (var vertex = 0; vertex < normals.Length; vertex++) {
            var decoded = ReadVector(words, (int)words[SdfMeshRecord.VertexWord] + vertex * stride + stride - 3);
            Assert.Equal(normals[vertex], decoded);
        }
    }

    [Theory]
    [InlineData(65536, 255u, true, true)]
    [InlineData(65537, 256u, false, false)]
    public void LevelsSharePackedWordsAndFallbacksPreserveEveryIndexAndMaterial(int count, uint lastMaterial, bool small, bool bytes) {
        var mesh = new SdfMesh(new Vector3[count], new uint[] { 0, 1, (uint)count - 1 }, triangleMaterials: new uint[] { 17 }) {
            Levels = [
                new SdfMeshLevel(new uint[] { (uint)count - 1, 0, 1 }, new uint[] { 43 }, 0f),
                new SdfMeshLevel(new uint[] { 1, (uint)count - 1, 0 }, new uint[] { lastMaterial }, 0f),
            ],
        };
        var packing = SdfMeshPacking.Of(mesh);
        Assert.Equal(small, packing.SmallIndices);
        Assert.Equal(bytes, packing.ByteMaterials);
        var words = Region(mesh);
        Assert.Equal(small, (words[SdfMeshRecord.FlagsWord] & SdfMeshRecord.SmallIndicesFlag) != 0);
        Assert.Equal(bytes, (words[SdfMeshRecord.FlagsWord] & SdfMeshRecord.ByteMaterialsFlag) != 0);
        uint[] expectedIndices = [0, 1, (uint)count - 1, (uint)count - 1, 0, 1, 1, (uint)count - 1, 0];
        uint[] expectedMaterials = [17, 43, lastMaterial];
        for (var index = 0; index < expectedIndices.Length; index++) {
            Assert.Equal(expectedIndices[index], ReadPacked(words, (int)words[SdfMeshRecord.IndexWord], index, small ? 16 : 32));
        }
        for (var triangle = 0; triangle < expectedMaterials.Length; triangle++) {
            Assert.Equal(expectedMaterials[triangle], ReadPacked(words, (int)words[SdfMeshRecord.TriangleMaterialWord], triangle, bytes ? 8 : 32));
        }
        Assert.Equal(1u, words[SdfMeshRecord.LevelTriangleWord]);
        Assert.Equal(2u, words[SdfMeshRecord.LevelTriangleWord + 1]);
    }

    private static uint ReadPacked(uint[] words, int start, int index, int bits) {
        var perWord = 32 / bits;
        return (words[start + index / perWord] >> ((index % perWord) * bits)) & (bits == 32 ? uint.MaxValue : (1u << bits) - 1);
    }

    private static Vector3 ReadVector(uint[] words, int start) => new(
        BitConverter.UInt32BitsToSingle(words[start]), BitConverter.UInt32BitsToSingle(words[start + 1]), BitConverter.UInt32BitsToSingle(words[start + 2]));

    private static uint[] Region(SdfMesh mesh) {
        SdfMeshDraw[] draws = [new(mesh, Matrix4x4.Identity, 0, mesh)];
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(draws, placements);
        var words = Enumerable.Repeat(uint.MaxValue, layout.Words).ToArray();
        SdfMeshRegion.Write(draws, placements, layout, words);
        return words;
    }
}
