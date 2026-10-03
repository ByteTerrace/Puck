using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The values a pass block takes from the tables that packed its frame (<see cref="SdfWorldTables.PassValues"/>).</summary>
/// <param name="ScreenCount">One past the highest screen whose source is bound, or zero when none is.</param>
/// <param name="InstanceMaskWordCount">The live program's per-tile instance-mask width.</param>
/// <param name="MeshDraws">The frame's mesh draws.</param>
/// <param name="DebugMode">The debug view mode; zero renders the final image.</param>
public readonly record struct SdfPassValues(uint ScreenCount, uint InstanceMaskWordCount, uint MeshDraws, int DebugMode);
/// <summary>
/// Writes what one view's passes read of a frame into an <c>sdf.world</c> pass block: the world values, the view's camera,
/// the frame's levers, its light count and shadow slots, and its curvature shading, each at the offset the generated
/// declarations read it from (<see cref="SdfWorldInterfaces.WorldParameters"/>). It is the one writer of that block; the
/// kernels read each value by name through <c>isa/sdf-world.interface.hlsli</c>. The lights and the sky are no part of
/// it: the tables write them into their own regions (<see cref="SdfLights.Pack"/>, <see cref="SdfSky.Pack"/>).
/// </summary>
public static class SdfFrameBlock {
    private static readonly ShaderPipelineParameterLayout Layout = SdfWorldInterfaces.WorldParameters;
    private static readonly int AspectRatio = Offset(member: SdfWorldPackage.AspectRatio);
    private static readonly int CurvatureCavity = Offset(member: SdfWorldPackage.CurvatureCavity);
    private static readonly int CurvatureInk = Offset(member: SdfWorldPackage.CurvatureInk);
    private static readonly int CurvatureInkColor = Offset(member: SdfWorldPackage.CurvatureInkColor);
    private static readonly int CurvatureInkHigh = Offset(member: SdfWorldPackage.CurvatureInkHigh);
    private static readonly int CurvatureInkLow = Offset(member: SdfWorldPackage.CurvatureInkLow);
    private static readonly int CurvatureRim = Offset(member: SdfWorldPackage.CurvatureRim);
    private static readonly int CameraTileShadowMask = Offset(member: SdfWorldPackage.CameraTileShadowMask);
    private static readonly int DebugMode = Offset(member: SdfWorldPackage.DebugMode);
    private static readonly int DebugSliceAxis = Offset(member: SdfWorldPackage.DebugSliceAxis);
    private static readonly int DebugSliceOffset = Offset(member: SdfWorldPackage.DebugSliceOffset);
    private static readonly int DisableAmbientOcclusion = Offset(member: SdfWorldPackage.DisableAmbientOcclusion);
    private static readonly int DisableFarBound = Offset(member: SdfWorldPackage.DisableFarBound);
    private static readonly int DisableScreenLights = Offset(member: SdfWorldPackage.DisableScreenLights);
    private static readonly int DisableShadowCull = Offset(member: SdfWorldPackage.DisableShadowCull);
    private static readonly int DisableSoftShadows = Offset(member: SdfWorldPackage.DisableSoftShadows);
    private static readonly int EnableShadowProxy = Offset(member: SdfWorldPackage.EnableShadowProxy);
    private static readonly int FarDistance = Offset(member: SdfWorldPackage.FarDistance);
    private static readonly int FastAmbientOcclusion = Offset(member: SdfWorldPackage.FastAmbientOcclusion);
    private static readonly int FastSoftShadowMarch = Offset(member: SdfWorldPackage.FastSoftShadowMarch);
    private static readonly int FiniteDifferenceNormals = Offset(member: SdfWorldPackage.FiniteDifferenceNormals);
    private static readonly int FrustumOffset = Offset(member: SdfWorldPackage.FrustumOffset);
    private static readonly int GridFlags = Offset(member: SdfWorldPackage.GridFlags);
    private static readonly int GridLineWidth = Offset(member: SdfWorldPackage.GridLineWidth);
    private static readonly int GridObjectFrame = Offset(member: SdfWorldPackage.GridObjectFrame);
    private static readonly int GridObjectOrigin = Offset(member: SdfWorldPackage.GridObjectOrigin);
    private static readonly int GridObjectPatchRadius = Offset(member: SdfWorldPackage.GridObjectPatchRadius);
    private static readonly int GridObjectPitch = Offset(member: SdfWorldPackage.GridObjectPitch);
    private static readonly int GridPlaneY = Offset(member: SdfWorldPackage.GridPlaneY);
    private static readonly int GridWorldFrame = Offset(member: SdfWorldPackage.GridWorldFrame);
    private static readonly int GridWorldOrigin = Offset(member: SdfWorldPackage.GridWorldOrigin);
    private static readonly int GridWorldPitch = Offset(member: SdfWorldPackage.GridWorldPitch);
    private static readonly int PreviousView = Offset(member: SdfWorldPackage.PreviousView);
    private static readonly int Jitter = Offset(member: SdfWorldPackage.Jitter);
    private static readonly int HistoryFrames = Offset(member: SdfWorldPackage.HistoryFrames);
    private static readonly int Temporal = Offset(member: SdfWorldPackage.Temporal);
    private static readonly int ShadowAmortize = Offset(member: SdfWorldPackage.ShadowAmortize);
    private static readonly int ShadowOwnershipReject = Offset(member: SdfWorldPackage.ShadowOwnershipReject);
    private static readonly int ShadowLightReject = Offset(member: SdfWorldPackage.ShadowLightReject);
    private static readonly int ImageExtent = Offset(member: SdfWorldPackage.ImageExtent);
    private static readonly int LightCount = Offset(member: SdfWorldPackage.LightCount);
    private static readonly int InstanceMaskWordCount = Offset(member: SdfWorldPackage.InstanceMaskWordCount);
    private static readonly int MeshDraws = Offset(member: SdfWorldPackage.MeshDraws);
    private static readonly int NearDistance = Offset(member: SdfWorldPackage.NearDistance);
    private static readonly int ScreenCount = Offset(member: SdfWorldPackage.ScreenCount);
    private static readonly int ShadowDistanceScale = Offset(member: SdfWorldPackage.ShadowDistanceScale);
    private static readonly int ShadowSlots = Offset(member: SdfWorldPackage.ShadowSlots);
    private static readonly int ShadowSlotCount = Offset(member: SdfWorldPackage.ShadowSlotCount);
    private static readonly int ShadowFadeCount = Offset(member: SdfWorldPackage.ShadowFadeCount);
    private static readonly int TanHalfFieldOfView = Offset(member: SdfWorldPackage.TanHalfFieldOfView);
    private static readonly int TileGrid = Offset(member: SdfWorldPackage.TileGrid);
    private static readonly int ViewBase = Offset(member: SdfWorldPackage.ViewBase);
    private static readonly int ViewForward = Offset(member: SdfWorldPackage.ViewForward);
    private static readonly int ViewPosition = Offset(member: SdfWorldPackage.ViewPosition);
    private static readonly int ViewRight = Offset(member: SdfWorldPackage.ViewRight);
    private static readonly int ViewUp = Offset(member: SdfWorldPackage.ViewUp);
    private static readonly int ViewportCount = Offset(member: SdfWorldPackage.ViewportCount);
    private static readonly int WorkCounterRow = Offset(member: ShaderWorkCounters.Row);
    private static readonly int WorkCounterDetailRow = Offset(member: ShaderWorkCounters.DetailRow);

