using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The values a pass block takes from the tables that packed its frame (<see cref="SdfWorldTables.PassValues"/>).</summary>
/// <param name="ScreenCount">One past the highest screen whose source is bound, or zero when none is.</param>
/// <param name="InstanceMaskWordCount">The live program's per-tile instance-mask width.</param>
/// <param name="SampleIndex">The tick clock star twinkle reads, or zero for a sky with no visible twinkle.</param>
/// <param name="MeshDraws">The frame's mesh draws.</param>
/// <param name="DebugMode">The debug view mode; zero renders the final image.</param>
/// <param name="Environment">The environment rows <see cref="SdfFrameBlock.BakeEnvironment"/> baked,
/// <see cref="SdfEnvironment.LaneCount"/> floats.</param>
public readonly record struct SdfPassValues(uint ScreenCount, uint InstanceMaskWordCount, uint SampleIndex, uint MeshDraws, int DebugMode, ReadOnlyMemory<float> Environment);
/// <summary>
/// Writes what one view's passes read of a frame into an <c>sdf.world</c> pass block: the world values, the view's camera,
/// the frame's levers and its environment, each at the offset the generated declarations read it from
/// (<see cref="SdfWorldInterfaces.WorldParameters"/>). It is the one writer of that block; the kernels read each value by
/// name through <c>isa/sdf-world.interface.hlsli</c>, and the environment's rows by the indices <see cref="SdfIsaHlsl"/>
/// generates from <see cref="SdfEnvironment"/>.
/// </summary>
public static class SdfFrameBlock {
    // The cloud offset's wrap period in layer units. The lattice is hashed on integer cell coordinates, so any integer
    // period is seamless; this one keeps a full period inside float's exact-integer range with room for the sub-cell
    // fraction.
    private const double CloudLatticePeriod = 4096d;

    private static readonly ShaderPipelineParameterLayout Layout = SdfWorldInterfaces.WorldParameters;
    private static readonly int AmbientScale = Offset(member: SdfWorldPackage.AmbientScale);
    private static readonly int AspectRatio = Offset(member: SdfWorldPackage.AspectRatio);
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
    private static readonly int Environment = Offset(member: SdfWorldPackage.Environment);
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
    private static readonly int GridWorldPitch = Offset(member: SdfWorldPackage.GridWorldPitch);
    private static readonly int ImageExtent = Offset(member: SdfWorldPackage.ImageExtent);
    private static readonly int InstanceMaskWordCount = Offset(member: SdfWorldPackage.InstanceMaskWordCount);
    private static readonly int MeshDraws = Offset(member: SdfWorldPackage.MeshDraws);
    private static readonly int NearDistance = Offset(member: SdfWorldPackage.NearDistance);
    private static readonly int SampleIndex = Offset(member: SdfWorldPackage.SampleIndex);
    private static readonly int SceneTime = Offset(member: SdfWorldPackage.SceneTime);
    private static readonly int ScreenCount = Offset(member: SdfWorldPackage.ScreenCount);
    private static readonly int ShadowDistanceScale = Offset(member: SdfWorldPackage.ShadowDistanceScale);
    private static readonly int SunScale = Offset(member: SdfWorldPackage.SunScale);
    private static readonly int TanHalfFieldOfView = Offset(member: SdfWorldPackage.TanHalfFieldOfView);
    private static readonly int TileGrid = Offset(member: SdfWorldPackage.TileGrid);
    private static readonly int ViewBase = Offset(member: SdfWorldPackage.ViewBase);
    private static readonly int ViewForward = Offset(member: SdfWorldPackage.ViewForward);
    private static readonly int ViewPosition = Offset(member: SdfWorldPackage.ViewPosition);
    private static readonly int ViewRight = Offset(member: SdfWorldPackage.ViewRight);
    private static readonly int ViewUp = Offset(member: SdfWorldPackage.ViewUp);
    private static readonly int ViewportCount = Offset(member: SdfWorldPackage.ViewportCount);

    /// <summary>The nearest forward distance, in world units, a view's surfaces are rendered from (<see cref="NearOf"/>):
    /// the mesh pass's reversed-Z depth needs a positive near. The kernels read it as <c>SDF_MINIMUM_NEAR</c>, which
    /// <see cref="SdfIsaHlsl"/> generates from this value.</summary>
    public const float MinimumNear = 0.02f;

    /// <summary>Gets the bytes of the pass block, a multiple of 16.</summary>
    public static int SizeBytes => ((int)Layout.SizeBytes);

