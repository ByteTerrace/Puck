using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a value keyed on a clock (<see cref="WorldKeyResolver"/>). Keys blend by the field's type: a
/// colour in linear light, an angle along the shorter arc across a whole turn, a direction along the great circle, a
/// scalar linearly; each key's ease shapes the time to the next key, and the last key wraps into the first. Structure is
/// never keyed: a key stating a count, a seed, a name, a kind or a slot is refused by name, as are keys on an
/// undeclared clock and keys out of order.
/// </summary>
public sealed class KeyedValueLawTests {
    private static WorldKeyTrack<T> Track<T>(params (double At, T Value, WorldEase Ease)[] keys) => new(
        clock: "day",
        keys: [.. keys.Select(selector: static key => new WorldKey<T>(At: key.At, Ease: key.Ease, Value: key.Value))]
    );
    private static WorldDefinition Definition(WorldRenderSky? sky = null, WorldRenderLighting? lighting = null) => new(
        RenderRaw: new WorldRenderDefaults(Lighting: lighting, Sky: sky),
        StateRaw: new WorldStateSection(World: [new WorldStateRow(
            Name: CellName.Parse(candidate: "tide"),
            Kind: CellKind.Fixed,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: 0L))]
        )]),
        TimelineRaw: new WorldTimelineSection(Clocks: [
            new WorldClock(Name: "day", PeriodSeconds: 60d, SpanSeconds: 24d),
            new WorldClock(Name: "tide", State: "tide"),
        ])
    );
    private static string Validate(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out var reason
    )
        ? string.Empty
        : reason
    );
    private static WorldRenderSky KeyedSky(WorldRenderSkyLayer key) => new(
        Clock: "day",
        Keys: [
            new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["night"] = key }),
            new WorldRenderSkyKey(At: 12d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["night"] = new WorldRenderSkyLayer.Stars(Brightness: 0.5f) }),
        ],
        Layers: [new WorldRenderSkyLayer.Stars(Brightness: 1f, Name: "night", Seed: 3u)]
    );

    [Fact]
    public void A_colour_blends_in_linear_light() {
        var track = Track(
            (0d, new BindableColor(Raw: "#000000"), WorldEase.Linear),
            (12d, new BindableColor(Raw: "#FFFFFF"), WorldEase.Linear)
        );
        var middle = WorldKeyResolver.Color(
            fallback: Vector4.Zero,
            phase: 0.25d,
            span: 24d,
            track: track
        );
        var linearHalf = ((float)ImageSourceConversion.LinearToSrgb(value: 0.5d));

        // Halfway in light is half the light of white, which encodes as sRGB 0.735, not as the code's halfway value.
        Assert.Equal(actual: middle.X, expected: linearHalf, precision: 5);
        Assert.Equal(actual: middle.Z, expected: linearHalf, precision: 5);
        Assert.Equal(actual: middle.W, expected: 1f, precision: 5);
        // Red leg: a blend of the encoded codes would read 0.5.
        Assert.NotEqual(actual: middle.X, expected: 0.5f, precision: 2);
        // A key reads exactly its own colour.
        Assert.Equal(expected: 1f, actual: WorldKeyResolver.Color(fallback: Vector4.Zero, phase: 0.5d, span: 24d, track: track).Y, precision: 6);
    }
    [Fact]
    public void An_angle_blends_along_the_shorter_arc_across_a_whole_turn() {
        var degree = (MathF.PI / 180f);
        var track = Track(
            (0d, (350f * degree), WorldEase.Linear),
            (12d, (10f * degree), WorldEase.Linear)
        );
        var middle = WorldKeyResolver.Angle(
            phase: 0.25d,
            span: 24d,
            track: track
        );

        // From 350° to 10° passes 0°: halfway reads a whole turn, which is 0°, never 180°.
        Assert.Equal(expected: 0d, actual: Math.IEEERemainder(x: middle, y: Math.Tau), precision: 4);
        // Red leg: the scalar blend of the same keys passes 180°.
        Assert.Equal(expected: (180f * degree), actual: WorldKeyResolver.Scalar(phase: 0.25d, span: 24d, track: track), precision: 4);
    }
    [Fact]
    public void A_direction_blends_along_the_great_circle() {
        var track = Track(
            (0d, Vector3.UnitX, WorldEase.Linear),
            (12d, (Vector3.UnitY * 3f), WorldEase.Linear)
        );
        var middle = WorldKeyResolver.Direction(
            phase: 0.25d,
            span: 24d,
            track: track
        );

        // Halfway between east and straight up is the arc's midpoint, of unit length whatever the keys' lengths.
        Assert.Equal(expected: MathF.Sqrt(x: 0.5f), actual: middle.X, precision: 5);
        Assert.Equal(expected: MathF.Sqrt(x: 0.5f), actual: middle.Y, precision: 5);
        Assert.Equal(expected: 1f, actual: middle.Length(), precision: 5);
        // A quarter of the way turns a quarter of the angle, which a straight blend does not.
        var quarter = WorldKeyResolver.Direction(phase: 0.125d, span: 24d, track: track);

        Assert.Equal(expected: MathF.Cos(x: (MathF.PI / 8f)), actual: quarter.X, precision: 5);

        // Opposite keys turn about one fixed perpendicular, the same on every read.
        var opposite = Track((0d, Vector3.UnitX, WorldEase.Linear), (12d, -Vector3.UnitX, WorldEase.Linear));
        var turned = WorldKeyResolver.Direction(phase: 0.25d, span: 24d, track: opposite);

        Assert.Equal(expected: 0f, actual: Vector3.Dot(vector1: turned, vector2: Vector3.UnitX), precision: 5);
        Assert.Equal(expected: turned, actual: WorldKeyResolver.Direction(phase: 0.25d, span: 24d, track: opposite));
    }
    [InlineData(WorldEase.Linear, 0.25d, 0.25d)]
    [InlineData(WorldEase.Smooth, 0.25d, 0.15625d)]
    [InlineData(WorldEase.Smooth, 0.5d, 0.5d)]
    [InlineData(WorldEase.Step, 0.75d, 0d)]
    [Theory]
    public void Each_ease_shapes_the_time_from_its_key(WorldEase ease, double fraction, double expected) {
        var track = Track(
            (0d, 0f, ease),
            (12d, 1f, WorldEase.Linear)
        );

        Assert.Equal(
            actual: WorldKeyResolver.Scalar(phase: (fraction * 0.5d), span: 24d, track: track),
            expected: expected,
            precision: 6
        );
    }
    [Fact]
    public void The_last_key_wraps_into_the_first() {
        var track = Track(
            (6d, 0f, WorldEase.Linear),
            (18d, 1f, WorldEase.Linear)
        );

        // From 18 round to 6 is twelve units: at 0 the value is halfway back, before the first key as after the last.
        Assert.Equal(expected: 0.5f, actual: WorldKeyResolver.Scalar(phase: 0d, span: 24d, track: track), precision: 6);
        Assert.Equal(expected: 0.75f, actual: WorldKeyResolver.Scalar(phase: (21d / 24d), span: 24d, track: track), precision: 6);
        Assert.Equal(expected: 0.25f, actual: WorldKeyResolver.Scalar(phase: (3d / 24d), span: 24d, track: track), precision: 6);
        // One key holds its value at every phase.
        Assert.Equal(expected: 0.4f, actual: WorldKeyResolver.Scalar(phase: 0.9d, span: 24d, track: Track((3d, 0.4f, WorldEase.Smooth))));
    }
    [Fact]
    public void A_seed_keyed_is_refused_by_name() {
        Assert.Contains(
            expectedSubstring: "render.sky.keys[0].layers.night.seed is structure, which a key never states",
            actualString: Validate(definition: Definition(sky: KeyedSky(key: new WorldRenderSkyLayer.Stars(Brightness: 0.2f, Seed: 9u))))
        );
        // Control: the same key moving only the brightness is admitted.
        Assert.Equal(
            actual: Validate(definition: Definition(sky: KeyedSky(key: new WorldRenderSkyLayer.Stars(Brightness: 0.2f)))),
            expected: string.Empty
        );
    }
    [Fact]
    public void Every_kind_of_structure_a_key_states_is_refused_by_name() {
        Assert.Contains(
            expectedSubstring: "render.sky.keys[0].layers.night.density is structure",
            actualString: Validate(definition: Definition(sky: KeyedSky(key: new WorldRenderSkyLayer.Stars(Density: 12f))))
        );
        Assert.Contains(
            expectedSubstring: "render.sky.keys[0].layers.night.name is structure",
            actualString: Validate(definition: Definition(sky: KeyedSky(key: new WorldRenderSkyLayer.Stars(Name: "night"))))
        );
        Assert.Contains(
            expectedSubstring: "render.sky.keys[0].layers.night must keep the kind of the layer it names",
            actualString: Validate(definition: Definition(sky: KeyedSky(key: new WorldRenderSkyLayer.Fog(Density: 0.1f))))
        );
        var lighting = new WorldRenderLighting(
            Clock: "day",
            Keys: [new WorldRenderLightingKey(At: 0d, Lights: new Dictionary<string, WorldRenderLight> { ["sun"] = new WorldRenderLight.Directional(Shadows: false) })],
            Lights: [new WorldRenderLight.Directional(Name: "sun", Shadows: true)]
        );

        Assert.Contains(
            expectedSubstring: "render.lighting.keys[0].lights.sun.shadows is structure",
            actualString: Validate(definition: Definition(lighting: lighting))
        );
        Assert.Contains(
            expectedSubstring: "render.lighting.keys[0].lights.moon names no light",
            actualString: Validate(definition: Definition(lighting: lighting with {
                Keys = [new WorldRenderLightingKey(At: 0d, Lights: new Dictionary<string, WorldRenderLight> { ["moon"] = new WorldRenderLight.Directional(Weight: 0.2f) })],
            }))
        );
    }
    [Fact]
    public void A_key_on_an_undeclared_clock_or_out_of_order_is_refused_by_name() {
        var keyed = new WorldRenderSkyLayer.Fog(Density: new BindableScalar(keys: new WorldKeyTrack<float>(
            clock: "night",
            keys: [new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f)]
        )));

        Assert.Contains(
            expectedSubstring: "render.sky.layers[0].density keys on clock 'night', which timeline.clocks does not declare",
            actualString: Validate(definition: Definition(sky: new WorldRenderSky(Layers: [keyed])))
        );
        Assert.Contains(
            expectedSubstring: "render.sky.layers[0].density.keys[1].at 2 must exceed the previous key's",
            actualString: Validate(definition: Definition(sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: new BindableScalar(keys: Track(
                (4d, 0f, WorldEase.Linear),
                (2d, 0.1f, WorldEase.Linear)
            )))])))
        );
        Assert.Contains(
            expectedSubstring: "must be finite and in [0, 24)",
            actualString: Validate(definition: Definition(sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: new BindableScalar(keys: Track(
                (24d, 0f, WorldEase.Linear)
            )))])))
        );
        // Control: ascending keys inside the span on a declared clock.
        Assert.Equal(
            actual: Validate(definition: Definition(sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: new BindableScalar(keys: Track(
                (2d, 0f, WorldEase.Linear),
                (4d, 0.1f, WorldEase.Smooth)
            )))]))),
            expected: string.Empty
        );
    }
    [Fact]
    public void A_keyed_value_is_judged_at_each_of_its_keys() {
        // A negative fog density is refused at the key that states it, not only as a literal.
        Assert.Contains(
            expectedSubstring: "render.sky.layers[0].density.keys[1].value must be finite and non-negative",
            actualString: Validate(definition: Definition(sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: new BindableScalar(keys: Track(
                (2d, 0f, WorldEase.Linear),
                (4d, -0.1f, WorldEase.Linear)
            )))])))
        );
    }
    [Fact]
    public void Every_bindable_round_trips_its_keyed_wire_form_and_refuses_an_unknown_member() {
        const string Json = """
            { "clock": "day", "keys": [ { "at": 0, "value": 1 }, { "at": 12, "value": 3, "ease": "Smooth" } ] }
            """;
        var scalar = JsonSerializer.Deserialize<BindableScalar>(json: Json, options: WorldJsonContext.Default.Options);

        Assert.Equal(expected: "day", actual: scalar.Keys!.Clock);
        Assert.Equal(expected: WorldEase.Smooth, actual: scalar.Keys.Keys[1].Ease);
        Assert.Equal(
            expected: scalar,
            actual: JsonSerializer.Deserialize<BindableScalar>(json: JsonSerializer.Serialize(value: scalar, options: WorldJsonContext.Default.Options), options: WorldJsonContext.Default.Options)
        );

        var color = JsonSerializer.Deserialize<BindableColor>(json: """{ "clock": "day", "keys": [ { "at": 0, "value": "#FF0000" } ] }""", options: WorldJsonContext.Default.Options);

        Assert.Equal(expected: new Vector4(w: 1f, x: 1f, y: 0f, z: 0f), actual: color.Keys!.Keys[0].Value.Literal);
        Assert.Null(@object: color.Raw);

        var direction = JsonSerializer.Deserialize<BindableDirection>(json: """{ "clock": "day", "keys": [ { "at": 0, "value": [0, 1, 0] } ] }""", options: WorldJsonContext.Default.Options);

        Assert.Equal(expected: Vector3.UnitY, actual: direction.Keys!.Keys[0].Value);

        var exception = Assert.ThrowsAny<JsonException>(testCode: () => JsonSerializer.Deserialize<BindableScalar>(
            json: """{ "clock": "day", "keys": [], "rate": 1 }""",
            options: WorldJsonContext.Default.Options
        ));

        Assert.Contains(expectedSubstring: "has no member 'rate'", actualString: exception.Message);
        Assert.Contains(
            expectedSubstring: "ease 'Bouncy' is not Linear, Smooth or Step",
            actualString: Assert.ThrowsAny<JsonException>(testCode: () => JsonSerializer.Deserialize<BindableScalar>(
                json: """{ "clock": "day", "keys": [ { "at": 0, "value": 1, "ease": "Bouncy" } ] }""",
                options: WorldJsonContext.Default.Options
            )).Message
        );
    }
    [Fact]
    public void A_section_key_resolves_as_the_value_keys_it_lands_in() {
        var sky = new WorldRenderSky(
            Clock: "day",
            Keys: [
                new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["haze"] = new WorldRenderSkyLayer.Fog(Density: 0f) }),
                new WorldRenderSkyKey(At: 6d, Ease: WorldEase.Smooth, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["night"] = new WorldRenderSkyLayer.Stars(Brightness: 2f) }),
                new WorldRenderSkyKey(At: 12d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["haze"] = new WorldRenderSkyLayer.Fog(Density: 0.2f) }),
            ],
            Layers: [
                new WorldRenderSkyLayer.Fog(Density: 0.05f, Name: "haze"),
                new WorldRenderSkyLayer.Stars(Brightness: 1f, Name: "night"),
                new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: "#000000")), new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#FFFFFF"))]),
            ]
        );
        var expanded = WorldRenderKeys.Expand(sky: sky)!;
        var fog = Assert.IsType<WorldRenderSkyLayer.Fog>(@object: expanded.Layers![0]);
        var stars = Assert.IsType<WorldRenderSkyLayer.Stars>(@object: expanded.Layers[1]);

        // The fog's keys are the two section keys that state it; the stars' the one that does, so it holds.
        Assert.Equal(expected: [0d, 12d], actual: fog.Density!.Value.Keys!.Keys.Select(selector: static key => key.At));
        Assert.Equal(expected: 0.1f, actual: WorldKeyResolver.Scalar(phase: 0.25d, span: 24d, track: fog.Density.Value.Keys), precision: 6);
        Assert.Equal(expected: 2f, actual: WorldKeyResolver.Scalar(phase: 0.9d, span: 24d, track: stars.Brightness!.Value.Keys!));
        Assert.Equal(expected: WorldEase.Smooth, actual: stars.Brightness.Value.Keys!.Keys[0].Ease);
        // An unnamed layer no key addresses keeps its authored values, and the expanded section carries no keys.
        Assert.Equal(expected: sky.Layers![2], actual: expanded.Layers[2]);
        Assert.Null(@object: expanded.Keys);
        Assert.Equal(actual: Validate(definition: Definition(sky: sky)), expected: string.Empty);
    }
    [Fact]
    public void A_gradients_stops_stay_ascending_at_every_key() {
        static WorldRenderSky Sky(float lowAtNoon) => new(
            Clock: "day",
            Keys: [new WorldRenderSkyKey(At: 12d, Layers: new Dictionary<string, WorldRenderSkyLayer> {
                ["air"] = new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(Elevation: lowAtNoon), new WorldRenderSkyStop(Elevation: 1f)]),
            })],
            Layers: [new WorldRenderSkyLayer.Gradient(Name: "air", Stops: [
                new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: "#000000")),
                new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#FFFFFF")),
            ])]
        );

        Assert.Contains(
            expectedSubstring: "render.sky.layers[0].stops[1].elevation must exceed render.sky.layers[0].stops[0].elevation wherever they resolve; at 12 on clock 'day' the value below resolves 1 and the value above 1",
            actualString: Validate(definition: Definition(sky: Sky(lowAtNoon: 1f)))
        );
        Assert.Equal(actual: Validate(definition: Definition(sky: Sky(lowAtNoon: 0.5f))), expected: string.Empty);
        // A key stating some elevations and not others is refused, so a blend cannot cross two stops.
        Assert.Contains(
            expectedSubstring: "states 1 of 2 elevations",
            actualString: Validate(definition: Definition(sky: Sky(lowAtNoon: 0.5f) with {
                Keys = [new WorldRenderSkyKey(At: 12d, Layers: new Dictionary<string, WorldRenderSkyLayer> {
                    ["air"] = new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(Elevation: 0.5f), new WorldRenderSkyStop()]),
                })],
            }))
        );
    }
    [Fact]
    public void A_keyed_rate_integrates_like_a_literal_when_it_holds_one_key() {
        var clock = new WorldClock(Name: "day", PeriodSeconds: 60d);
        var tick = new PresentedTick(Fraction: 0.25d, Whole: ((1UL << 33) + 12345UL));

        Assert.Equal(
            expected: tick.Integrate(modulus: 64d, ratePerSecond: 0.75d),
            actual: WorldKeyResolver.Integrate(clock: clock, modulus: 64d, tick: tick, track: new WorldKeyTrack<float>(clock: "day", keys: [new WorldKey<float>(At: 17d, Ease: WorldEase.Smooth, Value: 0.75f)])),
            precision: 6
        );
    }
}
