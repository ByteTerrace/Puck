using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.SdfVm;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The <c>--forge-bake-calibration</c> tool mode: HOW CLOSE does the SDF→bake pipeline land to hand-pixelled art? It
/// authors SDF stand-ins for Volley's hand tiles (the solid paddle bar, the ball dot, the dashed court net), bakes
/// them DMG classic at the hand art's native sizes (paddle 8×24, ball 8×8, net 8×8), and reports the per-tile
/// pixel-match percentage against the hand-authored 2bpp bytes — comparing DMG shade indices, with the hand art
/// mapped through its own palette's luma onto the same 4-shade ramp so both sides speak one currency. A side-by-side
/// PNG (hand | baked, ×8) lands in the output directory. This is a CALIBRATION REPORT, never a gate: a low match is
/// a finding to read on stderr, not a failure exit.
/// </summary>
internal static class BakeCalibration {
    private const int Scale = 8;
    private const int PanelGap = 2;   // Between a subject's hand and baked halves, pre-scale pixels.
    private const int SubjectGap = 4; // Between subjects, pre-scale pixels.
    private const float TanHalfFov = 0.41421356f; // tan(45°/2)

    // The DMG display ramp for the comparison PNG (shade 0 lightest — the same pea-green panel look the bake previews).
    private static readonly (byte R, byte G, byte B)[] DisplayRamp = [
        (224, 248, 208),
        (136, 192, 112),
        (52, 104, 86),
        (8, 24, 32),
    ];

    // One calibration subject: the hand tiles stacked top-to-bottom over an 8-pixel-wide column, and the SDF stand-in
    // framed head-on at the same native size.
    private sealed record CalibrationSubject(string Name, int Width, int Height, byte[] HandTiles, HgbImage.Rgb[] HandColours, SdfProgram Program);

