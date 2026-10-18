using Puck.World.Authoring;
using Puck.SignedDistance;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// THE LAW: a <c>puck.creation.v1</c> palette entry's <see cref="PaletteEntryDocument.Roughness"/>/
/// <see cref="PaletteEntryDocument.Sheen"/> — the fields that replaced the retired raw <c>shininess</c> exponent —
/// are refused by name when non-finite, and a document still spelling <c>shininess</c> never reaches validation at
/// all: it is an unmapped member the deserializer itself refuses.
/// </summary>
public sealed class PaletteAdmissionLawTests {
    private const string PrototypeId = "palette-admission";

    private static void AssertAccepts(PaletteEntryDocument entry) => CreationFixtures.AssertAccepts(document: Document(entry: entry));
    private static void AssertRefusesNaming(PaletteEntryDocument entry, string needle) => CreationFixtures.AssertRefusesNaming(
        document: Document(entry: entry),
        needle: needle
    );
    private static CreationDocument Document(PaletteEntryDocument entry) => CreationFixtures.Document(
        name: PrototypeId,
        palette: [entry],
        shapes: [CreationFixtures.UnitSphereShape with { Name = "a" }, CreationFixtures.UnitSphereShape with { Id = 1, Name = "folded", Domain = [new ShapeDomainOp.Symmetry(Normal: System.Numerics.Vector3.UnitX)] }]
    );

    [Fact]
    public void AFillThatIsNeitherHexNorAStateBindingIsRefusedByName() {
        AssertRefusesNaming(
            entry: new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: null,
                Fill: "warm"
            ),
            needle: "fill"
        );

