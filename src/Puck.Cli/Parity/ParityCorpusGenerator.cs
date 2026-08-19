using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Parity;

/// <summary><c>puck parity --generate</c> — regenerates the pattern-world corpus under <c>tests/Puck.Parity/</c>.
/// Ports <c>generate.py</c> (the tool's former shape) member for member: same skeleton (<c>dive.world.json</c> with
/// water removed and the grounded kit stack from <c>play.world.json</c>), same four patterns, same per-pattern
/// creation/placement shape. A creation's canonical hash cannot be derived here (the validator's canonicalizer is
/// out of reach from this project) so it is supplied the same way the retired tool needed it: via
/// <c>--hashes id=hex64,...</c>, defaulting an unlisted id to the all-zero placeholder the validator's own refusal
/// then names the real value for.</summary>
internal static class ParityCorpusGenerator {
    private const string FontSource = "fonts/JetBrainsMono-Regular.ttf";
    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000";

    public static IReadOnlyList<string> Generate(string repositoryRoot, IReadOnlyDictionary<string, string> hashes) {
        var target = Path.Combine(path1: repositoryRoot, path2: "tests", path3: "Puck.Parity");
        var sourcePath = Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "Assets", "worlds", "dive.world.json"]);
        var playPath = Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "Assets", "worlds", "play.world.json"]);
        var patterns = new (string Name, PatternData Data)[] {
            ("parity-gradient", Gradient()),
            ("parity-edges", Edges()),
            ("parity-modifiers", Modifiers()),
            ("parity-glyphs", Glyphs()),
            ("parity-film-grain", FilmGrain()),
        };
        var written = new List<string>(capacity: patterns.Length);

        Directory.CreateDirectory(path: target);

        foreach (var (name, data) in patterns) {
            var sourceWorld = JsonNode.Parse(json: File.ReadAllText(path: sourcePath, encoding: Encoding.UTF8))!.AsObject();
            var play = JsonNode.Parse(json: File.ReadAllText(path: playPath, encoding: Encoding.UTF8))!.AsObject();
            var world = Build(hashes: hashes, name: name, pattern: data, play: play, target: target, world: sourceWorld);
            var path = Path.Combine(path1: target, path2: $"{name}.world.json");

            File.WriteAllText(path: path, contents: (world.ToJsonString(options: WriteOptions) + "\n"), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written.Add(item: path);
        }

        return written;
    }

    // Matches json.dumps: only the characters JSON syntax itself requires escaped are escaped. The default encoder
    // additionally escapes '+' and other characters that are plain ASCII in every corpus string here (a font
    // code-point range, "U+0020-U+007E").
    private static readonly System.Text.Json.JsonSerializerOptions WriteOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static JsonObject Build(string name, PatternData pattern, IReadOnlyDictionary<string, string> hashes, JsonObject world, JsonObject play, string target) {
        world.Remove(propertyName: "water");

        foreach (var section in new[] { "channels", "bodyMotionPrograms", "kits", "defaultSeatKit", "bindingOverlays" }) {
            world[section] = play[section]!.DeepClone();
        }

        world["$schema"] = "../../../src/Puck.World/Assets/worlds/puck.world.def.v1.schema.json";
        world["documentId"] = name;

        var spawnPoints = new JsonArray();

        for (var i = 0; (i < 4); i++) {
            spawnPoints.Add(value: new JsonObject {
                ["id"] = $"seat-{(i + 1)}",
                ["position"] = new JsonArray(((JsonNode)(3 * i)), ((JsonNode)0), ((JsonNode)10)),
            });
        }
        world["spawnPoints"] = spawnPoints;

        var population = world["population"]!.AsObject();

        population["capacity"] = 4;
        population["networkPlayers"] = 0;
        ((JsonObject)population["distribution"]!["region"]!)["sampleCount"] = 1;

        world["cameras"] = new JsonArray();
        world["references"] = new JsonArray();
        world["destinations"] = new JsonArray();

        var views = world["views"]!.AsObject();
        var playViews = play["views"]!.AsObject();

        views["seatRig"] = playViews["seatRig"]!.DeepClone();
        views["seatControl"] = playViews["seatControl"]!.DeepClone();

        var document = new JsonObject {
            ["schema"] = "puck.creation.v1",
            ["name"] = name,
            ["palette"] = pattern.Palette,
            ["shapes"] = pattern.Shapes,
            ["frames"] = null,
            ["chains"] = null,
            ["cameras"] = null,
            ["behavior"] = null,
        };

        if (pattern.TextRuns is { } textRuns) {
            document["textRuns"] = textRuns;
        }

        if (pattern.Text) {
            world["text"] = new JsonObject {
                ["defaultFont"] = "body",
                ["fonts"] = new JsonArray(new JsonObject {
                    ["name"] = "body",
                    ["source"] = FontSource,
                    ["hash"] = FontHash(target: target),
                    ["codePointRanges"] = new JsonArray("U+0020-U+007E"),
                    ["pixelSize"] = 48,
                    ["distanceRange"] = 8,
                }),
            };
        }

        if (pattern.Screens is { } screens) {
            world["screens"] = screens;
        }

        if (pattern.RenderExtensions is { } renderExtensions) {
            world["render"]!.AsObject()["extensions"] = renderExtensions;
        }

        var hash = (hashes.TryGetValue(key: name, value: out var pinned) ? pinned : ZeroHash);

        world["creations"] = new JsonArray(new JsonObject {
            ["id"] = name,
            ["document"] = document,
            ["hash"] = hash,
        });
        world["placements"] = new JsonArray(new JsonObject {
            ["id"] = name,
            ["creationId"] = name,
            ["position"] = new JsonArray(((JsonNode)0), ((JsonNode)0), ((JsonNode)3)),
            ["yawDegrees"] = 0,
            ["scale"] = 1.25,
            ["distribution"] = null,
            ["mirror"] = null,
            ["solid"] = new JsonObject { ["margin"] = 0 },
        });

        return world;
    }
    // AssetContentHash: sha256-64/{16 lowercase hex} = the digest's first 8 bytes read little-endian.
    private static string FontHash(string target) {
        var bytes = File.ReadAllBytes(path: Path.Combine(path1: target, path2: FontSource));
        var digest = SHA256.HashData(source: bytes);
        var prefix = BinaryPrimitives.ReadUInt64LittleEndian(source: digest.AsSpan(start: 0, length: 8));

        return $"sha256-64/{prefix:x16}";
    }

    private sealed record PatternData(JsonArray Palette, JsonArray Shapes, JsonArray? TextRuns = null, bool Text = false, JsonArray? Screens = null, JsonArray? RenderExtensions = null);

    // Gradients are where benign cross-backend codegen noise clusters: smooth-blend seams, curved normals, and
    // broad specular falloff, with no hard edges.
    private static PatternData Gradient() => new(
        Palette: [
            Material(albedo: (0.18, 0.19, 0.22), emissive: 0, shininess: 4, specular: 0.05),
            Material(albedo: (0.55, 0.25, 0.20), emissive: 0, shininess: 24, specular: 0.40),
            Material(albedo: (0.20, 0.30, 0.55), emissive: 0, shininess: 48, specular: 0.60),
            Material(albedo: (0.25, 0.50, 0.30), emissive: 0, shininess: 12, specular: 0.20),
        ],
        Shapes: [
            Shape(id: 0, name: "floor", type: "Plane", position: (0, 0, 0), scale: (1, 1, 1), material: 0),
            Shape(id: 1, name: "dome-red", type: "Sphere", position: (-3.2, 2.2, 0), scale: (2.6, 2.6, 2.6), material: 1, blend: "SmoothUnion", smooth: 0.5),
            Shape(id: 2, name: "dome-blue", type: "Ellipsoid", position: (3.2, 1.8, -0.6), scale: (2.9, 1.7, 2.3), material: 2, blend: "SmoothUnion", smooth: 0.5),
            Shape(id: 3, name: "ring", type: "Torus", position: (0, 1.1, -3.4), scale: (1.9, 1.9, 1.9), material: 3, blend: "SmoothUnion", smooth: 0.4),
            Shape(id: 4, name: "horn", type: "RoundCone", position: (0, 1.6, 3.0), scale: (1.4, 1.4, 1.4), material: 1, blend: "SmoothUnion", smooth: 0.3),
        ]
    );
    // A plain gradient scene with the sdf-film-grain post-render extension authored — proves the same 32-bit integer
    // hash (sdfPcg3d, keyed on pixel/grain-frame/seed) produces the same noise field on SPIR-V and DXIL. Reuses
    // Gradient's shapes: this pattern's own job is the extension, not a second geometry stress. flickerHz is 1
    // because the two backend legs are fenced to the same second, not the same tick: world.screenshot captures the
    // next composed frame after the fence, and that frame's ElapsedTicks differs by a few simulation ticks between
    // two processes — a per-tick grain frame would compare two different noise fields and fail on pacing alone.
    private static PatternData FilmGrain() => new(
        Palette: Gradient().Palette,
        Shapes: Gradient().Shapes,
        RenderExtensions: [
            new JsonObject {
                ["id"] = "sdf-film-grain",
                ["config"] = new JsonObject {
                    ["intensity"] = 0.08,
                    ["size"] = 1.5,
                    ["seed"] = 7,
                    ["flickerHz"] = 1,
                },
            },
        ]
    );
    // Hard high-contrast edges: near-black ground, white checker boxes, one yawed box for angled silhouettes, a
    // thin distant sliver, and an emissive bar.
    private static PatternData Edges() => new(
        Palette: [
            Material(albedo: (0.03, 0.03, 0.035), emissive: 0, shininess: 2, specular: 0.02),
            Material(albedo: (0.95, 0.95, 0.95), emissive: 0, shininess: 8, specular: 0.10),
            Material(albedo: (0.90, 0.55, 0.15), emissive: 0.8, shininess: 4, specular: 0.05),
        ],
        // The 8-cell checkerboard keeps only the cells where (i%4 + i//4) is even (i = 0, 2, 5, 7).
        Shapes: [
            Shape(id: 0, name: "floor", type: "Plane", position: (0, 0, 0), scale: (1, 1, 1), material: 0),
            Shape(id: 1, name: "checker-0", type: "Box", position: (-4.5, 0.75, -1.5), scale: (1.5, 1.5, 1.5), material: 1),
            Shape(id: 3, name: "checker-2", type: "Box", position: (1.5, 0.75, -1.5), scale: (1.5, 1.5, 1.5), material: 1),
            Shape(id: 6, name: "checker-5", type: "Box", position: (-1.5, 0.75, 1.5), scale: (1.5, 1.5, 1.5), material: 1),
            Shape(id: 8, name: "checker-7", type: "Box", position: (4.5, 0.75, 1.5), scale: (1.5, 1.5, 1.5), material: 1),
            Shape(id: 9, name: "yawed", type: "Box", position: (0, 0.9, 3.6), scale: (1.8, 1.8, 1.8), material: 1, rotation: YawRotation(degrees: 45)),
            Shape(id: 10, name: "sliver", type: "Box", position: (0, 3.4, -6.0), scale: (10.0, 0.12, 0.12), material: 1),
            Shape(id: 11, name: "beacon", type: "Box", position: (-6.4, 1.8, 0), scale: (0.35, 3.6, 0.35), material: 2),
        ]
    );
    // The shape-modifier stress: twist, bend, onion, dilate, and mirror all in one frame.
    private static PatternData Modifiers() => new(
        Palette: [
            Material(albedo: (0.20, 0.21, 0.24), emissive: 0, shininess: 6, specular: 0.08),
            Material(albedo: (0.60, 0.45, 0.20), emissive: 0, shininess: 20, specular: 0.30),
            Material(albedo: (0.30, 0.55, 0.60), emissive: 0, shininess: 32, specular: 0.45),
            Material(albedo: (0.55, 0.30, 0.55), emissive: 0, shininess: 16, specular: 0.25),
        ],
        Shapes: [
            Shape(id: 0, name: "floor", type: "Plane", position: (0, 0, 0), scale: (1, 1, 1), material: 0),
            Shape(id: 1, name: "twisted", type: "Box", position: (-3.4, 2.0, 0), scale: (1.2, 2.0, 1.2), material: 1, twist: 2.0),
            Shape(id: 2, name: "bent", type: "Box", position: (3.4, 1.9, -0.4), scale: (1.1, 1.9, 1.1), material: 2, bend: 0.6),
            Shape(id: 3, name: "shell", type: "Sphere", position: (0, 1.7, -3.2), scale: (1.7, 1.7, 1.7), material: 3, onion: 0.15),
            Shape(id: 4, name: "dilated", type: "Torus", position: (0, 1.0, 3.2), scale: (1.4, 1.4, 1.4), material: 1, dilate: 0.25),
            Shape(id: 5, name: "mirrored", type: "RoundCone", position: (2.2, 1.4, 2.0), scale: (0.9, 0.9, 0.9), material: 2, mirror: true),
        ]
    );
    // Both glyph tiers at once: marched Glyph geometry on a backdrop slab, and the dense decal tier on a
    // text-source screen sampling the same packed atlas.
    private static PatternData Glyphs() => new(
        Palette: [
            Material(albedo: (0.16, 0.17, 0.20), emissive: 0, shininess: 4, specular: 0.05),
            Material(albedo: (0.32, 0.30, 0.28), emissive: 0, shininess: 10, specular: 0.15),
            Material(albedo: (0.92, 0.78, 0.30), emissive: 0, shininess: 24, specular: 0.35),
            Material(albedo: (0.20, 0.45, 0.60), emissive: 0, shininess: 20, specular: 0.30),
        ],
        Shapes: [
            Shape(id: 0, name: "floor", type: "Plane", position: (0, 0, 0), scale: (1, 1, 1), material: 0),
            Shape(id: 1, name: "slab", type: "Box", position: (0, 1.9, 0.6), scale: (7.0, 3.4, 0.6), material: 1),
        ],
        TextRuns: [
            new JsonObject {
                ["text"] = "PUCK PARITY",
                ["position"] = Vec3(x: 0, y: 2.7, z: 0.9),
                ["rotation"] = IdentityRotation(),
                ["emHeight"] = 0.6,
                ["depth"] = 0.06,
                ["mode"] = "emboss",
                ["material"] = 2,
                ["maxWidth"] = 2.4,
                ["align"] = "center",
                ["tracking"] = 0.04,
                ["lineSpacing"] = 1.1,
            },
            new JsonObject {
                ["text"] = "ENGRAVED",
                ["position"] = Vec3(x: 0, y: 0.9, z: 0.9),
                ["rotation"] = IdentityRotation(),
                ["emHeight"] = 0.45,
                ["depth"] = 0.05,
                ["mode"] = "engrave",
                ["material"] = 3,
            },
        ],
        Text: true,
        Screens: [
            new JsonObject {
                ["index"] = 0,
                ["origin"] = new JsonArray(((JsonNode)(-4.6)), ((JsonNode)1.7), ((JsonNode)4.4)),
                ["right"] = new JsonArray(((JsonNode)1), ((JsonNode)0), ((JsonNode)0)),
                ["up"] = new JsonArray(((JsonNode)0), ((JsonNode)1), ((JsonNode)0)),
                ["halfWidth"] = 1.7,
                ["halfHeight"] = 1.1,
                ["halfDepth"] = 0.12,
                ["round"] = 0.05,
                ["source"] = new JsonObject {
                    ["$type"] = "text",
                    ["lines"] = new JsonArray("DECAL TIER", "ABCDEFGHIJKLM", "0123456789", "THE QUICK FOX"),
                    ["foreground"] = "#FFD24A",
                    ["background"] = "#101018",
                },
                ["route"] = new JsonObject { ["engageable"] = false, ["engageRadius"] = 0, ["autoInsert"] = false },
            },
        ]
    );
    private static JsonObject Material((PyNum X, PyNum Y, PyNum Z) albedo, PyNum emissive, PyNum specular, PyNum shininess) => new() {
        ["albedo"] = Vec3(x: albedo.X, y: albedo.Y, z: albedo.Z),
        ["emissive"] = emissive.ToJson(),
        ["specular"] = specular.ToJson(),
        ["shininess"] = shininess.ToJson(),
    };

    // The shape's ordered ShapeDomainOp list — "mirror: true"'s exact fold is one symmetry op across the
    // Vector3.UnitX plane (see CreationDocument.cs's ShapeDocument.Domain remarks).
    private static readonly JsonArray s_mirrorDomain = [new JsonObject { ["$type"] = "symmetry", ["normal"] = new JsonArray(((JsonNode)1), ((JsonNode)0), ((JsonNode)0)) }];

    private static JsonObject Shape(
        int id,
        string name,
        string type,
        (PyNum X, PyNum Y, PyNum Z) position,
        (PyNum X, PyNum Y, PyNum Z) scale,
        int material,
        string? blend = null,
        PyNum smooth = default,
        JsonArray? rotation = null,
        bool mirror = false,
        PyNum twist = default,
        PyNum onion = default,
        PyNum bend = default,
        PyNum dilate = default
    ) => new() {
        ["id"] = id,
        ["name"] = name,
        ["type"] = type,
        ["position"] = Vec3(x: position.X, y: position.Y, z: position.Z),
        ["rotation"] = (rotation ?? IdentityRotation()),
        ["scale"] = Vec3(x: scale.X, y: scale.Y, z: scale.Z),
        ["material"] = material,
        ["blend"] = (blend ?? "Union"),
        ["smooth"] = smooth.ToJson(),
        ["group"] = 0,
        ["domain"] = (mirror ? s_mirrorDomain.DeepClone() : null),
        ["twist"] = twist.ToJson(),
        ["onion"] = onion.ToJson(),
        ["bend"] = bend.ToJson(),
        ["dilate"] = dilate.ToJson(),
    };
    private static JsonArray IdentityRotation() => new() { 0, 0, 0, 1 };
    private static JsonArray YawRotation(double degrees) {
        var half = (((degrees * Math.PI) / 180.0) / 2.0);
        var y = Math.Round(value: Math.Sin(a: half), digits: 7);
        var w = Math.Round(value: Math.Cos(d: half), digits: 7);

        return new JsonArray { 0, PyNum.OfDouble(value: y).ToJson(), 0, PyNum.OfDouble(value: w).ToJson() };
    }
    private static JsonArray Vec3(PyNum x, PyNum y, PyNum z) => new() { x.ToJson(), y.ToJson(), z.ToJson() };

    /// <summary>A JSON number carrying Python's own int/float distinction — an int literal renders bare
    /// (<c>3</c>), a float literal always keeps a decimal point (<c>3.0</c>), matching <c>json.dumps</c>'s
    /// behavior on the value <c>generate.py</c> built from. <see cref="System.Text.Json.Nodes.JsonValue"/>'s own
    /// double factory drops a whole-valued double's trailing <c>.0</c>, which this type exists to restore.</summary>
    private readonly struct PyNum {
        private readonly double m_value;
        private readonly bool m_isDouble;

        private PyNum(double value, bool isDouble) {
            m_value = value;
            m_isDouble = isDouble;
        }

        public static implicit operator PyNum(int value) => new(isDouble: false, value: value);
        public static implicit operator PyNum(double value) => new(isDouble: true, value: value);

        public static PyNum OfDouble(double value) => new(isDouble: true, value: value);
        public JsonNode ToJson() => (m_isDouble ? PyFloat(value: m_value) : JsonValue.Create(value: ((int)m_value))!);

        // System.Text.Json's own double formatter already matches Python's shortest-round-trip float repr except
        // for a whole-valued double, which it renders bare (`3`) where Python always keeps the point (`3.0`).
        private static JsonNode PyFloat(double value) {
            var text = value.ToString(provider: CultureInfo.InvariantCulture);

            if (!text.Contains(value: '.') && !text.Contains(value: 'e') && !text.Contains(value: 'E')) {
                text += ".0";
            }

            return JsonNode.Parse(json: text)!;
        }
    }
}
