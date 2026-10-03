using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The lights and the sky block's fog, softboxes and horizon left the pass block for typed records without moving a value
/// the kernels read: one set of lights and one sky are packed both into the 53-row environment table every pass block
/// carried (its rows reproduced here as the table and its decoders laid them out) and into the records and pass-block
/// values the kernels read now, and every value each layout decodes to is the same bits. The new side decodes through the generated structures
/// (<see cref="SdfKernelInterfaces.LightAndSkyTables"/>) by field name, so a structure whose members disagree with the
/// host's packing (two members swapped) fails.
/// </summary>
public sealed class SdfLightAndSkyLayoutLawTests {
    // The environment table's rows, as it laid them out.
    private const int ControlRow = 0;
    private const int CurvatureRow = 25;
    private const int HorizonHighRow = 52;
    private const int HorizonLowRow = 51;
    private const int LightsRow = 1;
    private const int RowCount = 53;
    private const int RowsPerLight = 3;
    private const int RowsPerSoftbox = 3;
    private const int SoftboxControlRow = 38;
    private const int SoftboxesRow = 39;

    // The pinned sun the kernels fall back to (SdfSunDirection), as the decoders read it.
    private static readonly double[] PinnedSun = [0.51343602f, 0.79349202f, 0.32673201f];

    // One set of lights with a distinct value in every field a kernel reads: five lights of every kind, the second the
    // shadow light, a positional light riding a slot, and curvature shading.
    private static SdfLights Lights() {
        var lights = new SdfLights { Count = 5 };

        lights.Set(index: 0, light: new SdfLight(Color: new(x: 0.9f, y: 0.8f, z: 0.7f), Direction: new(x: 1f, y: 2f, z: 3f), Kind: SdfLightKind.Directional, Param: 0.11f, Shadows: false, Weight: 0.85f));
        lights.Set(index: 1, light: new SdfLight(Color: new(x: 0.6f, y: 0.5f, z: 0.4f), Direction: new(x: -2f, y: 5f, z: 1f), Kind: SdfLightKind.Directional, Param: 0.13f, Shadows: true, Weight: 0.75f));
        lights.Set(index: 2, light: new SdfLight(Color: new(x: 0.3f, y: 0.35f, z: 0.45f), Direction: Vector3.Zero, Kind: SdfLightKind.Hemisphere, Param: 0.25f, Shadows: false, Weight: 0.2f));
        lights.Set(index: 3, light: new SdfLight(Color: new(x: 0.1f, y: 0.2f, z: 0.3f), Direction: new(x: 4f, y: -1f, z: 6f), DynamicSlot: 9, Kind: SdfLightKind.Point, Param: 1.5f, Shadows: false, Weight: 1.25f));
        lights.Set(index: 4, light: new SdfLight(Color: new(x: 0.55f, y: 0.65f, z: 0.75f), Direction: Vector3.Zero, Kind: SdfLightKind.Rim, Param: 3f, Shadows: false, Weight: 0.4f));
        lights.Curvature = new SdfCurvature(Cavity: 0.3f, Ink: 0.6f, InkColor: new(x: 0.02f, y: 0.03f, z: 0.04f), InkHigh: 14f, InkLow: 5f, Rim: 0.45f);
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 1);
        lights.ShadowSlots.SetSlot(light: 1, slot: 0);