    /// <summary>Gets the bytes of the pass block, a multiple of 16.</summary>
    public static int SizeBytes => ((int)Layout.SizeBytes);

    /// <summary>Writes the instance's temporal sample after the frame values, keeping cadence signatures independent of jitter.</summary>
    /// <param name="block">The pass block.</param>
    /// <param name="jitter">The ray offset in render pixels.</param>
    /// <param name="historyFrames">The number of preceding samples in the current epoch.</param>
    /// <param name="temporal">Whether the view runs the temporal fragment (<see cref="SdfWorldPackage.TemporalFragment"/>).</param>
    public static void WriteTemporal(Span<byte> block, Vector2 jitter, uint historyFrames, bool temporal) {
        WriteSingle(block: block, offset: Jitter, value: jitter.X);
        WriteSingle(block: block, offset: (Jitter + sizeof(float)), value: jitter.Y);
        WriteUInt32(block: block, offset: HistoryFrames, value: historyFrames);
        WriteUInt32(block: block, offset: Temporal, value: (temporal ? 1u : 0u));
    }
    /// <summary>Writes the shadow history policy and the two frame-uniform rejection masks.</summary>
    /// <param name="block">The common pass block.</param>
    /// <param name="enabled">Whether temporal secondary shadows are enabled.</param>
    /// <param name="ownership">Slots rejected by ownership.</param>
    /// <param name="lightMotion">Slots rejected by light motion.</param>
    public static void WriteShadowHistory(Span<byte> block, bool enabled, uint ownership, uint lightMotion) {
        WriteFlag(block: block, offset: ShadowAmortize, value: enabled);
        WriteUInt32(block: block, offset: ShadowOwnershipReject, value: ownership);
        WriteUInt32(block: block, offset: ShadowLightReject, value: lightMotion);
    }
    /// <summary>Writes the preceding render's camera, lens and jitter for visibility reprojection: position and
    /// validity, right and tangent, up and aspect, forward, the render extent and the jitter in render pixels, then the
    /// near distance and the unjittered frustum offset (<c>frame/sdf-reprojection.hlsli</c>).</summary>
    /// <param name="block">The pass block.</param>
    /// <param name="view">The preceding view and sample grid.</param>
    /// <param name="valid">Whether the preceding render belongs to this epoch.</param>
    public static void WritePreviousView(Span<byte> block, SdfReprojectionView view, bool valid) {
        var rows = MemoryMarshal.Cast<byte, float>(span: block.Slice(length: (24 * sizeof(float)), start: PreviousView));

        rows.Clear();
        if (!valid) {
            return;
        }
        var camera = view.Camera;

        rows[0] = camera.Position.X; rows[1] = camera.Position.Y; rows[2] = camera.Position.Z; rows[3] = 1f;
        rows[4] = camera.Right.X; rows[5] = camera.Right.Y; rows[6] = camera.Right.Z; rows[7] = camera.TanHalfFieldOfView;
        rows[8] = camera.Up.X; rows[9] = camera.Up.Y; rows[10] = camera.Up.Z; rows[11] = camera.AspectRatio;
        rows[12] = camera.Forward.X; rows[13] = camera.Forward.Y; rows[14] = camera.Forward.Z;
        rows[16] = view.Width; rows[17] = view.Height; rows[18] = view.Jitter.X; rows[19] = view.Jitter.Y;
        rows[20] = camera.Near;
        rows[21] = camera.FrustumOffset.X;
        rows[22] = camera.FrustumOffset.Y;
    }
    /// <summary>Writes the pass's row of the work counters into its pass block (<see cref="ShaderWorkCounters.Row"/>),
    /// which <see cref="Write"/> leaves alone: the row names the pass, never what the view renders from, so a view's
    /// signature (<see cref="SdfWorldTables.ViewSignature"/>) never reads it.</summary>
    /// <param name="block">The pass block, at least <see cref="SizeBytes"/> bytes.</param>
    /// <param name="row">The pass's row.</param>
    public static void WriteWorkCounterRow(Span<byte> block, uint row) =>
        WriteUInt32(block: block, offset: WorkCounterRow, value: row);
    /// <summary>Writes the first named detail row. Zero disables named detail counting.</summary>
    /// <param name="block">The pass block.</param>
    /// <param name="row">The first named detail row.</param>
    public static void WriteWorkCounterDetailRow(Span<byte> block, uint row) =>
        WriteUInt32(block: block, offset: WorkCounterDetailRow, value: row);
    /// <summary>Returns the forward distance of the plane a view's surfaces are rendered from: the camera's own
    /// <see cref="CameraSnapshot.Near"/>, or <see cref="SdfWorldPackage.MinimumNear"/> when that is nearer. The kernels start every
    /// surface march where its ray crosses this plane and the mesh pass clips there
    /// (<see cref="ViewProjection.Create"/>'s <c>near</c>). The pass block carries the camera's own near distance, which
    /// the bounded volumes start from, and the kernels apply this floor themselves.</summary>
    /// <param name="camera">The view's camera.</param>
    /// <returns>The plane's forward distance, in world units; at least <see cref="SdfWorldPackage.MinimumNear"/>.</returns>
    public static float NearOf(in CameraSnapshot camera) =>
        MathF.Max(
            x: camera.Near,
            y: SdfWorldPackage.MinimumNear
        );
    /// <summary>Writes a view's values into a pass block: its render extent and tile grid, the frame's bound screens,
    /// instance-mask width and mesh draws the tables packed, the view's camera and quality
    /// (<see cref="SdfViewSnapshot.Quality"/>), the far distance and the debug view mode, the frame's bench levers, its
    /// light count and shadow slots (<see cref="SdfLights"/>), and its curvature shading. The extent is not written: the
    /// node writes it.</summary>
    /// <param name="block">The pass block, at least <see cref="SizeBytes"/> bytes.</param>
    /// <param name="tables">The values of the tables that packed <paramref name="frame"/>.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <param name="width">The view's render width, in pixels.</param>
    /// <param name="height">The view's render height, in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static void Write(Span<byte> block, in SdfPassValues tables, SdfFrame frame, int view, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        WriteTemporal(block: block, jitter: Vector2.Zero, historyFrames: 0, temporal: false);
        WriteShadowHistory(block: block, enabled: false, lightMotion: 0, ownership: 0);
        WritePreviousView(block: block, valid: false, view: default);

