using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every bindable presentation scalar declares one domain (<see cref="WorldValueFields"/>), and
/// both the validator and the presentation read it. The validator refuses a literal, a key, or (at load) a bound row's
/// starting value outside it, naming the field. The presentation maps whatever a bound row presents by one rule: a value
/// inside is presented as it is, a finite value beyond a closed end is clamped to it, and a value that is not finite or
/// lies at or beyond an open end holds the binding's last valid value. Each binding is reported when it leaves its domain
/// and when it returns, never once per frame, and does no counted work while its input is unchanged.
/// </summary>
public sealed class WorldValueDomainLawTests {
    internal const string Row = "bound";

    /// <summary>One world as a presentation sees it live: a document whose state rows change, and the one state mirror
    /// that follows it, so a binding keeps its identity across the writes a law makes.</summary>
    internal sealed class LiveWorld {
        public LiveWorld(WorldDefinition definition) {
            Current = definition;
            Mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => Current));
            Mirror.Install(engineTick: 0UL, tick: 0UL);
        }

        public WorldDefinition Current { get; private set; }
        public WorldStateMirror Mirror { get; }

        public void Set(WorldDefinition definition) {
            Current = definition;
            Mirror.Install(engineTick: 0UL, tick: 0UL);
        }
    }

    // How a test authors a field into a document and reads back what the presentation made of it.
    private sealed record Case(Func<BindableScalar, WorldDefinition> Author, string Path, Func<WorldDefinition, WorldStateMirror, WorldValueDomainGuard, float>? Present = null, float[]? Controls = null);

    private static readonly IReadOnlyDictionary<WorldValueField, Case> Cases = new Dictionary<WorldValueField, Case> {
        [WorldValueFields.LightBounce] = Lit(author: s => new WorldRenderLight.Directional { Bounce = s }, present: static e => e.Lights[0].Bounce, member: "bounce"),
        [WorldValueFields.DirectionalWeight] = Lit(author: s => new WorldRenderLight.Directional(Weight: s), present: static e => e.Lights[0].Weight, member: "weight"),
        [WorldValueFields.DirectionalAngularRadius] = Lit(author: s => new WorldRenderLight.Directional(AngularRadius: s), present: static e => MathF.Atan(x: e.Lights[0].Param), member: "angularRadius"),
        [WorldValueFields.EnvironmentAmbient] = Env(s => new WorldRenderEnvironment(Ambient: s), e => e.Sky.Block.Ambient, "ambient"),
        [WorldValueFields.EnvironmentReflection] = Env(s => new WorldRenderEnvironment(Reflection: s), e => e.Sky.Block.Reflection, "reflection"),
        [WorldValueFields.PanelIntensity] = Sky(s => new WorldRenderSkyLayer.Panel(Intensity: s), e => e.Sky.First<SdfSkyPanel>().Intensity, "intensity"),
        [WorldValueFields.PanelBlur] = Sky(s => new WorldRenderSkyLayer.Panel(Blur: s), e => e.Sky.First<SdfSkyPanel>().Blur, "blur"),
        [WorldValueFields.RimWeight] = Lit(author: s => new WorldRenderLight.Rim(Weight: s), present: static e => e.Lights[0].Weight, member: "weight"),
        [WorldValueFields.RimPower] = Lit(author: s => new WorldRenderLight.Rim(Power: s), present: static e => e.Lights[0].Param, member: "power"),
        [WorldValueFields.PointRadius] = Lit(author: s => new WorldRenderLight.Point(Radius: s), present: static e => e.Lights[0].Param, member: "radius"),
        [WorldValueFields.PointWeight] = Lit(author: s => new WorldRenderLight.Point(Weight: s), present: static e => e.Lights[0].Weight, member: "weight"),
        [WorldValueFields.OccluderRadius] = Lit(author: s => new WorldRenderLight.Occluder(Radius: s), present: static e => e.Lights[0].Param, member: "radius"),
        [WorldValueFields.OccluderWeight] = Lit(author: s => new WorldRenderLight.Occluder(Weight: s), present: static e => e.Lights[0].Weight, member: "weight"),
        [WorldValueFields.CurvatureCavity] = Curve(author: s => new WorldRenderCurvature(Cavity: s), present: static e => e.Lights.Curvature.Cavity, member: "cavity"),
        [WorldValueFields.CurvatureRim] = Curve(author: s => new WorldRenderCurvature(Rim: s), present: static e => e.Lights.Curvature.Rim, member: "rim"),
        [WorldValueFields.CurvatureInk] = Curve(author: s => new WorldRenderCurvature(Ink: s), present: static e => e.Lights.Curvature.Ink, member: "ink"),
        // The ink band's ends must stay ascending, which a bound row cannot promise, so neither binds.
        [WorldValueFields.CurvatureInkLow] = Curve(author: s => new WorldRenderCurvature(InkLow: s), present: null, member: "inkLow", controls: [0f]),
        [WorldValueFields.CurvatureInkHigh] = Curve(author: s => new WorldRenderCurvature(InkHigh: s), present: null, member: "inkHigh", controls: [SdfCurvature.DefaultInkHigh]),
        [WorldValueFields.StopElevation] = new Case(
            Author: s => Layer(layer: new WorldRenderSkyLayer.Gradient(Stops: [
                new WorldRenderSkyStop(Elevation: s, Color: new BindableColor(Raw: "#000000")),
                new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#FFFFFF")),
            ])),
            Controls: [-1f, 0f],
            Path: "render.sky.layers[0].stops[0].elevation"
        ),
        [WorldValueFields.SunDiscRadius] = Sky(author: s => new WorldRenderSkyLayer.SunDisc(Radius: s), present: static e => e.Sky.First<SdfSkyDisc>().Radius, member: "radius"),
        [WorldValueFields.SunDiscIntensity] = Sky(author: s => new WorldRenderSkyLayer.SunDisc(Intensity: s), present: static e => e.Sky.First<SdfSkyDisc>().Intensity, member: "intensity"),
        [WorldValueFields.StarBrightness] = Sky(author: s => new WorldRenderSkyLayer.Stars(Brightness: s), present: static e => e.Sky.First<SdfSkyStars>().Brightness, member: "brightness"),
        [WorldValueFields.TwinkleShare] = Sky(author: s => new WorldRenderSkyLayer.Stars(Twinkle: new WorldRenderSkyTwinkle(Share: s)), present: static e => e.Sky.First<SdfSkyStars>().TwinkleShare, member: "twinkle.share"),
        [WorldValueFields.TwinkleDepth] = Sky(author: s => new WorldRenderSkyLayer.Stars(Twinkle: new WorldRenderSkyTwinkle(Depth: s)), present: static e => e.Sky.First<SdfSkyStars>().TwinkleDepth, member: "twinkle.depth"),
        // A rate the tick integrates may not bind a state row.
        [WorldValueFields.TwinkleRate] = Sky(author: s => new WorldRenderSkyLayer.Stars(Twinkle: new WorldRenderSkyTwinkle(Rate: s)), present: null, member: "twinkle.rate"),
        [WorldValueFields.CloudCoverage] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Coverage: s), present: static e => e.Sky.First<SdfSkyClouds>().Coverage, member: "coverage"),
        [WorldValueFields.CloudSoftness] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Softness: s), present: static e => e.Sky.First<SdfSkyClouds>().Softness, member: "softness"),
        [WorldValueFields.CloudScale] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Scale: s), present: static e => e.Sky.First<SdfSkyClouds>().Scale, member: "scale"),
        [WorldValueFields.SkyLayerOpacity] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Coverage: new BindableScalar(literal: 0.5f)) { Opacity = s }, present: static e => e.Sky.LayerAt(index: e.Sky.IndexOf(kind: SdfSkyLayerKind.Clouds)).Opacity, member: "opacity"),
        [WorldValueFields.AuroraIntensity] = Sky(author: s => new WorldRenderSkyLayer.Aurora(Intensity: s), present: static e => e.Sky.First<SdfSkyAurora>().Intensity, member: "intensity"),
        // An aurora's angles reach the kernel as heights (their sines), so the presentation reads no angle back.
        [WorldValueFields.AuroraBase] = Sky(author: s => new WorldRenderSkyLayer.Aurora(Base: s), present: null, member: "base"),
        [WorldValueFields.AuroraHeight] = Sky(author: s => new WorldRenderSkyLayer.Aurora(Height: s), present: null, member: "height"),
        [WorldValueFields.AuroraFold] = Sky(author: s => new WorldRenderSkyLayer.Aurora(Fold: s), present: null, member: "fold"),
        [WorldValueFields.NoiseCoverage] = Sky(author: s => new WorldRenderSkyLayer.Noise(Coverage: s), present: static e => e.Sky.First<SdfSkyNoise>().Coverage, member: "coverage"),
        [WorldValueFields.PanoramaIntensity] = Sky(author: s => new WorldRenderSkyLayer.Panorama(Intensity: s, Screen: Fixtures.TestPatternScreenIndex), present: static e => e.Sky.First<SdfSkyPanorama>().Intensity, member: "intensity"),
        [WorldValueFields.FogDensity] = Air(author: s => new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: s)), present: static e => e.Sky.Atmosphere.FogDensity, member: "fog.density"),
        [WorldValueFields.AirFalloff] = Air(author: s => new WorldRenderAtmosphere(Fog: new WorldRenderFog(Height: new WorldRenderAirHeight(Falloff: s))), present: static e => e.Sky.Atmosphere.FogFalloff, member: "fog.height.falloff"),
        [WorldValueFields.HazeAmount] = Air(author: s => new WorldRenderAtmosphere(Haze: new WorldRenderHaze(Amount: s)), present: static e => e.Sky.Atmosphere.HazeAmount, member: "haze.amount"),
        [WorldValueFields.HazeAnisotropy] = Air(author: s => new WorldRenderAtmosphere(Haze: new WorldRenderHaze(Anisotropy: s)), present: static e => e.Sky.Atmosphere.HazeAnisotropy, member: "haze.anisotropy"),
        [WorldValueFields.MediumExtinction] = Air(author: s => new WorldRenderAtmosphere(Medium: new WorldRenderMedium(Extinction: s)), present: static e => e.Sky.Atmosphere.MediumExtinction, member: "medium.extinction"),
        [WorldValueFields.ScrimAlpha] = new Case(
            Author: s => Fixtures.BuildDocument() with {
                ThemeRaw = WorldThemeValidationLawTests.MinimalTheme() with {
                    Color = WorldThemeValidationLawTests.MinimalColor() with { ScrimPanel = new WorldThemeScrim(Alpha: s, Color: new BindableColor(Raw: "#101010")) },
                },
            },
            Path: "theme.color.scrimPanel.alpha",
            Present: static (definition, mirror, domains) => new WorldThemeResolve(domains: domains).Resolve(definition: definition, mirror: mirror, revision: 1).Color.ScrimPanel.Alpha
        ),
        [WorldValueFields.BloomHaloAlpha] = Bloom(field: nameof(WorldThemeElevation.BloomHaloAlpha), author: static (e, s) => e with { BloomHaloAlpha = s }, present: static e => e.BloomHaloAlpha),
        [WorldValueFields.BloomRingAlpha] = Bloom(field: nameof(WorldThemeElevation.BloomRingAlpha), author: static (e, s) => e with { BloomRingAlpha = s }, present: static e => e.BloomRingAlpha),
        [WorldValueFields.BloomNeutralHaloAlpha] = Bloom(field: nameof(WorldThemeElevation.BloomNeutralHaloAlpha), author: static (e, s) => e with { BloomNeutralHaloAlpha = s }, present: static e => e.BloomNeutralHaloAlpha),
        [WorldValueFields.BloomNeutralRingAlpha] = Bloom(field: nameof(WorldThemeElevation.BloomNeutralRingAlpha), author: static (e, s) => e with { BloomNeutralRingAlpha = s }, present: static e => e.BloomNeutralRingAlpha),
        [WorldValueFields.BloomHeldInsetAlpha] = Bloom(field: nameof(WorldThemeElevation.BloomHeldInsetAlpha), author: static (e, s) => e with { BloomHeldInsetAlpha = s }, present: static e => e.BloomHeldInsetAlpha),
        [WorldValueFields.MarkerChipAlpha] = Marker(author: s => new WorldMarkerStyle(ChipAlpha: s, Size: 12f, RingAlpha: 0.35f, RingColor: new BindableColor(Raw: "#9BA3AB")), present: static a => a.Chip, member: "chipAlpha"),
        [WorldValueFields.BlendWeight] = new Case(
            Author: static s => Fixtures.BuildDocument() with {
                CamerasRaw = [
                    Camera(name: "probe", rig: Program(name: "probe-rig", operations: new WorldCameraProgramOp.Blend(A: "low-rig", B: "high-rig", Weight: s))),
                    Camera(name: "low", rig: Program(name: "low-rig", operations: new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: LowFov))),
                    Camera(name: "high", rig: Program(name: "high-rig", operations: new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: HighFov))),
                ],
            },
            Path: "cameras[0].rig.operations[0].weight",
            // The blend's field of view is the two programs' interpolated by the presented weight.
            Present: static (definition, mirror, domains) => ((WorldCameraRigCompiler.Compile(
                definition: definition,
                domains: domains,
                mirror: mirror,
                program: definition.Cameras[0].Rig
            ).Resolve(
                anchor: new SdfAnchor(Orientation: Quaternion.Identity, Position: Vector3.Zero),
                clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
            ).FovRadians - LowFov) / (HighFov - LowFov))
        ),
        [WorldValueFields.FieldOfView] = new Case(
            Author: static s => Fixtures.BuildDocument() with {
                CamerasRaw = [Camera(name: "probe", rig: Program(name: "probe-rig", operations: new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: s)))],
            },
            Path: "cameras[0].rig.operations[0].fieldOfViewRadians",
            Present: static (definition, mirror, domains) => WorldCameraRigCompiler.Compile(
                definition: definition,
                domains: domains,
                mirror: mirror,
                program: definition.Cameras[0].Rig
            ).Resolve(
                anchor: new SdfAnchor(Orientation: Quaternion.Identity, Position: Vector3.Zero),
                clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
            ).FovRadians
        ),
        [WorldValueFields.MarkerRingAlpha] = Marker(author: s => new WorldMarkerStyle(ChipAlpha: 0.9f, Size: 12f, RingAlpha: s, RingColor: new BindableColor(Raw: "#9BA3AB")), present: static a => a.Ring, member: "ringAlpha"),
    };

    public static TheoryData<string> Bindable => [.. Cases.Where(predicate: static pair => (pair.Value.Present is not null)).Select(selector: static pair => Key(field: pair.Key))];

    // Rows for plain numbers a creation authors, judged where the creation is canonicalized, not in a world section.
    private static readonly IReadOnlySet<WorldValueField> LiteralOnly = new HashSet<WorldValueField> { WorldValueFields.VolumeSoftness };

    private const float HighFov = 1.5f;
    private const float LowFov = 0.5f;

    // The fields whose values must stay ordered against a neighbour: a gradient's stops and the curvature ink band.
    public static TheoryData<string> Coupled => [
        Key(field: WorldValueFields.StopElevation),
        Key(field: WorldValueFields.CurvatureInkLow),
        Key(field: WorldValueFields.CurvatureInkHigh),
    ];
    public static TheoryData<string> Restricted => [.. WorldValueFields.All.Where(predicate: static candidate => (candidate.Domain.IsRestricted && !LiteralOnly.Contains(item: candidate))).Select(selector: static candidate => Key(field: candidate))];
    public static TheoryData<string> Shapes => [
        "closed:[0, 1]",
        "closed:[-1, 1]",
        "half-open:[0, inf)",
        "open-lower:(0, inf)",
        "open-lower:(0, 1]",
        .. WorldValueFields.All.Where(predicate: static candidate => candidate.Domain.IsRestricted).Select(selector: static candidate => $"field:{Key(field: candidate)}"),
    ];

    private static WorldCamera Camera(string name, WorldCameraProgram rig) => new(
        Anchor: null,
        Name: name,
        RenderHeight: 240u,
        RenderWidth: 320u,
        Rig: rig
    );
    private static WorldCameraProgram Program(string name, WorldCameraProgramOp operations) => new(
        Name: name,
        Operations: [operations],
        Version: WorldCameraProgram.CurrentVersion
    );
    private static string Key(WorldValueField field) => $"{field.Owner.Name}.{field.Member}";
    private static WorldValueField FieldOf(string key) => WorldValueFields.All.Single(predicate: field => (Key(field: field) == key));

    internal static WorldDefinition Render(WorldRenderLighting? lighting = null, WorldRenderSky? sky = null) => Fixtures.BuildDocument() with {
        RenderRaw = WorldRenderDefaults.Absent with { Lighting = lighting, Sky = sky },
    };
    internal static WorldDefinition Layer(WorldRenderSkyLayer layer) => Render(sky: new WorldRenderSky(Layers: [layer]));

    private static WorldResolvedEnvironment Environment(WorldDefinition definition, WorldStateMirror mirror, WorldValueDomainGuard domains) => new WorldEnvironmentResolve(domains: domains).Resolve(
        definition: definition,
        mirror: mirror,
        revision: 0
    );
    private static Case Env(Func<BindableScalar, WorldRenderEnvironment> author, Func<WorldResolvedEnvironment, float> present, string member) => new(
        Author: s => Fixtures.BuildDocument() with { RenderRaw = WorldRenderDefaults.Absent with { Environment = author(s) } },
        Path: $"render.environment.{member}",
        Present: (definition, mirror, domains) => present(Environment(definition: definition, domains: domains, mirror: mirror))
    );
    private static Case Lit(Func<BindableScalar, WorldRenderLight> author, Func<WorldResolvedEnvironment, float> present, string member) => new(
        Author: s => Render(lighting: new WorldRenderLighting(Lights: [author(arg: s)])),
        Path: $"render.lighting.lights[0].{member}",
        Present: (definition, mirror, domains) => present(arg: Environment(definition: definition, domains: domains, mirror: mirror))
    );
    private static Case Curve(Func<BindableScalar, WorldRenderCurvature> author, Func<WorldResolvedEnvironment, float>? present, string member, float[]? controls = null) => new(
        Author: s => Render(lighting: new WorldRenderLighting(Curvature: author(arg: s))),
        Controls: controls,
        Path: $"render.lighting.curvature.{member}",
        Present: ((present is null)
            ? null
            : (definition, mirror, domains) => present(arg: Environment(definition: definition, domains: domains, mirror: mirror)))
    );
    private static Case Sky(Func<BindableScalar, WorldRenderSkyLayer> author, Func<WorldResolvedEnvironment, float>? present, string member) => new(
        Author: s => Layer(layer: author(arg: s)),
        Path: $"render.sky.layers[0].{member}",
        Present: ((present is null)
            ? null
            : (definition, mirror, domains) => present(arg: Environment(definition: definition, domains: domains, mirror: mirror)))
    );
    private static Case Air(Func<BindableScalar, WorldRenderAtmosphere> author, Func<WorldResolvedEnvironment, float>? present, string member) => new(
        Author: s => Fixtures.BuildDocument() with { RenderRaw = WorldRenderDefaults.Absent with { Atmosphere = author(arg: s) } },
        Path: $"render.atmosphere.{member}",
        Present: ((present is null)
            ? null
            : (definition, mirror, domains) => present(arg: Environment(definition: definition, domains: domains, mirror: mirror)))
    );
    private static Case Bloom(string field, Func<WorldThemeElevation, BindableScalar, WorldThemeElevation> author, Func<OverlayThemeValues.ElevationSet, float> present) => new(
        Author: s => Fixtures.BuildDocument() with {
            ThemeRaw = WorldThemeValidationLawTests.MinimalTheme() with { Elevation = author(arg1: WorldThemeValidationLawTests.MinimalElevation(), arg2: s) },
        },
        Path: $"theme.elevation.{char.ToLowerInvariant(c: field[0])}{field[1..]}",
        Present: (definition, mirror, domains) => present(arg: new WorldThemeResolve(domains: domains).Resolve(definition: definition, mirror: mirror, revision: 1).Elevation)
    );
    private static Case Marker(Func<BindableScalar, WorldMarkerStyle> author, Func<WorldMarkerAlphas, float> present, string member) => new(
        Author: s => Fixtures.BuildDocument() with {
            IconsRaw = new WorldIconographySection(IconsRaw: [
                new WorldIconRow(
                    Name: "marker.dot",
                    Glyph: new WorldIconGlyphRef(Font: "jetbrains-mono-regular", Glyph: "U+25A0")
                ),
            ]),
            MarkersRaw = [
                new WorldMarkerRow(
                    Id: "speakers",
                    Source: new WorldMarkerSource.Speakers(),
                    Icon: "marker.dot",
                    Ring: new WorldMarkerRing(Field: WorldMarkerRing.SpeakerRadiusField),
                    Style: author(arg: s)
                ),
            ],
        },
        Path: $"markers[0].style.{member}",
        Present: (definition, mirror, domains) => present(arg: WorldMarkerAlphas.Resolve(domains: domains, index: 0, marker: definition.Markers[0], mirror: mirror))
    );

    // The document with the bound row holding a value: the state a load reads, or a live write leaves.
    internal static WorldDefinition WithRow(WorldDefinition definition, double value, string row = Row) => definition.WithWorldState(rows: [
        .. definition.State.Where(predicate: candidate => !string.Equals(a: candidate.Name.Value, b: row, comparisonType: StringComparison.Ordinal)),
        new WorldStateRow(
            Name: CellName.Parse(candidate: row),
            Kind: CellKind.Fixed,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value))]
        ),
    ]);

    // The number a presentation reads the bound row as.
    private static float Presented(double value) {
        Assert.True(condition: WorldStateReader.TryNumber(number: out var number, value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value)));

        return ((float)number);
    }
    // A value well inside the domain, which a Fixed row holds without leaving it.
    private static float Inside(WorldValueDomain domain) => ((domain.Highest < float.MaxValue)
        ? ((domain.Lowest + domain.Highest) / 2f)
        : (domain.Lowest + 0.5f));
    private static bool Admits(WorldDefinition definition, out string reason) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out reason
    );

    [Fact]
    public void Every_bindable_scalar_of_the_model_declares_exactly_one_domain() {
        static Type Underlying(Type type) => (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        var model = WorldModelShape.Types
            .SelectMany(selector: static shape => shape.Members.Concat(second: shape.Properties))
            // An angle's own scalar form is the angle's representation, not a field.
            .Where(predicate: static member => ((member.DeclaringType != typeof(BindableAngle)) && ((Underlying(type: member.Type) == typeof(BindableScalar)) || (Underlying(type: member.Type) == typeof(BindableAngle)))))
            .Select(selector: static member => (member.DeclaringType, member.Member))
            .ToHashSet();
        var declared = WorldValueFields.All.Select(selector: static field => (field.Owner, field.Member)).ToList();
        var properties = typeof(WorldValueFields)
            .GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Static)
            .Where(predicate: static property => (property.PropertyType == typeof(WorldValueField)))
            .Select(selector: static property => ((WorldValueField)property.GetValue(obj: null)!))
            .ToHashSet(comparer: ReferenceEqualityComparer.Instance);

        // One row per member: no member declares two domains, every declared row is in the table the validator and the
        // presentation look fields up in, and the model's every bindable scalar member has its row.
        Assert.Equal(expected: declared.Count, actual: declared.Distinct().Count());
        Assert.Equal(expected: properties.Count, actual: WorldValueFields.All.Count);
        Assert.All(collection: WorldValueFields.All, action: field => Assert.Contains(collection: properties, expected: field));
        Assert.Equal(
            expected: model.OrderBy(keySelector: static pair => $"{pair.DeclaringType.FullName}.{pair.Member}"),
            actual: declared.Where(predicate: static pair => !LiteralOnly.Any(predicate: field => ((field.Owner == pair.Owner) && (field.Member == pair.Member)))).OrderBy(keySelector: static pair => $"{pair.Owner.FullName}.{pair.Member}")
        );
        // A literal-only row names a plain number, which no binding can reach.
        Assert.All(collection: LiteralOnly, action: static field => Assert.Equal(expected: typeof(float?), actual: field.Owner.GetProperty(name: field.Member)!.PropertyType));
        Assert.All(collection: WorldValueFields.All, action: static field => Assert.Same(expected: field, actual: WorldValueFields.Of(member: field.Member, owner: field.Owner)));

        // Every restricted field is authored by this suite, which is how the laws below reach the validator and the
        // presentation for each.
        Assert.Equal(
            expected: WorldValueFields.All.Where(predicate: static field => (field.Domain.IsRestricted && !LiteralOnly.Contains(item: field))).Select(selector: Key).Order(),
            actual: Cases.Keys.Select(selector: Key).Order()
        );
    }
    [MemberData(nameof(Shapes))]
    [Theory]
    public void Map_clamps_a_finite_number_to_a_closed_end_and_holds_one_that_has_no_nearest_number(string shape) {
        var domain = shape switch {
            "closed:[0, 1]" => WorldValueDomain.Unit,
            "closed:[-1, 1]" => new WorldValueDomain(Maximum: 1f, Minimum: -1f),
            "half-open:[0, inf)" => WorldValueDomain.NonNegative,
            "open-lower:(0, inf)" => WorldValueDomain.Positive,
            "open-lower:(0, 1]" => new WorldValueDomain(Maximum: 1f, Minimum: 0f, MinimumOpen: true),
            _ => FieldOf(key: shape["field:".Length..]).Domain,
        };

        static float Int(long raw) {
            Assert.True(condition: WorldStateReader.TryNumber(number: out var number, value: CellValue.Int(value: raw)));

            return ((float)number);
        }
        static float Fixed(long raw) {
            Assert.True(condition: WorldStateReader.TryNumber(number: out var number, value: CellValue.Fixed(rawBits: raw)));

            return ((float)number);
        }
        float[] values = [
            float.NaN, float.NegativeInfinity, float.MinValue, -1f, -float.Epsilon, -0f, 0f, float.Epsilon, 0.5f, 1f, 2f, float.MaxValue, float.PositiveInfinity,
            domain.Minimum, domain.Maximum, domain.Lowest, domain.Highest, MathF.BitDecrement(x: domain.Lowest), MathF.BitIncrement(x: domain.Highest),
            Int(raw: long.MinValue), Int(raw: long.MaxValue), Int(raw: int.MinValue), Int(raw: int.MaxValue), Int(raw: -1L), Int(raw: 0L), Int(raw: 1L),
            Fixed(raw: long.MinValue), Fixed(raw: long.MaxValue), Fixed(raw: -1L), Fixed(raw: 0L), Fixed(raw: 1L),
        ];

        Assert.True(condition: domain.IsRestricted);
        Assert.True(condition: domain.Contains(value: domain.Lowest));
        Assert.True(condition: domain.Contains(value: domain.Highest));
        Assert.False(condition: domain.Contains(value: MathF.BitDecrement(x: domain.Lowest)));

        foreach (var value in values) {
            // The rule, stated apart from the domain's own members: a number that is not finite, or at or beyond an open
            // end, has no admissible nearest number, so it needs a hold; any other number is clamped to the closed ends.
            var needsHold = (
                !float.IsFinite(f: value) ||
                (domain.MinimumOpen && (value <= domain.Minimum)) ||
                (domain.MaximumOpen && (value >= domain.Maximum))
            );

            var mapped = domain.Map(value: value);

            Assert.Equal(expected: needsHold, actual: mapped.Holds);

            if (needsHold) {
                // A hold is an outcome, never a thrown error: the frame path has no exception to meet.
                Assert.Equal(expected: WorldValueMapping.Hold, actual: mapped);

                continue;
            }

            var clamped = mapped.Value;

            Assert.True(condition: domain.Contains(value: clamped), userMessage: $"{shape}: {value} clamped to {clamped}, outside {domain}");
            Assert.Equal(
                expected: Math.Clamp(max: domain.Maximum, min: domain.Minimum, value: value),
                actual: clamped
            );

            if (domain.Contains(value: value)) {
                Assert.Equal(actual: clamped, expected: value);
            }

            // Pure: the same value always maps to the same outcome.
            Assert.Equal(expected: mapped, actual: domain.Map(value: value));
        }
    }
    [MemberData(nameof(Restricted))]
    [Theory]
    public void The_validator_judges_each_restricted_field_by_its_declared_domain(string key) {
        var field = FieldOf(key: key);
        var domain = field.Domain;
        var authoring = Cases[field];
        var controls = (authoring.Controls ?? ((domain.Highest < float.MaxValue)
            ? [domain.Lowest, domain.Highest]
            : [domain.Lowest]));

        foreach (var control in controls) {
            Assert.True(condition: Admits(definition: authoring.Author(arg: control), reason: out var reason), userMessage: $"{authoring.Path} {control}: {reason}");
        }

        Laws.Refuses(
            definition: authoring.Author(arg: MathF.BitDecrement(x: domain.Lowest)),
            needle: $"{authoring.Path} {MathF.BitDecrement(x: domain.Lowest)} must be finite and within {domain}"
        );

        if (domain.Highest < float.MaxValue) {
            Laws.Refuses(
                definition: authoring.Author(arg: MathF.BitIncrement(x: domain.Highest)),
                needle: $"{authoring.Path} {MathF.BitIncrement(x: domain.Highest)} must be finite and within {domain}"
            );
        }

        if (authoring.Present is null) {
            return;
        }

        // A bound field is judged at load by the value its row starts at.
        var bound = new BindableScalar(binding: $"state.{Row}");

        Assert.True(condition: Admits(definition: WithRow(definition: authoring.Author(arg: bound), value: Inside(domain: domain)), reason: out var boundReason), userMessage: boundReason);
        Laws.Refuses(
            definition: WithRow(definition: authoring.Author(arg: bound), value: (domain.Lowest - 1d)),
            needle: $"{authoring.Path} binds state.{Row} whose starting value"
        );
    }
    [MemberData(nameof(Bindable))]
    [Theory]
    public void The_presentation_maps_each_bound_field_by_its_declared_domain(string key) {
        var field = FieldOf(key: key);
        var domain = field.Domain;
        var authoring = Cases[field];
        var bound = authoring.Author(arg: new BindableScalar(binding: $"state.{Row}"));
        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();
        var quarter = ((domain.Highest < float.MaxValue)
            ? (domain.Lowest + ((domain.Highest - domain.Lowest) / 4f))
            : (domain.Lowest + 0.25f));
        var written = new List<double> { Inside(domain: domain), (domain.Lowest - 1d), domain.Minimum, quarter };

        domains.Report = reports.Add;

        if (domain.Highest < float.MaxValue) {
            written.Add(item: (domain.Highest + 1d));
            written.Add(item: domain.Maximum);
        }

        written.Add(item: Inside(domain: domain));

        // The first value is the load's: valid, and the value a hold falls back to.
        var world = new LiveWorld(definition: WithRow(definition: bound, value: written[0]));
        var last = Presented(value: written[0]);
        var outside = false;
        var transitions = 0;

        Assert.Equal(expected: last, actual: authoring.Present!(arg1: world.Current, arg2: world.Mirror, arg3: domains), tolerance: 1e-6f);

        foreach (var value in written.Skip(count: 1)) {
            world.Set(definition: WithRow(definition: bound, value: value));

            var raw = Presented(value: value);
            // The rule, stated apart from the domain's own members: hold the last valid value for a number at or beyond
            // an open end, clamp any other to the closed ends.
            var holds = (
                (domain.MinimumOpen && (raw <= domain.Minimum)) ||
                (domain.MaximumOpen && (raw >= domain.Maximum))
            );
            var expected = (holds
                ? last
                : Math.Clamp(max: domain.Maximum, min: domain.Minimum, value: raw));
            var presented = authoring.Present!(arg1: world.Current, arg2: world.Mirror, arg3: domains);

            Assert.True(condition: domain.Contains(value: presented), userMessage: $"{authoring.Path} written {value} presented {presented}, outside {domain}");
            Assert.Equal(actual: presented, expected: expected, tolerance: 1e-6f);

            if (!holds) {
                last = expected;
            }

            if (domain.Contains(value: raw) == outside) {
                outside = !outside;
                transitions++;
            }
        }

        // One report when the binding leaves its domain and one when it returns, however often or far its row strays.
        Assert.True(condition: (transitions >= 2), userMessage: "the sequence must leave the domain and return");
        Assert.Equal(expected: transitions, actual: domains.Reported);
        Assert.Equal(expected: transitions, actual: reports.Count);
        Assert.Contains(expectedSubstring: $"{authoring.Path} reads", actualString: reports[0]);
        Assert.Contains(expectedSubstring: $"state.{Row}", actualString: reports[0]);
        Assert.Contains(expectedSubstring: "outside", actualString: reports[0]);
        Assert.Contains(expectedSubstring: "recovered", actualString: reports[1]);
    }
    [Fact]
    public void A_bound_row_starting_outside_its_fields_domain_is_refused_at_load_naming_the_field() {
        var softness = Layer(layer: new WorldRenderSkyLayer.Clouds(Softness: new BindableScalar(binding: $"state.{Row}")));

        Laws.Refuses(
            definition: WithRow(definition: softness, value: 0d),
            needle: $"render.sky.layers[0].softness binds state.{Row} whose starting value 0 lies outside {WorldValueFields.CloudSoftness.Domain}"
        );
        Assert.True(condition: Admits(definition: WithRow(definition: softness, value: 0.5d), reason: out var reason), userMessage: reason);

        // Only a load reads the row: revalidating a live document, whose rows hold whatever its rules last wrote,
        // admits the same document, so a stray value never refuses an unrelated mutation.
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: WithRow(definition: softness, value: 0d), reason: out var local),
            userMessage: local
        );
    }
    [Fact]
    public void A_cloud_softness_below_its_floor_is_refused_at_load_and_presents_the_floor() {
        // The floor is a closed end the kernels' smoothstep bands are proved against, not an open zero.
        Assert.Equal(expected: new WorldValueDomain(Maximum: 1f, Minimum: SdfSky.MinCloudSoftness), actual: WorldValueFields.CloudSoftness.Domain);

        var literal = Layer(layer: new WorldRenderSkyLayer.Clouds(Softness: 1e-30f));

        Laws.Refuses(
            definition: literal,
            needle: $"render.sky.layers[0].softness {1e-30f} must be finite and within {WorldValueFields.CloudSoftness.Domain}"
        );
        Assert.True(condition: Admits(definition: Layer(layer: new WorldRenderSkyLayer.Clouds(Softness: SdfSky.MinCloudSoftness)), reason: out var reason), userMessage: reason);

        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();
        var resolve = new WorldEnvironmentResolve(domains: domains);

        domains.Report = reports.Add;

        // An unvalidated literal below the floor still presents the floor; a literal is no binding and reports nothing.
        Assert.Equal(
            expected: SdfSky.MinCloudSoftness,
            actual: resolve.Resolve(definition: literal, mirror: ClientFixtures.StateMirror(definition: literal), revision: 0).Sky.First<SdfSkyClouds>().Softness
        );
        Assert.Empty(collection: reports);

        // A bound row written to 0 or below presents the floor and reports once.
        var bound = Layer(layer: new WorldRenderSkyLayer.Clouds(Softness: new BindableScalar(binding: $"state.{Row}")));

        Assert.True(condition: Admits(definition: WithRow(definition: bound, value: 0.5d), reason: out var boundReason), userMessage: boundReason);

        var world = new LiveWorld(definition: WithRow(definition: bound, value: 0.5d));

        foreach (var value in new[] { 0d, -1d }) {
            world.Set(definition: WithRow(definition: bound, value: value));

            Assert.Equal(
                expected: SdfSky.MinCloudSoftness,
                actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 0).Sky.First<SdfSkyClouds>().Softness
            );
        }

        Assert.Single(collection: reports);
        Assert.Contains(expectedSubstring: $"render.sky.layers[0].softness reads 0 from state.{Row}, outside {WorldValueFields.CloudSoftness.Domain}", actualString: reports[0]);
    }
    [Fact]
    public void A_cloud_volume_softness_below_its_floor_is_refused_where_its_creation_is_canonicalized() {
        static IReadOnlyList<DocumentValidationError> Errors(float softness) {
            var document = CreationFixtures.Document(name: "cloud", shapes: [CreationFixtures.UnitSphereShape]) with {
                Volumes = [
                    new VolumeDocument(
                        Kind: VolumeDocument.CloudKind,
                        Position: Vector3.Zero,
                        Rotation: Quaternion.Identity,
                        HalfExtent: Vector3.One,
                        Ramp: [new VolumeDensityStopDocument(Color: "#FFFFFF", Density: 0f), new VolumeDensityStopDocument(Color: "#FFFFFF", Density: 1f)],
                        Softness: softness
                    ),
                ],
            };

            try {
                _ = CreationCanonicalizer.Canonicalize(document: document);

                return [];
            } catch (DocumentValidationException exception) {
                return exception.Errors;
            }
        }

        var domain = WorldValueFields.VolumeSoftness.Domain;

        Assert.Same(expected: WorldValueFields.Of(member: nameof(VolumeDocument.Softness), owner: typeof(VolumeDocument)), actual: WorldValueFields.VolumeSoftness);
        Assert.Equal(expected: VolumeDocument.SoftnessDomain, actual: domain);
        Assert.Empty(collection: Errors(softness: domain.Lowest));
        Assert.Empty(collection: Errors(softness: domain.Highest));

        foreach (var softness in new[] { 0f, 1e-30f, MathF.BitDecrement(x: domain.Lowest), MathF.BitIncrement(x: domain.Highest) }) {
            Assert.Contains(
                collection: Errors(softness: softness),
                filter: error => ((error.Path == "volumes[0].softness") && error.Message.Contains(comparisonType: StringComparison.Ordinal, value: $"within {domain}"))
            );
        }
    }

    // A field read directly: the world is named by its mirror, the binding by its row, the site by its section.
    private static float Resolve(WorldValueDomainGuard guard, WorldStateMirror mirror, WorldValueField field, float value, float fallback = 0.25f, string row = Row) => guard.Resolve(
        fallback: fallback,
        field: field,
        mirror: mirror,
        scalar: new BindableScalar(binding: $"state.{row}"),
        site: new WorldValueSite(Section: "probe"),
        value: value
    );
    private static WorldDefinition CloudSoftnessBoundTo(string row, double value) => WithRow(
        definition: Layer(layer: new WorldRenderSkyLayer.Clouds(Softness: new BindableScalar(binding: $"state.{row}"))),
        row: row,
        value: value
    );

    [Fact]
    public void A_value_that_cannot_be_clamped_holds_the_last_valid_value_and_reports_each_transition() {
        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();
        var mirror = new LiveWorld(definition: WithRow(definition: Fixtures.BuildDocument(), value: 0.5d)).Mirror;
        var coverage = WorldValueFields.CloudCoverage;

        domains.Report = reports.Add;

        // Not a number and either infinity have no admissible nearest number: the binding keeps what it last presented
        // from a valid input, and none of the three is mapped to an end of the domain.
        (float Written, float Presented, int Reports)[] steps = [
            (0.5f, 0.5f, 0),
            (float.NaN, 0.5f, 1),
            (float.PositiveInfinity, 0.5f, 1),
            (float.NegativeInfinity, 0.5f, 1),
            (0.7f, 0.7f, 2),
            // A finite value beyond a closed end clamps to it, and is reported only on leaving the domain.
            (3f, 1f, 3),
            (-4f, 0f, 3),
            (0.2f, 0.2f, 4),
        ];

        foreach (var (written, presented, count) in steps) {
            Assert.Equal(expected: presented, actual: Resolve(field: coverage, guard: domains, mirror: mirror, value: written));
            Assert.Equal(expected: count, actual: reports.Count);
            Assert.Equal(expected: count, actual: domains.Reported);
        }

        Assert.Equal(expected: 3L, actual: domains.Holds);
        Assert.Contains(expectedSubstring: "reads NaN from state.bound", actualString: reports[0]);
        Assert.Contains(expectedSubstring: "holding 0.5", actualString: reports[0]);
        Assert.Contains(expectedSubstring: "recovered", actualString: reports[1]);
        Assert.Contains(expectedSubstring: "presenting 1", actualString: reports[2]);
        Assert.Contains(expectedSubstring: "recovered", actualString: reports[3]);

        // A value at an open end is not clamped to the nearest number either: a sun disc of radius zero holds.
        var radius = WorldValueFields.SunDiscRadius;

        Assert.Equal(expected: 0.5f, actual: Resolve(field: radius, guard: domains, mirror: mirror, row: "radius", value: 0.5f));
        Assert.Equal(expected: 0.5f, actual: Resolve(field: radius, guard: domains, mirror: mirror, row: "radius", value: 0f));
        Assert.Equal(expected: radius.Domain.Maximum, actual: Resolve(field: radius, guard: domains, mirror: mirror, row: "radius", value: 2f));

        // Before a binding has presented a valid value, the field's fallback stands in for it.
        var fresh = new LiveWorld(definition: WithRow(definition: Fixtures.BuildDocument(), value: 0.5d)).Mirror;

        Assert.Equal(expected: 0.25f, actual: Resolve(field: coverage, guard: domains, mirror: fresh, value: float.NaN));
    }
    [Fact]
    public void An_unchanged_input_does_no_counted_work() {
        var domains = new WorldValueDomainGuard();
        var mirror = new LiveWorld(definition: WithRow(definition: Fixtures.BuildDocument(), value: 0.5d)).Mirror;

        for (var frame = 0; (frame < 100); frame++) {
            Assert.Equal(expected: 0.5f, actual: Resolve(field: WorldValueFields.CloudCoverage, guard: domains, mirror: mirror, value: 0.5f));
        }

        Assert.Equal(expected: 1L, actual: domains.Checks);

        // A stray value that stays put is not checked again either; only a changed input is.
        for (var frame = 0; (frame < 100); frame++) {
            Assert.Equal(expected: 0.5f, actual: Resolve(field: WorldValueFields.CloudCoverage, guard: domains, mirror: mirror, value: float.NaN));
        }

        Assert.Equal(expected: 2L, actual: domains.Checks);

        // Through the environment resolve, a bound softness that no write moves is checked once however many frames.
        var resolve = new WorldEnvironmentResolve(domains: domains);
        var world = new LiveWorld(definition: CloudSoftnessBoundTo(row: Row, value: 0.5d));
        var before = domains.Checks;

        for (var frame = 0; (frame < 50); frame++) {
            Assert.Equal(expected: 0.5f, actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: frame).Sky.First<SdfSkyClouds>().Softness);
        }

        Assert.Equal(expected: (before + 1L), actual: domains.Checks);
    }
    [MemberData(nameof(Coupled))]
    [Theory]
    public void A_coupled_threshold_cannot_bind_a_row_so_no_end_is_ever_clamped_alone(string key) {
        var field = FieldOf(key: key);
        var authoring = Cases[field];
        var control = ((field == WorldValueFields.CurvatureInkHigh)
            ? SdfCurvature.DefaultInkHigh
            : ((field == WorldValueFields.CurvatureInkLow)
                ? 0f
                : -0.5f));

        // The ends of an ordered pair are judged together, as literals and on one clock; a row's value cannot promise
        // the order, and a presentation clamping one end at a time could cross them, so neither end binds.
        Assert.True(condition: Admits(definition: authoring.Author(arg: new BindableScalar(literal: control)), reason: out var reason), userMessage: reason);
        Laws.Refuses(
            definition: WithRow(definition: authoring.Author(arg: new BindableScalar(binding: $"state.{Row}")), value: control),
            needle: "may not bind a state row"
        );
    }
    [Fact]
    public void Worlds_binding_the_same_field_to_rows_of_one_name_report_and_hold_each_for_itself() {
        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();
        var resolve = new WorldEnvironmentResolve(domains: domains);
        var first = new LiveWorld(definition: CloudSoftnessBoundTo(row: "cloudSoft", value: 0.5d));
        var second = new LiveWorld(definition: CloudSoftnessBoundTo(row: "cloudSoft", value: 0.5d));

        domains.Report = reports.Add;

        foreach (var world in new[] { first, second }) {
            Assert.Equal(expected: 0.5f, actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 0).Sky.First<SdfSkyClouds>().Softness);
        }

        foreach (var world in new[] { first, second }) {
            world.Set(definition: CloudSoftnessBoundTo(row: "cloudSoft", value: 0d));
            _ = resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 1);
        }

        // Both worlds' bindings left their domain; the second is not taken for the first.
        Assert.Equal(expected: 2, actual: reports.Count);
        Assert.All(collection: reports, action: report => Assert.Contains(actualString: report, expectedSubstring: "render.sky.layers[0].softness reads 0 from state.cloudSoft"));

        // Each world holds the last valid value of its own binding.
        var held = new WorldValueDomainGuard();
        var one = new LiveWorld(definition: WithRow(definition: Fixtures.BuildDocument(), value: 0.5d)).Mirror;
        var other = new LiveWorld(definition: WithRow(definition: Fixtures.BuildDocument(), value: 0.5d)).Mirror;

        _ = Resolve(field: WorldValueFields.CloudCoverage, guard: held, mirror: one, value: 0.2f);
        _ = Resolve(field: WorldValueFields.CloudCoverage, guard: held, mirror: other, value: 0.9f);

        Assert.Equal(expected: 0.2f, actual: Resolve(field: WorldValueFields.CloudCoverage, guard: held, mirror: one, value: float.NaN));
        Assert.Equal(expected: 0.9f, actual: Resolve(field: WorldValueFields.CloudCoverage, guard: held, mirror: other, value: float.NaN));
        Assert.Equal(expected: 2, actual: held.Tracked);
    }
    [Fact]
    public void A_binding_replaced_by_another_releases_what_the_guard_kept_for_it() {
        var domains = new WorldValueDomainGuard();
        var resolve = new WorldEnvironmentResolve(domains: domains);
        var world = new LiveWorld(definition: CloudSoftnessBoundTo(row: "cloud0", value: 0.5d));

        domains.Report = static _ => { };

        for (var generation = 0; (generation < 40); generation++) {
            var row = $"cloud{generation}";

            // The binding names a row of its own that starts valid, then strays; the document then drops the row.
            world.Set(definition: CloudSoftnessBoundTo(row: row, value: 0.5d));
            _ = resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: (generation * 2));
            world.Set(definition: CloudSoftnessBoundTo(row: row, value: 0d));
            _ = resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: ((generation * 2) + 1));

            // The live document binds one row; the guard holds that binding and no retired one.
            Assert.Equal(expected: 1, actual: domains.Tracked);
        }

        Assert.Equal(expected: 40L, actual: domains.Reported);
    }
    [Fact]
    public void A_worlds_state_is_released_with_the_world() {
        var domains = new WorldValueDomainGuard();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference Observe(WorldValueDomainGuard domains) {
            var world = new LiveWorld(definition: WithRow(definition: Fixtures.BuildDocument(), value: 0.5d));

            _ = Resolve(field: WorldValueFields.CloudCoverage, guard: domains, mirror: world.Mirror, value: 0.4f);

            return new WeakReference(target: world.Mirror);
        }

        var gone = Observe(domains: domains);

        for (var attempt = 0; ((attempt < 10) && gone.IsAlive); attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(condition: gone.IsAlive);
        Assert.Equal(expected: 0, actual: domains.Tracked);
    }
    [Fact]
    public void Admission_judges_the_value_a_binding_presents_not_the_row_it_follows() {
        var slow = new DynamicsRow(Damping: 1f, Frequency: 0.25f, Name: "slow", Response: 0f);
        var scrim = Cases[WorldValueFields.ScrimAlpha];
        var floor = WorldThemeCapacity.ScrimMinAlpha;

        // A row eased toward `target` from `start`: its follower reads `start` at tick zero, its stored truth `target`.
        WorldDefinition Eased(string token, double target, double start) => EasedDocument(token: token).WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: Row),
                Kind: CellKind.Fixed,
                Dynamics: new StateDynamics(Row: slow.Name),
                Cells: [
                    new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: target).Value),
                        Clock: new StateCellClock(Y0: FixedQ4816.FromDouble(value: start).Value)
                    ),
                ]
            ),
        ]);

        WorldDefinition EasedDocument(string token) {
            var document = scrim.Author(arg: new BindableScalar(binding: token));

            return (document with { DynamicsRaw = [.. document.Dynamics, slow] });
        }

        var eased = $"state.{Row}";
        var stored = $"state.{Row}.$target";
        var inside = 0.9d;
        var outside = 0d;

        Assert.True(condition: (outside < floor), userMessage: "the control needs a value below the scrim floor");

        // The binding presents the follower: stored 0 below the floor does not refuse a follower that starts at 0.9.
        Assert.True(condition: Admits(definition: Eased(start: inside, target: outside, token: eased), reason: out var easedReason), userMessage: easedReason);
        // The same row read as stored truth presents 0 and is refused.
        Laws.Refuses(
            definition: Eased(start: inside, target: outside, token: stored),
            needle: $"theme.color.scrimPanel.alpha binds {stored} whose starting value {outside} lies outside"
        );
        // Reversed, the follower starts below the floor while the stored truth is inside it.
        Laws.Refuses(
            definition: Eased(start: outside, target: inside, token: eased),
            needle: $"theme.color.scrimPanel.alpha binds {eased} whose starting value {outside} lies outside"
        );
        Assert.True(condition: Admits(definition: Eased(start: outside, target: inside, token: stored), reason: out var storedReason), userMessage: storedReason);
    }
}