        // Control.
        AssertAccepts(entry: new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Fill: "#33150A"
        ));
        AssertAccepts(entry: new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Fill: null
        ));
    }
    [Fact]
    public void IndirectPaletteInputsAreAdmittedBeforeStamping() {
        var entry = new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null);

        AssertRefusesNaming(entry: entry with { Bleed = "warm" }, needle: "bleed");
        foreach (var invalid in new[] { -0.1f, float.NaN, float.PositiveInfinity }) {
            AssertRefusesNaming(entry: entry with { Receive = invalid }, needle: "receive");
        }
        AssertAccepts(entry: entry with { Bleed = "#000000", Receive = 0 });
        AssertAccepts(entry: entry with { Bleed = "#FFFFFF", Receive = 2 });
        AssertAccepts(entry: entry);
    }
    [Fact]
    public void ANonFiniteRoughnessIsRefusedByName() {
        AssertRefusesNaming(
            entry: new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: float.NaN
            ),
            needle: "roughness"
        );
        AssertRefusesNaming(
            entry: new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: float.PositiveInfinity
            ),
            needle: "roughness"
        );

        // Control: a finite roughness inside [0, 1] is accepted here.
        AssertAccepts(entry: new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: 0.4f
        ));
    }
    [Fact]
    public void ANonFiniteSheenIsRefusedByName() {
        AssertRefusesNaming(
            entry: new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: null,
                Sheen: float.NaN
            ),
            needle: "sheen"
        );

        // Control.
        AssertAccepts(entry: new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Sheen: 0.3f
        ));
    }
    // THE LAW: the four unit-range lanes (roughness, sheen, metal, coat) are refused BY NAME at this door when
    // outside [0, 1] — SdfMaterial's own RequireUnitRange would otherwise throw at stamp emission, after the document
    // had validated clean. The closed boundary (0 and 1) is the control.
    [Theory]
    [InlineData("roughness")]
    [InlineData("sheen")]
    [InlineData("metal")]
    [InlineData("coat")]
    public void AUnitRangeLaneOutsideZeroOneIsRefusedByName(string lane) {
        static PaletteEntryDocument With(string lane, float value) => lane switch {
            "roughness" => new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: value
        ),
            "sheen" => new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Sheen: value
        ),
            "metal" => new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Metal: value
        ),
            _ => new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Coat: value
        ),
        };

        AssertRefusesNaming(
            entry: With(
                lane: lane,
                value: 1.5f
            ),
            needle: $"{lane} must be in [0, 1]"
        );
        AssertRefusesNaming(
            entry: With(
                lane: lane,
                value: -0.01f
            ),
            needle: $"{lane} must be in [0, 1]"
        );
        AssertAccepts(entry: With(
            lane: lane,
            value: 1f
        ));
        AssertAccepts(entry: With(
            lane: lane,
            value: 0f
        ));
    }
    // An entry leaving Roughness/Sheen unauthored (null) is the common shipped case and must validate clean.
    [Fact]
    public void AnUnauthoredRoughnessAndSheenAreAccepted() {
        AssertAccepts(entry: new PaletteEntryDocument(
            Color: "#CCCCCC",
            Emissive: null,
            Specular: null,
            Roughness: null
        ));
    }
    [Fact]
    public void InsetRequiresOrderedStopsAndValidColors() {
        var inset = new PaletteInsetDocument(
            System.Numerics.Vector3.Zero,
            System.Numerics.Quaternion.Identity,
            0.1f,
            1f,
            new([new(
                    Color: "#000000",
                    Radius: 0f
                ), new(
                    Color: "#FFFFFF",
                    Radius: 1f
                )])
        );
        var entry = new PaletteEntryDocument(
            "#CCCCCC",
            null,
            null,
            null,
            Inset: inset
        );

        AssertAccepts(entry: entry);
        AssertRefusesNaming(
            entry: entry with {
                Inset = inset with {
                    Paint = new([new(
                    Color: "#FFFFFF",
                    Radius: 1f
                ), new(
                    Color: "#000000",
                    Radius: 0f
                )]),
                },
            },
            needle: "Invalid inset"
        );
        AssertRefusesNaming(
            entry: entry with {
                Inset = inset with {
                    Paint = new([new(
                    Color: "brown",
                    Radius: 0f
                )]),
                },
            },
            needle: "Layer colors"
        );
    }
    [Fact]
    public void PaintsTakeAShapesAdmissionAColorAParentShapeAndAtMostEightToASlot() {
        var band = new PalettePaintDocument(Type: SdfSolidPrimitive.Plane, Color: "#EED3A6", Position: new System.Numerics.Vector3(x: 0f, y: -0.0125f, z: 0f), Stroke: 0.02f);
        var ring = new PalettePaintDocument(Type: SdfSolidPrimitive.Prism, Color: "#9D66C6", Scale: new System.Numerics.Vector3(x: 0.2f, y: 0.18f, z: 0.4f), Profile: new SdfPrismProfile(Kind: SdfPrismProfileKind.Ellipse), Stroke: 0.02f, Parent: "a");
        var line = new PalettePaintDocument(Type: SdfSolidPrimitive.Sweep, Color: "#754137", Curve: new ShapeCurveDocument(A: new System.Numerics.Vector3(x: 0f, y: 0f, z: 0f), B: new System.Numerics.Vector3(x: 0.02f, y: -0.01f, z: 0f), C: new System.Numerics.Vector3(x: 0.04f, y: 0f, z: 0f), RadiusStart: 0.003f, RadiusEnd: 0.003f));
        var entry = new PaletteEntryDocument("#9D66C6", null, null, null, Paints: [band]);

        AssertAccepts(entry: entry);
        AssertAccepts(entry: entry with { Paints = [band, ring, line] });
        AssertRefusesNaming(entry: entry with { Paints = [band with { Rotation = new System.Numerics.Quaternion(x: 0f, y: 0f, z: 0f, w: 0f) }] }, needle: "Invalid inset, weathering or paint");
        AssertRefusesNaming(entry: entry with { Paints = [band, band with { Stroke = -1f }] }, needle: "stroke must be finite and non-negative");
        AssertRefusesNaming(entry: entry with { Paints = [band with { Color = "cream" }] }, needle: "Layer colors");
        AssertRefusesNaming(entry: entry with { Paints = [ring with { Parent = "nowhere" }] }, needle: "parent 'nowhere' names no shape");
        AssertRefusesNaming(entry: entry with { Paints = [ring with { Parent = "folded" }] }, needle: "carries a domain fold");
        AssertRefusesNaming(entry: entry with { Paints = [null!] }, needle: "paint entry is null");
        AssertRefusesNaming(entry: entry with { Paints = [ring with { Profile = new SdfPrismProfile(Kind: SdfPrismProfileKind.Path) }] }, needle: "path profile");
        AssertRefusesNaming(entry: entry with { Paints = [line with { Curve = null }] }, needle: "a Sweep paint requires a curve");
        AssertRefusesNaming(entry: entry with { Paints = [ring with { Scale = new System.Numerics.Vector3(x: float.NaN, y: 1f, z: 1f) }] }, needle: "A scale must be finite");
        AssertRefusesNaming(entry: entry with { Paints = [band with { Type = (SdfSolidPrimitive)99 }] }, needle: "is not a primitive");
        AssertRefusesNaming(entry: entry with { Paints = [band, band, band, band, band, band, band, band, band] }, needle: "9 paints exceed the 8 a palette slot draws");
    }
    [Fact]
    public void WeatheringRequiresAuthoredRevealAndDepositSurfaces() {
        var entry = new PaletteEntryDocument(
            "#CCCCCC",
            null,
            null,
            null,
            Weathering: new(Edge: 1f)
        );

        AssertRefusesNaming(
            entry: entry,
            needle: "Invalid inset, weathering or paint"
        );
        AssertAccepts(entry: entry with {
            Weathering = new(
            Edge: 1f,
            Under: [new(
                    0.4f,
                    new(
                        Color: "#223344",
                        Metal: 0.8f,
                        Roughness: 0.5f
                    )
                )]
        ),
        });
        AssertRefusesNaming(
            entry: entry with { Weathering = new(Settle: 1f) },
            needle: "Invalid inset, weathering or paint"
        );
    }
    [InlineData("wrap")]
    [InlineData("soften")]
    [Theory]
    public void WrapOrSoftenOutsideZeroOneIsRefusedByName(string lane) {
        var denied = ((lane == "wrap")
            ? new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: null,
                Wrap: 1.5f
            )
            : new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: null,
                Soften: -0.01f
            )
        );
        var control = ((lane == "wrap")
            ? new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: null,
                Wrap: 0.3f
            )
            : new PaletteEntryDocument(
                Color: "#CCCCCC",
                Emissive: null,
                Specular: null,
                Roughness: null,
                Soften: 0.5f
            )
        );

        AssertRefusesNaming(
            entry: denied,
            needle: $"{lane} must be in [0, 1]"
        );
        AssertAccepts(entry: control);
    }
}