        var snapshot = frame.Views[view];
        var camera = snapshot.Camera;
        var quality = snapshot.Quality;

        WriteUInt32(block: block, offset: ImageExtent, value: width);
        WriteUInt32(block: block, offset: (ImageExtent + sizeof(uint)), value: height);
        WriteUInt32(block: block, offset: TileGrid, value: ((width + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize));
        WriteUInt32(block: block, offset: (TileGrid + sizeof(uint)), value: ((height + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize));
        WriteUInt32(block: block, offset: ViewportCount, value: 1u);
        WriteUInt32(block: block, offset: ViewBase, value: 0u);
        WriteUInt32(block: block, offset: ScreenCount, value: tables.ScreenCount);
        WriteUInt32(block: block, offset: InstanceMaskWordCount, value: tables.InstanceMaskWordCount);
        WriteUInt32(block: block, offset: MeshDraws, value: tables.MeshDraws);
        WriteVector3(block: block, offset: ViewPosition, value: camera.Position);
        WriteVector3(block: block, offset: ViewRight, value: camera.Right);
        WriteVector3(block: block, offset: ViewUp, value: camera.Up);
        WriteVector3(block: block, offset: ViewForward, value: camera.Forward);
        WriteSingle(block: block, offset: TanHalfFieldOfView, value: camera.TanHalfFieldOfView);
        WriteSingle(block: block, offset: AspectRatio, value: camera.AspectRatio);
        WriteSingle(block: block, offset: FrustumOffset, value: camera.FrustumOffset.X);
        WriteSingle(block: block, offset: (FrustumOffset + sizeof(float)), value: camera.FrustumOffset.Y);
        WriteSingle(block: block, offset: NearDistance, value: camera.Near);
        WriteSingle(block: block, offset: FarDistance, value: frame.FarDistance);
        WriteUInt32(block: block, offset: DebugMode, value: ((uint)tables.DebugMode));
        var lights = frame.Lights;
        var curvature = lights.Curvature;

        WriteUInt32(block: block, offset: LightCount, value: ((uint)lights.Count));
        for (var slot = 0; (slot < SdfShadowSlots.MaxSlots); slot++) {
            WriteUInt32(block: block, offset: (ShadowSlots + (slot * sizeof(int))), value: unchecked((uint)lights.ShadowSlots[slot]));
        }
        WriteUInt32(block: block, offset: ShadowSlotCount, value: ((uint)lights.ShadowSlots.SlotCount));
        WriteUInt32(block: block, offset: ShadowFadeCount, value: ((uint)lights.ShadowSlots.FadeCount));
        WriteSingle(block: block, offset: CurvatureCavity, value: curvature.Cavity);
        WriteSingle(block: block, offset: CurvatureRim, value: curvature.Rim);
        WriteSingle(block: block, offset: CurvatureInk, value: curvature.Ink);
        WriteSingle(block: block, offset: CurvatureInkLow, value: curvature.InkLow);
        WriteSingle(block: block, offset: CurvatureInkHigh, value: curvature.InkHigh);
        WriteVector3(block: block, offset: CurvatureInkColor, value: curvature.InkColor);
        WriteSingle(block: block, offset: DebugSliceAxis, value: frame.DebugSliceAxis);
        WriteSingle(block: block, offset: DebugSliceOffset, value: frame.DebugSliceOffset);
        var grid = snapshot.Grid;

        WriteUInt32(block: block, offset: GridFlags, value: ((uint)grid.Flags));
        WriteSingle(block: block, offset: GridPlaneY, value: grid.PlaneY);
        WriteSingle(block: block, offset: GridLineWidth, value: grid.LineWidth);
        WriteVector3(block: block, offset: GridWorldPitch, value: grid.WorldPitch);
        WriteVector3(block: block, offset: GridObjectOrigin, value: grid.ObjectOrigin);
        WriteVector3(block: block, offset: GridObjectPitch, value: grid.ObjectPitch);
        WriteSingle(block: block, offset: GridObjectFrame, value: grid.ObjectFrame.X);
        WriteSingle(block: block, offset: (GridObjectFrame + sizeof(float)), value: grid.ObjectFrame.Y);
        WriteSingle(block: block, offset: (GridObjectFrame + (2 * sizeof(float))), value: grid.ObjectFrame.Z);
        WriteSingle(block: block, offset: (GridObjectFrame + (3 * sizeof(float))), value: grid.ObjectFrame.W);
        WriteSingle(block: block, offset: GridObjectPatchRadius, value: grid.ObjectPatchRadius);
        WriteVector3(block: block, offset: GridWorldOrigin, value: grid.WorldOrigin);
        WriteSingle(block: block, offset: GridWorldFrame, value: grid.WorldFrame.X);
        WriteSingle(block: block, offset: (GridWorldFrame + sizeof(float)), value: grid.WorldFrame.Y);
        WriteSingle(block: block, offset: (GridWorldFrame + (2 * sizeof(float))), value: grid.WorldFrame.Z);
        WriteSingle(block: block, offset: (GridWorldFrame + (3 * sizeof(float))), value: grid.WorldFrame.W);
        WriteFlag(block: block, offset: FiniteDifferenceNormals, value: frame.UseFiniteDifferenceNormals);
        WriteFlag(block: block, offset: DisableShadowCull, value: frame.DisableShadowCull);
        WriteFlag(block: block, offset: DisableSoftShadows, value: quality.DisableSoftShadows);
        WriteFlag(block: block, offset: ShadowAmortize, value: quality.ShadowAmortize);
        WriteFlag(block: block, offset: DisableAmbientOcclusion, value: quality.DisableAmbientOcclusion);
        WriteSingle(block: block, offset: ShadowDistanceScale, value: quality.ShadowDistanceScale);
        WriteFlag(block: block, offset: DisableScreenLights, value: frame.DisableScreenLights);
        WriteFlag(block: block, offset: EnableShadowProxy, value: frame.EnableShadowProxy);
        WriteFlag(block: block, offset: CameraTileShadowMask, value: quality.UseCameraTileShadowMask);
        WriteFlag(block: block, offset: FastSoftShadowMarch, value: quality.UseFastSoftShadowMarch);
        WriteFlag(block: block, offset: FastAmbientOcclusion, value: quality.UseFastAmbientOcclusion);
        WriteFlag(block: block, offset: DisableFarBound, value: quality.DisableFarBound);
    }

    // The offset a pass-block member lies at.
    private static int Offset(string member) =>
        ((int)Layout.BlockOffsetOf(member: member));
    private static void WriteFlag(Span<byte> block, int offset, bool value) =>
        WriteUInt32(
            block: block,
            offset: offset,
            value: (value ? 1u : 0u)
        );
    private static void WriteSingle(Span<byte> block, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: block[offset..],
            value: value
        );
    private static void WriteUInt32(Span<byte> block, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block[offset..],
            value: value
        );
    private static void WriteVector3(Span<byte> block, int offset, Vector3 value) {
        WriteSingle(block: block, offset: offset, value: value.X);
        WriteSingle(block: block, offset: (offset + sizeof(float)), value: value.Y);
        WriteSingle(block: block, offset: (offset + (2 * sizeof(float))), value: value.Z);
    }
}