    /// <summary>Returns the forward distance of the plane a view's surfaces are rendered from: the camera's own
    /// <see cref="CameraSnapshot.Near"/>, or <see cref="MinimumNear"/> when that is nearer. The kernels start every
    /// surface march where its ray crosses this plane and the mesh pass clips there
    /// (<see cref="ViewProjection.Create"/>'s <c>near</c>). The pass block carries the camera's own near distance, which
    /// the bounded volumes start from, and the kernels apply this floor themselves.</summary>
    /// <param name="camera">The view's camera.</param>
    /// <returns>The plane's forward distance, in world units; at least <see cref="MinimumNear"/>.</returns>
    public static float NearOf(in CameraSnapshot camera) =>
        MathF.Max(
            x: camera.Near,
            y: MinimumNear
        );
    /// <summary>Writes a view's values into a pass block: its render extent and tile grid, the frame's bound screens,
    /// instance-mask width, twinkle tick and mesh draws the tables packed, the view's camera, the far distance and the
    /// debug view mode, the frame's levers, and the environment the tables baked. The extent is not written: the node
    /// writes it.</summary>
    /// <param name="block">The pass block, at least <see cref="SizeBytes"/> bytes.</param>
    /// <param name="tables">The values of the tables that packed <paramref name="frame"/>.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <param name="width">The view's render width, in pixels.</param>
    /// <param name="height">The view's render height, in pixels.</param>
    /// <param name="sceneTime">The presentation time the block carries, in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static void Write(Span<byte> block, in SdfPassValues tables, SdfFrame frame, int view, uint width, uint height, float sceneTime) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var snapshot = frame.Views[view];
        var camera = snapshot.Camera;

        WriteUInt32(block: block, offset: ImageExtent, value: width);
        WriteUInt32(block: block, offset: (ImageExtent + sizeof(uint)), value: height);
        WriteUInt32(block: block, offset: TileGrid, value: ((width + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize));
        WriteUInt32(block: block, offset: (TileGrid + sizeof(uint)), value: ((height + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize));
        WriteUInt32(block: block, offset: ViewportCount, value: 1u);
        WriteUInt32(block: block, offset: ViewBase, value: 0u);
        WriteUInt32(block: block, offset: ScreenCount, value: tables.ScreenCount);
        WriteUInt32(block: block, offset: InstanceMaskWordCount, value: tables.InstanceMaskWordCount);
        WriteUInt32(block: block, offset: SampleIndex, value: tables.SampleIndex);
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
        WriteSingle(block: block, offset: SceneTime, value: sceneTime);
        WriteUInt32(block: block, offset: DebugMode, value: ((uint)tables.DebugMode));
        WriteSingle(block: block, offset: AmbientScale, value: frame.AmbientScale);
        WriteSingle(block: block, offset: SunScale, value: frame.SunScale);
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
        WriteFlag(block: block, offset: FiniteDifferenceNormals, value: frame.UseFiniteDifferenceNormals);
        WriteFlag(block: block, offset: DisableShadowCull, value: frame.DisableShadowCull);
        WriteFlag(block: block, offset: DisableSoftShadows, value: frame.DisableSoftShadows);
        WriteFlag(block: block, offset: DisableAmbientOcclusion, value: frame.DisableAmbientOcclusion);
        WriteSingle(block: block, offset: ShadowDistanceScale, value: frame.ShadowDistanceScale);
        WriteFlag(block: block, offset: DisableScreenLights, value: frame.DisableScreenLights);
        WriteFlag(block: block, offset: EnableShadowProxy, value: frame.EnableShadowProxy);
        WriteFlag(block: block, offset: CameraTileShadowMask, value: frame.UseCameraTileShadowMask);
        WriteFlag(block: block, offset: FastSoftShadowMarch, value: frame.UseFastSoftShadowMarch);
        WriteFlag(block: block, offset: FastAmbientOcclusion, value: frame.UseFastAmbientOcclusion);
        WriteFlag(block: block, offset: DisableFarBound, value: frame.DisableFarBound);
        MemoryMarshal.AsBytes(span: tables.Environment.Span).CopyTo(destination: block[Environment..]);
    }
    /// <summary>Bakes a frame's environment into the rows every pass block carries: <see cref="SdfEnvironment.Lanes"/>
    /// copied row for row, with the host bakes the shader must not pay per pixel: every directional (light and softbox)
    /// normalized in double and rounded once (DXC's DXIL backend constant-folds a <c>normalize()</c> while its SPIR-V
    /// backend emits a runtime call; a uniform has no such asymmetry), the sun-disc angular radius baked into the
    /// <c>pow()</c> exponent that puts the disc's edge at half brightness (k = ln 0.5 / ln cos r), the twinkle rate baked into
    /// a period in engine ticks so the shader reduces the tick counter by an integer modulo, and the cloud drift, shear and
    /// spin integrated from the tick counter in double (offsets wrapped modulo the lattice period, the angle modulo 2π). The
    /// kernels read the rows' indices from the generated <c>sdf-isa.hlsli</c>, and <c>frame/sdf-lights.hlsli</c> decodes
    /// each row's lanes as <see cref="SdfEnvironment"/> lays them out.</summary>
    /// <param name="frame">The frame whose environment and tick clock the rows are baked from.</param>
    /// <param name="rows">The rows, <see cref="SdfEnvironment.LaneCount"/> floats.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rows"/> holds other than <see cref="SdfEnvironment.LaneCount"/>
    /// floats.</exception>
    public static void BakeEnvironment(SdfFrame frame, Span<float> rows) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        if (rows.Length != SdfEnvironment.LaneCount) {
            throw new ArgumentException(
                message: $"The environment bakes into {SdfEnvironment.LaneCount} floats; the rows hold {rows.Length}.",
                paramName: nameof(rows)
            );
        }

        var environment = frame.Environment;
        var lanes = environment.Lanes;
        var floats = rows;

        lanes.CopyTo(destination: floats);

        for (var index = 0; (index < SdfEnvironment.MaxLights); index++) {
            var row = ((SdfEnvironment.LightsRow + (index * SdfEnvironment.RowsPerLight)) * 4);
            var kind = ((SdfLightKind)((byte)lanes[(row + 7)]));

            if (kind != SdfLightKind.Directional) {
                continue;
            }

            double x = lanes[(row + 0)], y = lanes[(row + 1)], z = lanes[(row + 2)];
            var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

            if (length <= 0d) {
                // A zero direction has no Lambert term; the authoring doors refuse one by name, and a frame assembled
                // in code still must not upload NaNs into every shaded pixel.
                x = SdfEnvironment.DefaultSunDirection.X; y = SdfEnvironment.DefaultSunDirection.Y; z = SdfEnvironment.DefaultSunDirection.Z;
                length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));
            }

