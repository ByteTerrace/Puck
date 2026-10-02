using System.Numerics;
using System.Reflection;
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
/// starting value outside it, naming the field; the presentation maps whatever value a bound row presents into it with
/// <see cref="WorldValueDomain.Clamp"/>, a pure function of the value, and reports the first stray value of each field
/// and row once.
/// </summary>
public sealed class WorldValueDomainLawTests {
    internal const string Row = "bound";

    // How a test authors a field into a document and reads back what the presentation made of it.
    // A report names the field where the presentation finds it, which is the document path unless the case says
    // otherwise (ReportPath).
    private sealed record Case(Func<BindableScalar, WorldDefinition> Author, string Path, Func<WorldDefinition, WorldStateMirror, WorldValueDomainReports, float>? Present = null, float[]? Controls = null, string? ReportPath = null);

    private static readonly IReadOnlyDictionary<WorldValueField, Case> Cases = new Dictionary<WorldValueField, Case> {
        [WorldValueFields.DirectionalWeight] = Lit(author: s => new WorldRenderLight.Directional(Weight: s), present: static e => e.Lights[0].Weight, member: "weight"),
        [WorldValueFields.DirectionalAngularRadius] = Lit(author: s => new WorldRenderLight.Directional(AngularRadius: s), present: static e => MathF.Atan(x: e.Lights[0].Param), member: "angularRadius"),
        [WorldValueFields.HemisphereBase] = Lit(author: s => new WorldRenderLight.Hemisphere(Base: s), present: static e => e.Lights[0].Weight, member: "base"),
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
        [WorldValueFields.FogDensity] = Sky(author: s => new WorldRenderSkyLayer.Fog(Density: s), present: static e => e.Sky.Block.FogDensity, member: "density"),
        [WorldValueFields.SunDiscRadius] = Sky(author: s => new WorldRenderSkyLayer.SunDisc(Radius: s), present: static e => e.Sky.SunDiscRadians, member: "radius"),
        [WorldValueFields.SunDiscIntensity] = Sky(author: s => new WorldRenderSkyLayer.SunDisc(Intensity: s), present: static e => e.Sky.Block.DiscIntensity, member: "intensity"),
        [WorldValueFields.StarBrightness] = Sky(author: s => new WorldRenderSkyLayer.Stars(Brightness: s), present: static e => e.Sky.Block.StarBrightness, member: "brightness"),
        [WorldValueFields.TwinkleShare] = Sky(author: s => new WorldRenderSkyLayer.Stars(Twinkle: new WorldRenderSkyTwinkle(Share: s)), present: static e => e.Sky.Block.TwinkleShare, member: "twinkle.share"),
        [WorldValueFields.TwinkleDepth] = Sky(author: s => new WorldRenderSkyLayer.Stars(Twinkle: new WorldRenderSkyTwinkle(Depth: s)), present: static e => e.Sky.Block.TwinkleDepth, member: "twinkle.depth"),
        // A rate the tick integrates may not bind a state row.
        [WorldValueFields.TwinkleRate] = Sky(author: s => new WorldRenderSkyLayer.Stars(Twinkle: new WorldRenderSkyTwinkle(Rate: s)), present: null, member: "twinkle.rate"),
        [WorldValueFields.CloudCoverage] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Coverage: s), present: static e => e.Sky.Block.CloudCoverage, member: "coverage"),
        [WorldValueFields.CloudSoftness] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Softness: s), present: static e => e.Sky.Block.CloudSoftness, member: "softness"),
        [WorldValueFields.CloudScale] = Sky(author: s => new WorldRenderSkyLayer.Clouds(Scale: s), present: static e => e.Sky.Block.CloudScale, member: "scale"),
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
            // A compiled rig knows its program by name, not where the document declares it.
            ReportPath: "camera program 'probe-rig'.operations[0].weight",
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
            ReportPath: "camera program 'probe-rig'.operations[0].fieldOfViewRadians",
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
    private static WorldDefinition Render(WorldRenderLighting? lighting = null, WorldRenderSky? sky = null) => Fixtures.BuildDocument() with {
        RenderRaw = WorldRenderDefaults.Absent with { Lighting = lighting, Sky = sky },
    };
    private static WorldDefinition Layer(WorldRenderSkyLayer layer) => Render(sky: new WorldRenderSky(Layers: [layer]));
    private static WorldResolvedEnvironment Environment(WorldDefinition definition, WorldStateMirror mirror, WorldValueDomainReports domains) => new WorldEnvironmentResolve(domains: domains).Resolve(
        definition: definition,
        mirror: mirror,
        revision: 0
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
    internal static WorldDefinition WithRow(WorldDefinition definition, double value) => definition.WithWorldState(rows: [
        .. definition.State.Where(predicate: static row => !string.Equals(a: row.Name.Value, b: Row, comparisonType: StringComparison.Ordinal)),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Row),
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
    public void Clamp_lands_inside_its_domain_from_below_at_inside_and_above(string shape) {
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

        if (domain.MinimumOpen) {
            // An open end clamps to the nearest float inside it.
            Assert.Equal(expected: MathF.BitIncrement(x: domain.Minimum), actual: domain.Clamp(value: domain.Minimum));
        }

        foreach (var value in values) {
            var clamped = domain.Clamp(value: value);

            Assert.True(condition: domain.Contains(value: clamped), userMessage: $"{shape}: {value} clamped to {clamped}, outside {domain}");

            if (domain.Contains(value: value)) {
                Assert.Equal(actual: clamped, expected: value);
            } else if ((value < domain.Lowest) || float.IsNaN(f: value)) {
                Assert.Equal(expected: domain.Lowest, actual: clamped);
            } else {
                Assert.Equal(expected: domain.Highest, actual: clamped);
            }

            // Pure: the same value always maps to the same value.
            Assert.Equal(expected: clamped, actual: domain.Clamp(value: value));
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
    public void The_presentation_clamps_each_bound_field_into_its_declared_domain(string key) {
        var field = FieldOf(key: key);
        var domain = field.Domain;
        var authoring = Cases[field];
        var bound = authoring.Author(arg: new BindableScalar(binding: $"state.{Row}"));
        var domains = new WorldValueDomainReports();
        var reports = new List<string>();
        var written = new List<double> { (domain.Lowest - 1d), domain.Minimum, Inside(domain: domain) };

        domains.Report = reports.Add;

        if (domain.Highest < float.MaxValue) {
            written.Add(item: (domain.Highest + 1d));
        }

        foreach (var value in written) {
            var live = WithRow(definition: bound, value: value);
            var presented = authoring.Present!(arg1: live, arg2: ClientFixtures.StateMirror(definition: live), arg3: domains);

            Assert.True(condition: domain.Contains(value: presented), userMessage: $"{authoring.Path} written {value} presented {presented}, outside {domain}");
            Assert.Equal(expected: domain.Clamp(value: Presented(value: value)), actual: presented, tolerance: 1e-6f);
        }

        // One field at one site bound to one row reports once, however often its row strays.
        Assert.Equal(expected: 1, actual: domains.Reported);
        Assert.Single(collection: reports);
        Assert.Contains(expectedSubstring: $"{(authoring.ReportPath ?? authoring.Path)} reads", actualString: reports[0]);
        Assert.Contains(expectedSubstring: $"state.{Row}", actualString: reports[0]);
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

        var domains = new WorldValueDomainReports();
        var reports = new List<string>();
        var resolve = new WorldEnvironmentResolve(domains: domains);

        domains.Report = reports.Add;

        // An unvalidated literal below the floor still presents the floor; a literal reports nothing.
        Assert.Equal(
            expected: SdfSky.MinCloudSoftness,
            actual: resolve.Resolve(definition: literal, mirror: ClientFixtures.StateMirror(definition: literal), revision: 0).Sky.Block.CloudSoftness
        );
        Assert.Empty(collection: reports);

        // A bound row written to 0 or below presents the floor and reports once.
        var bound = Layer(layer: new WorldRenderSkyLayer.Clouds(Softness: new BindableScalar(binding: $"state.{Row}")));

        Assert.True(condition: Admits(definition: WithRow(definition: bound, value: 0.5d), reason: out var boundReason), userMessage: boundReason);

        foreach (var value in new[] { 0d, -1d }) {
            var live = WithRow(definition: bound, value: value);

            Assert.Equal(
                expected: SdfSky.MinCloudSoftness,
                actual: resolve.Resolve(definition: live, mirror: ClientFixtures.StateMirror(definition: live), revision: 0).Sky.Block.CloudSoftness
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
}