        return lights;
    }
    // One sky with a distinct value in every field of its block the views read: two softboxes, a horizon and the fog. The
    // sky's layers are the layer table's, held by SkyLayerTableLawTests.
    private static SdfSky Sky() {
        var sky = new SdfSky { SoftboxCount = 2 };

        sky.SetSoftbox(index: 0, softbox: new SdfSoftbox(Blur: 0.05f, Color: new(x: 1f, y: 0.9f, z: 0.8f), Direction: new(x: 0f, y: 2f, z: 0f), Size: new(x: 0.3f, y: 0.4f), Weight: 0.7f));
        sky.SetSoftbox(index: 1, softbox: new SdfSoftbox(Blur: 0.15f, Color: new(x: 0.5f, y: 0.6f, z: 0.7f), Direction: new(x: 3f, y: 0f, z: 4f), Size: new(x: 0.2f, y: 0.5f), Weight: 0.35f));

        ref var block = ref sky.Block;

        sky.Atmosphere.FogDensity = 0.004f;
        block.HorizonLow = new Vector3(x: 0.01f, y: 0.02f, z: 0.03f);
        block.HorizonHigh = new Vector3(x: 0.11f, y: 0.12f, z: 0.13f);

        return sky;
    }
    private static SdfFrame Frame(SdfLights lights, SdfSky sky) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Time: 0f,
            Views: [new SdfViewSnapshot(
                Camera: new CameraSnapshot(AspectRatio: 1f, Forward: Vector3.UnitZ, Position: Vector3.Zero, Right: Vector3.UnitX, TanHalfFieldOfView: 0.5f, Up: Vector3.UnitY),
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
            )]
        ) {
            Lights = lights,
            Sky = sky,
        };
    }
    // The environment table's rows, packed as its setters and the host bake wrote them.
    private static float[] OldRows(SdfLights lights, SdfSky sky) {
        var rows = new float[(RowCount * 4)];
        var block = sky.Block;

        void Row(int row, float x, float y, float z, float w) {
            rows[(row * 4)] = x; rows[((row * 4) + 1)] = y; rows[((row * 4) + 2)] = z; rows[((row * 4) + 3)] = w;
        }

        Row(row: ControlRow, w: sky.Atmosphere.FogDensity, x: lights.Count, y: lights.ShadowSlots[0], z: 0f);
        for (var index = 0; (index < SdfLights.MaxLights); index++) {
            var light = lights[index];
            var direction = ((light.Kind == SdfLightKind.Directional) ? Normalized(direction: light.Direction, fallback: SdfLights.DefaultSunDirection) : light.Direction);
            var row = (LightsRow + (index * RowsPerLight));

            Row(row: row, w: light.Weight, x: direction.X, y: direction.Y, z: direction.Z);
            Row(row: (row + 1), w: ((float)light.Kind), x: light.Color.X, y: light.Color.Y, z: light.Color.Z);
            Row(row: (row + 2), w: 0f, x: light.Param, y: light.Shadows, z: light.DynamicSlot);
        }

        var curvature = lights.Curvature;

        Row(row: CurvatureRow, w: curvature.InkLow, x: curvature.Cavity, y: curvature.Rim, z: curvature.Ink);
        Row(row: (CurvatureRow + 1), w: curvature.InkHigh, x: curvature.InkColor.X, y: curvature.InkColor.Y, z: curvature.InkColor.Z);

        Row(row: SoftboxControlRow, w: 0f, x: block.SoftboxCount, y: 0f, z: 0f);
        for (var index = 0; (index < SdfSky.MaxSoftboxes); index++) {
            var softbox = sky.Softboxes[index];
            var direction = ((softbox.Direction == Vector3.Zero) ? Vector3.Zero : Normalized(direction: softbox.Direction, fallback: Vector3.Zero));
            var row = (SoftboxesRow + (index * RowsPerSoftbox));

            Row(row: row, w: softbox.Weight, x: direction.X, y: direction.Y, z: direction.Z);
            Row(row: (row + 1), w: softbox.Size.X, x: softbox.Color.X, y: softbox.Color.Y, z: softbox.Color.Z);
            Row(row: (row + 2), w: 0f, x: softbox.Size.Y, y: softbox.Blur, z: 0f);
        }
        Row(row: HorizonLowRow, w: 0f, x: block.HorizonLow.X, y: block.HorizonLow.Y, z: block.HorizonLow.Z);
        Row(row: HorizonHighRow, w: 0f, x: block.HorizonHigh.X, y: block.HorizonHigh.Y, z: block.HorizonHigh.Z);

        return rows;
    }
    private static Vector3 Normalized(Vector3 direction, Vector3 fallback) {
        double x = direction.X, y = direction.Y, z = direction.Z;
        var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

        if (length <= 0d) {
            x = fallback.X; y = fallback.Y; z = fallback.Z;
            length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));
        }

        return new Vector3(x: ((float)(x / length)), y: ((float)(y / length)), z: ((float)(z / length)));
    }
    // Every value the environment table's decoders read, by the name of what it is.
    private static SortedDictionary<string, double[]> OldValues(float[] rows) {
        var values = new SortedDictionary<string, double[]>(comparer: StringComparer.Ordinal);

        double[] Lanes(int row, int first, int count) => [.. rows.AsSpan(length: count, start: ((row * 4) + first)).ToArray().Select(selector: static lane => ((double)lane))];
        double Lane(int row, int lane) => rows[((row * 4) + lane)];

        var count = Math.Min(val1: ((uint)Math.Max(val1: (Lane(lane: 0, row: ControlRow) + 0.5d), val2: 0d)), val2: ((uint)SdfLights.MaxLights));
        var shadow = ((int)Math.Round(a: Lane(lane: 1, row: ControlRow)));

        values["lightCount"] = [count];
        values["shadowSlots[0]"] = [shadow];
        for (var index = 0; (index < count); index++) {
            var row = (LightsRow + (index * RowsPerLight));

            values[$"light{index}.direction"] = Lanes(count: 3, first: 0, row: row);
            values[$"light{index}.weight"] = [Lane(lane: 3, row: row)];
            values[$"light{index}.color"] = Lanes(count: 3, first: 0, row: (row + 1));
            values[$"light{index}.kind"] = [((uint)(Lane(lane: 3, row: (row + 1)) + 0.5d))];
            values[$"light{index}.param"] = [Lane(lane: 0, row: (row + 2))];
            values[$"light{index}.slot"] = [Math.Round(a: Lane(lane: 2, row: (row + 2)))];
        }
        // The key light's direction, which the shadow march and the shading read, and the light the clouds are lit by:
        // the shadow light, or the pinned sun and white.
        values["key.direction"] = ((shadow >= 0) ? Lanes(count: 3, first: 0, row: (LightsRow + (shadow * RowsPerLight))) : PinnedSun);
        values["curvature"] = [.. Lanes(count: 4, first: 0, row: CurvatureRow), .. Lanes(count: 4, first: 0, row: (CurvatureRow + 1))];
        values["sky.fog"] = [Lane(lane: 3, row: ControlRow)];

        var softboxes = Math.Min(val1: ((uint)Math.Max(val1: (Lane(lane: 0, row: SoftboxControlRow) + 0.5d), val2: 0d)), val2: ((uint)SdfSky.MaxSoftboxes));

        values["sky.softboxCount"] = [softboxes];
        for (var index = 0; (index < softboxes); index++) {
            var row = (SoftboxesRow + (index * RowsPerSoftbox));

            values[$"sky.softbox{index}.direction"] = Lanes(count: 3, first: 0, row: row);
            values[$"sky.softbox{index}.weight"] = [Lane(lane: 3, row: row)];
            values[$"sky.softbox{index}.color"] = Lanes(count: 3, first: 0, row: (row + 1));
            values[$"sky.softbox{index}.size"] = [Lane(lane: 3, row: (row + 1)), Lane(lane: 0, row: (row + 2))];
            values[$"sky.softbox{index}.blur"] = [Lane(lane: 1, row: (row + 2))];
        }
        values["sky.horizon"] = [.. Lanes(count: 3, first: 0, row: HorizonLowRow), .. Lanes(count: 3, first: 0, row: HorizonHighRow)];

        return values;
    }
    // The records the tables pack for a frame, as bytes, and the frame's pass block.
    private static (byte[] Lights, byte[] Sky, byte[] Softboxes, byte[] Block) NewBytes(SdfLights lights, SdfSky sky) {
        var lightRecords = new SdfLight[SdfLights.MaxLights];
        var softboxes = new SdfSoftbox[SdfSky.MaxSoftboxes];
        var block = new byte[SdfFrameBlock.SizeBytes];

        lights.Pack(records: lightRecords);
        sky.Pack(block: out var skyBlock, details: new SdfSkyDetails(), farDistance: 40f, layers: new SdfSkyLayer[SdfSky.MaxLayers], lights: lights, softboxes: softboxes);
        SdfFrameBlock.Write(
            block: block,
            frame: Frame(lights: lights, sky: sky),
            height: 1u,
            tables: new SdfPassValues(DebugMode: 0, InstanceMaskWordCount: 1u, MeshDraws: 0u, ScreenCount: 0u),
            view: 0,
            width: 1u
        );

        return (
            MemoryMarshal.AsBytes(span: lightRecords.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<SdfSkyBlock>(reference: in skyBlock)).ToArray(),
            MemoryMarshal.AsBytes(span: softboxes.AsSpan()).ToArray(),
            block
        );
    }
    // Every value the kernels read of the records and the pass block, by the same names, through the structures given.
    private static SortedDictionary<string, double[]> NewValues((byte[] Lights, byte[] Sky, byte[] Softboxes, byte[] Block) bytes, IReadOnlyDictionary<string, ShaderInterfaceStructure> structures) {
        var values = new SortedDictionary<string, double[]>(comparer: StringComparer.Ordinal);
        var light = structures[SdfKernelInterfaces.Lights];
        var sky = structures[SdfKernelInterfaces.Sky];
        var softbox = structures[SdfKernelInterfaces.Softboxes];

        double[] Field(byte[] table, ShaderInterfaceStructure structure, int record, string field) {
            var member = structure.Members.Single(predicate: member => (member.Name == field));
            var start = ((int)((record * structure.SizeBytes) + member.Offset));
            var components = ((int)member.Type.ComponentCount());

            return [.. Enumerable.Range(count: components, start: 0).Select(selector: component => Component(bytes: table, offset: (start + (component * 4)), type: member.Type))];
        }
        double[] Value(string member, ShaderValueType type) {
            var offset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member));

            return [.. Enumerable.Range(count: ((int)type.ComponentCount()), start: 0).Select(selector: component => Component(bytes: bytes.Block, offset: (offset + (component * 4)), type: type))];
        }

        var count = ((uint)Value(member: SdfWorldPackage.LightCount, type: ShaderValueType.Uint)[0]);
        var shadow = ((int)Value(member: SdfWorldPackage.ShadowSlots, type: ShaderValueType.Int4)[0]);

        values["lightCount"] = [count];
        values["shadowSlots[0]"] = [shadow];
        for (var index = 0; (index < count); index++) {
            values[$"light{index}.direction"] = Field(field: nameof(SdfLight.Direction), record: index, structure: light, table: bytes.Lights);
            values[$"light{index}.weight"] = Field(field: nameof(SdfLight.Weight), record: index, structure: light, table: bytes.Lights);
            values[$"light{index}.color"] = Field(field: nameof(SdfLight.Color), record: index, structure: light, table: bytes.Lights);
            values[$"light{index}.kind"] = Field(field: nameof(SdfLight.Kind), record: index, structure: light, table: bytes.Lights);
            values[$"light{index}.param"] = Field(field: nameof(SdfLight.Param), record: index, structure: light, table: bytes.Lights);
            values[$"light{index}.slot"] = Field(field: nameof(SdfLight.DynamicSlot), record: index, structure: light, table: bytes.Lights);
        }
        values["key.direction"] = ((shadow >= 0) ? Field(field: nameof(SdfLight.Direction), record: shadow, structure: light, table: bytes.Lights) : PinnedSun);
        values["curvature"] = [
            .. Value(member: SdfWorldPackage.CurvatureCavity, type: ShaderValueType.Float),
            .. Value(member: SdfWorldPackage.CurvatureRim, type: ShaderValueType.Float),
            .. Value(member: SdfWorldPackage.CurvatureInk, type: ShaderValueType.Float),
            .. Value(member: SdfWorldPackage.CurvatureInkLow, type: ShaderValueType.Float),
            .. Value(member: SdfWorldPackage.CurvatureInkColor, type: ShaderValueType.Float3),
            .. Value(member: SdfWorldPackage.CurvatureInkHigh, type: ShaderValueType.Float),
        ];

        double[] Sky(string field) => Field(field: field, record: 0, structure: sky, table: bytes.Sky);

        values["sky.fog"] = Sky(field: nameof(SdfSkyBlock.FogExtinction));

        var softboxes = ((uint)Sky(field: nameof(SdfSkyBlock.SoftboxCount))[0]);

        values["sky.softboxCount"] = [softboxes];
        for (var index = 0; (index < softboxes); index++) {
            values[$"sky.softbox{index}.direction"] = Field(field: nameof(SdfSoftbox.Direction), record: index, structure: softbox, table: bytes.Softboxes);
            values[$"sky.softbox{index}.weight"] = Field(field: nameof(SdfSoftbox.Weight), record: index, structure: softbox, table: bytes.Softboxes);
            values[$"sky.softbox{index}.color"] = Field(field: nameof(SdfSoftbox.Color), record: index, structure: softbox, table: bytes.Softboxes);
            values[$"sky.softbox{index}.size"] = Field(field: nameof(SdfSoftbox.Size), record: index, structure: softbox, table: bytes.Softboxes);
            values[$"sky.softbox{index}.blur"] = Field(field: nameof(SdfSoftbox.Blur), record: index, structure: softbox, table: bytes.Softboxes);
        }
        values["sky.horizon"] = [.. Sky(field: nameof(SdfSkyBlock.HorizonLow)), .. Sky(field: nameof(SdfSkyBlock.HorizonHigh))];

        return values;
    }
    private static double Component(byte[] bytes, int offset, ShaderValueType type) => type switch {
        ShaderValueType.Uint => BinaryPrimitives.ReadUInt32LittleEndian(source: bytes.AsSpan(start: offset)),
        ShaderValueType.Int or ShaderValueType.Int2 or ShaderValueType.Int3 or ShaderValueType.Int4 => BinaryPrimitives.ReadInt32LittleEndian(source: bytes.AsSpan(start: offset)),
        _ => BinaryPrimitives.ReadSingleLittleEndian(source: bytes.AsSpan(start: offset)),
    };
    // The generated structures of the lights and sky tables, by member name.
    private static Dictionary<string, ShaderInterfaceStructure> Structures() =>
        SdfKernelInterfaces.LightAndSkyTables.ToDictionary(elementSelector: static member => member.Structure!, keySelector: static member => member.Name);
    // The names whose values differ between the two layouts.
    private static string[] Differences(SortedDictionary<string, double[]> old, SortedDictionary<string, double[]> current) => [
        .. old.Keys.Union(second: current.Keys).Where(predicate: name => !(old.TryGetValue(key: name, value: out var a) && current.TryGetValue(key: name, value: out var b) && a.SequenceEqual(second: b))),
    ];

    [Fact]
    public void EveryValueTheKernelsReadDecodesEquallyFromTheOldRowsAndTheNewRecords() {
        var lights = Lights();
        var sky = Sky();
        var old = OldValues(rows: OldRows(lights: lights, sky: sky));
        var current = NewValues(bytes: NewBytes(lights: lights, sky: sky), structures: Structures());

        Assert.Equal(expected: 5d, actual: old["lightCount"][0]);
        Assert.Equal(expected: 1d, actual: old["shadowSlots[0]"][0]);
        Assert.Empty(collection: Differences(current: current, old: old));
    }
    [Fact]
    public void AnUnlitUnauthoredFrameDecodesEquallyToo() {
        var lights = new SdfLights();
        var sky = new SdfSky();

        Assert.Empty(collection: Differences(
            current: NewValues(bytes: NewBytes(lights: lights, sky: sky), structures: Structures()),
            old: OldValues(rows: OldRows(lights: lights, sky: sky))
        ));
    }
    // The red leg: a structure whose two members a kernel would read swapped decodes the values the host packed into
    // each other's place, and the law names exactly those.
    [Fact]
    public void AStructureWithTwoMembersSwappedFails() {
        var lights = Lights();
        var sky = Sky();
        var structures = Structures();
        var block = structures[SdfKernelInterfaces.Sky];

        structures[SdfKernelInterfaces.Sky] = new ShaderInterfaceStructure(
            members: [.. block.Members.Select(selector: static member => member with {
                Name = member.Name switch {
                    nameof(SdfSkyBlock.HorizonLow) => nameof(SdfSkyBlock.HorizonHigh),
                    nameof(SdfSkyBlock.HorizonHigh) => nameof(SdfSkyBlock.HorizonLow),
                    _ => member.Name,
                },
            })],
            name: block.Name,
            sizeBytes: block.SizeBytes
        );

        Assert.Equal(
            actual: Differences(
                current: NewValues(bytes: NewBytes(lights: lights, sky: sky), structures: structures),
                old: OldValues(rows: OldRows(lights: lights, sky: sky))
            ),
            expected: ["sky.horizon"]
        );
    }
}
