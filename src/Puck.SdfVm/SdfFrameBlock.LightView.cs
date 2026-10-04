using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.Shaders;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    private static readonly int LightMapsOffset = Offset(member: SdfWorldPackage.LightMaps);
    private static readonly int LightMapOffset = Offset(member: SdfWorldPackage.LightMap);
    private static readonly int LightMapCountOffset = Offset(member: SdfWorldPackage.LightMapCount);
    private static readonly int LightSweepRadiusOffset = Offset(member: SdfWorldPackage.LightSweepRadius);

    /// <summary>Writes planned projections and their submitted validity; unavailable regions remain explicit fallback requests.</summary>
    /// <param name="block">The common world pass block.</param>
    /// <param name="views">The residency's map state, or null when indirect lighting is off.</param>
    /// <param name="depthCamera">Whether this pass publishes the one scheduled region.</param>
    /// <param name="geometryOwner">The pinned source's table allocation, or null for a live geometry reader.</param>
    /// <param name="geometry">The pinned source's exact revisions when <paramref name="geometryOwner"/> is supplied.</param>
    public static void WriteLightViews(Span<byte> block, SdfIndirectLightViews? views, bool depthCamera,
        object? geometryOwner = null, SdfLightGeometry geometry = default) {
        // A live depth camera can advance while a finite solve still reads captured poses. Incompatible maps
        // become ordinary map misses, so shade uses its existing bounded ray over the pinned World set.
        if (geometryOwner is not null && views is not null && !views.MatchesGeometry(geometryOwner, geometry)) { views = null; }
        var records = block.Slice(start: LightMapsOffset, length: (SdfIndirectLightLayout.MaxMaps * SdfIndirectLightLayout.MetadataRows * 16));
        records.Clear();
        WriteUInt32(block: block, offset: LightMapCountOffset, value: ((uint)(views?.MapCount ?? 0)));
        WriteUInt32(block: block, offset: LightMapOffset, value: ((depthCamera && (views?.Pending is >= 0)) ? ((uint)(views.Pending + 1)) : 0u));
        WriteSingle(block: block, offset: LightSweepRadiusOffset, value: ((depthCamera && (views?.Projection is { } pending)) ? ((float)pending.SweepRadius) : 0f));
        if (views is null) { return; }
        for (var index = 0; (index < views.MapCount); index++) {
            var map = views.Snapshot(index: index);
            if (map.Projection is not { } projection) { continue; }
            var record = records.Slice(start: (index * SdfIndirectLightLayout.MetadataRows * 16), length: (SdfIndirectLightLayout.MetadataRows * 16));
            var values = MemoryMarshal.Cast<byte, float>(span: record);
            Point(values[0..], projection.Origin); values[3] = (map.Valid ? 1f : 0f);
            Point(values[4..], projection.Right); values[7] = ((float)projection.HalfWidth);
            Point(values[8..], projection.Up); values[11] = ((float)projection.Near);
            Point(values[12..], projection.TowardLight); values[15] = ((float)projection.Far);
            Point(values[16..], map.Receiver.Min); values[19] = ((float)projection.SweepRadius);
            Point(values[20..], map.Receiver.Max); values[23] = (map.LightIndex + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(destination: record[96..], value: map.LightGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(destination: record[104..], value: map.GeometryGeneration);
        }
    }
    private static void Point(Span<float> values, Double3 point) { values[0] = ((float)point.X); values[1] = ((float)point.Y); values[2] = ((float)point.Z); }
}