    /// <summary>Runs the calibration inside the shared one-shot GPU host; returns 0 on success (always — low match
    /// percentages are findings, not failures).</summary>
    /// <param name="outputDirectory">Where the comparison PNG lands.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string outputDirectory, string[] args) {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        return ForgeHost.RunAsync(args: args, work: (device, gpu) => Run(device: device, gpu: gpu, outputDirectory: outputDirectory));
    }

    private static int Run(IGpuDeviceContext device, IGpuComputeServices gpu, string outputDirectory) {
        _ = Directory.CreateDirectory(path: outputDirectory);

        var subjects = BuildSubjects();
        var panels = new List<(CalibrationSubject Subject, byte[] HandShades, byte[] BakedShades)>(capacity: subjects.Count);
        var totalMatched = 0L;
        var totalPixels = 0L;

        foreach (var subject in subjects) {
            var handShades = HandShades(subject: subject);
            var bakedShades = BakeShades(device: device, gpu: gpu, subject: subject);
            var report = ReportSubject(bakedShades: bakedShades, handShades: handShades, subject: subject, matched: out var matched);

            Console.Error.WriteLine(value: report);
            totalMatched += matched;
            totalPixels += handShades.Length;
            panels.Add(item: (subject, handShades, bakedShades));
        }

        Console.Error.WriteLine(value: $"bake-calibration | overall {((100.0 * totalMatched) / totalPixels):F1}% shade match over {totalPixels} pixels (hand indices mapped through their palette's luma onto the DMG ramp; low match is a finding, not a failure)");

        var path = Path.Combine(path1: outputDirectory, path2: "calibration.png");

        WriteComparisonPng(panels: panels, path: path);
        Console.Error.WriteLine(value: $"bake-calibration | wrote {path} (hand | baked per subject, ×{Scale})");

        return 0;
    }

    // ---- the subjects -----------------------------------------------------------------------------------------------

    private static List<CalibrationSubject> BuildSubjects() {
        var art = VolleyRom.CalibrationArt();

        return [
            new CalibrationSubject(HandColours: art.ObjectColours, HandTiles: StackTiles(count: 3, tile: art.PaddleTile), Height: 24, Name: "paddle", Program: PaddleScene(), Width: 8),
            new CalibrationSubject(HandColours: art.ObjectColours, HandTiles: art.BallTile, Height: 8, Name: "ball", Program: BallScene(), Width: 8),
            new CalibrationSubject(HandColours: art.BackgroundColours, HandTiles: art.NetTile, Height: 8, Name: "net", Program: NetScene(), Width: 8),
        ];
    }
    private static byte[] StackTiles(byte[] tile, int count) {
        var stacked = new byte[(tile.Length * count)];

        for (var index = 0; (index < count); index++) {
            tile.CopyTo(array: stacked, index: (index * tile.Length));
        }

        return stacked;
    }

    // The stand-in scenes author in pixel units (one world unit = one pixel at the target plane) against a dark court
    // backdrop; emissive flattens the lighting so the comparison measures the CRUSH, not the shading model.
    private static SdfProgram PaddleScene() {
        var builder = new SdfProgramBuilder();

        AddBackdrop(builder: builder);
        _ = builder.ResetPoint().Box(halfExtents: new Vector3(x: 4.6f, y: 12.6f, z: 0.4f), round: 0f, material: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.93f, y: 0.94f, z: 0.97f), Emissive: 0.8f)));

        return builder.Build();
    }
    private static SdfProgram BallScene() {
        var builder = new SdfProgramBuilder();

        AddBackdrop(builder: builder);
        _ = builder.ResetPoint().Sphere(radius: 3.1f, material: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.96f, y: 0.84f, z: 0.36f), Emissive: 0.8f)));

        return builder.Build();
    }
    private static SdfProgram NetScene() {
        var builder = new SdfProgramBuilder();

        AddBackdrop(builder: builder);

        var cyan = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.38f, y: 0.69f, z: 0.78f), Emissive: 0.8f));

        // The hand tile's dash pattern: columns 3-4 lit on rows 0-3 and 6-7 — a long dash over a short one.
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 2f, z: 0f)).Box(halfExtents: new Vector3(x: 1f, y: 2f, z: 0.4f), round: 0f, material: cyan);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -3f, z: 0f)).Box(halfExtents: new Vector3(x: 1f, y: 1f, z: 0.4f), round: 0f, material: cyan);

        return builder.Build();
    }
    private static void AddBackdrop(SdfProgramBuilder builder) {
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0f, y: 0f, z: -6f))
            .Box(halfExtents: new Vector3(x: 40f, y: 40f, z: 0.5f), round: 0f, material: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.05f, y: 0.06f, z: 0.10f), Emissive: 1.0f)));
    }

    // ---- the two shade sources --------------------------------------------------------------------------------------

    // The baked side: one head-on DMG classic background bake at native size, its tiles read back per map cell.
    private static byte[] BakeShades(IGpuDeviceContext device, IGpuComputeServices gpu, CalibrationSubject subject) {
        var style = BakeStyles.Classic;
        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: (45f * (MathF.PI / 180f)),
            position: new Vector3(x: 0f, y: 0f, z: ((subject.Height / 2f) / TanHalfFov)),
            target: Vector3.Zero,
            viewportHeight: (uint)(subject.Height * style.SupersampleFactor),
            viewportWidth: (uint)(subject.Width * style.SupersampleFactor)
        );
        var plan = new BakePlan(
            Budget: new BakeBudget(),
            Intent: BakeIntent.Background,
            NativeHeight: subject.Height,
            NativeWidth: subject.Width,
            Style: style,
            Target: BakeTarget.Dmg,
            Views: [new BakeView(Camera: camera, Name: subject.Name, Program: subject.Program)]
        );
        var background = (BakePipeline.Run(device: device, gpu: gpu, plan: plan).Assets.Background
            ?? throw new InvalidOperationException(message: "The calibration bake produced no background."));
        var shades = new byte[(subject.Width * subject.Height)];
        var tilesWide = (subject.Width / 8);

        for (var tileY = 0; (tileY < (subject.Height / 8)); tileY++) {
            for (var tileX = 0; (tileX < tilesWide); tileX++) {
                var tile = HgbImage.DecodeTile2bpp(tileBytes: background.Tiles.TileData.AsSpan(start: (background.TileMap[((tileY * 32) + tileX)] * 16), length: 16));

                for (var row = 0; (row < 8); row++) {
                    for (var column = 0; (column < 8); column++) {
                        shades[((((tileY * 8) + row) * subject.Width) + ((tileX * 8) + column))] = tile[((row * 8) + column)];
                    }
                }
            }
        }

        return shades;
    }

    // The hand side: each 2bpp index maps through its Volley palette colour's luma onto the DMG ramp — the honest
    // shared currency (the hand palettes are display colours; the bake's indices are display shades).
    private static byte[] HandShades(CalibrationSubject subject) {
        var shades = new byte[(subject.Width * subject.Height)];
        var tileCount = (subject.HandTiles.Length / 16);

        for (var tile = 0; (tile < tileCount); tile++) {
            var indices = HgbImage.DecodeTile2bpp(tileBytes: subject.HandTiles.AsSpan(start: (tile * 16), length: 16));

            for (var pixel = 0; (pixel < 64); pixel++) {
                var colour = subject.HandColours[indices[pixel]];
                var luma = StyleGrade.Luma(b: (colour.B / 255f), g: (colour.G / 255f), r: (colour.R / 255f));

                // The subjects are one tile wide, so tile t is exactly rows t*8..t*8+7.
                shades[((tile * 64) + pixel)] = (byte)StyleGrade.DmgShade(luma: luma);
            }
        }

        return shades;
    }

    // ---- the report + the comparison PNG ----------------------------------------------------------------------------

    private static string ReportSubject(byte[] bakedShades, byte[] handShades, CalibrationSubject subject, out long matched) {
        var perTile = new List<string>();
        var tilesHigh = (subject.Height / 8);

        matched = 0L;

        for (var tile = 0; (tile < tilesHigh); tile++) {
            var tileMatched = 0;

            for (var pixel = 0; (pixel < 64); pixel++) {
                if (handShades[((tile * 64) + pixel)] == bakedShades[((tile * 64) + pixel)]) {
                    tileMatched++;
                }
            }

            matched += tileMatched;
            perTile.Add(item: $"tile {tile}: {tileMatched}/64 ({((100.0 * tileMatched) / 64.0):F1}%)");
        }

        return $"bake-calibration | {subject.Name} {subject.Width}×{subject.Height} | {string.Join(separator: " | ", values: perTile)} | subject {((100.0 * matched) / handShades.Length):F1}%";
    }
    private static void WriteComparisonPng(IReadOnlyList<(CalibrationSubject Subject, byte[] HandShades, byte[] BakedShades)> panels, string path) {
        var width = 0;
        var height = 0;

        foreach (var (subject, _, _) in panels) {
            width += (((subject.Width * 2) + PanelGap) + SubjectGap);
            height = Math.Max(val1: height, val2: subject.Height);
        }

        width -= SubjectGap;

        var rgba = new byte[(((width * Scale) * (height * Scale)) * 4)];

        FillBackground(rgba: rgba);

        var originX = 0;

        foreach (var (subject, handShades, bakedShades) in panels) {
            PaintShades(height: subject.Height, originX: originX, rgba: rgba, rowStride: (width * Scale), shades: handShades, width: subject.Width);
            PaintShades(height: subject.Height, originX: ((originX + subject.Width) + PanelGap), rgba: rgba, rowStride: (width * Scale), shades: bakedShades, width: subject.Width);
            originX += (((subject.Width * 2) + PanelGap) + SubjectGap);
        }

        PngEncoder.Write(height: (height * Scale), path: path, rgba: rgba, width: (width * Scale));
    }
    private static void FillBackground(byte[] rgba) {
        for (var offset = 0; (offset < rgba.Length); offset += 4) {
            rgba[offset] = 58;
            rgba[(offset + 1)] = 58;
            rgba[(offset + 2)] = 70;
            rgba[(offset + 3)] = 0xFF;
        }
    }
    private static void PaintShades(byte[] rgba, int rowStride, byte[] shades, int width, int height, int originX) {
        for (var y = 0; (y < (height * Scale)); y++) {
            for (var x = 0; (x < (width * Scale)); x++) {
                var (r, g, b) = DisplayRamp[shades[(((y / Scale) * width) + (x / Scale))]];
                var offset = (((y * rowStride) + ((originX * Scale) + x)) * 4);

                rgba[offset] = r;
                rgba[(offset + 1)] = g;
                rgba[(offset + 2)] = b;
                rgba[(offset + 3)] = 0xFF;
            }
        }
    }
}