            floats[(row + 0)] = ((float)(x / length)); floats[(row + 1)] = ((float)(y / length)); floats[(row + 2)] = ((float)(z / length));
        }

        var skyControl = (SdfEnvironment.SkyControlRow * 4);
        var cosDiscRadius = Math.Cos(d: environment.SunDiscRadians);
        var discExponent = ((cosDiscRadius is > 0d and < 1d)
            ? Math.Clamp(
                value: (Math.Log(d: 0.5d) / Math.Log(d: cosDiscRadius)),
                min: 0d,
                max: 100000d
            )
            : 100000d
        );

        floats[(skyControl + 2)] = ((float)discExponent);

        var twinkle = (SdfEnvironment.TwinkleRow * 4);
        var twinklePeriodTicks = ((environment.TwinkleRate > 0f)
            ? Math.Max(
                val1: 1d,
                val2: Math.Round(a: (((double)EngineTicks.PerSecond) / environment.TwinkleRate))
            )
            : 1d
        );

        floats[(twinkle + 2)] = ((float)twinklePeriodTicks);

        var elapsedSeconds = (((double)frame.SampleIndex) / EngineTicks.PerSecond);
        var drift = environment.CloudDrift;
        var shear = environment.CloudShear;
        var cloudsC = ((SdfEnvironment.CloudsRow + 2) * 4);
        var cloudsD = ((SdfEnvironment.CloudsRow + 3) * 4);

        floats[(cloudsC + 0)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * drift.X),
            y: CloudLatticePeriod
        ));
        floats[(cloudsC + 1)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * drift.Y),
            y: CloudLatticePeriod
        ));
        floats[(cloudsC + 2)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * shear.X),
            y: CloudLatticePeriod
        ));
        floats[(cloudsC + 3)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * shear.Y),
            y: CloudLatticePeriod
        ));
        floats[(cloudsD + 0)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * environment.CloudSpin),
            y: Math.Tau
        ));

        for (var index = 0; (index < SdfEnvironment.MaxSoftboxes); index++) {
            var row = ((SdfEnvironment.SoftboxesRow + (index * SdfEnvironment.RowsPerSoftbox)) * 4);

            double x = lanes[(row + 0)], y = lanes[(row + 1)], z = lanes[(row + 2)];
            var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

            if (length <= 0d) {
                continue; // an unauthored softbox slot has zero weight and never contributes; leave its direction zero
            }

            floats[(row + 0)] = ((float)(x / length)); floats[(row + 1)] = ((float)(y / length)); floats[(row + 2)] = ((float)(z / length));
        }
    }

    // The offset a pass-block member lies at, which the environment's rows must fill exactly.
    private static int Offset(string member) {
        if (
            string.Equals(
                a: member,
                b: SdfWorldPackage.Environment,
                comparisonType: StringComparison.Ordinal
            ) &&
            (SdfWorldPackage.EnvironmentRows != SdfEnvironment.RowCount)
        ) {
            throw new InvalidOperationException(message: $"The pass block holds {SdfWorldPackage.EnvironmentRows} environment rows; the environment lays out {SdfEnvironment.RowCount}.");
        }

        return ((int)Layout.BlockOffsetOf(member: member));
    }
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
