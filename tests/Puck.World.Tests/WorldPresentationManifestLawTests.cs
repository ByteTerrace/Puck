using System.Text.Json;

using Puck.Abstractions.Counting;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the presentation manifest (<see cref="WorldPresentationManifest"/>): it is exactly the deduplicated union of
/// the state bindings a document's presentation sections author, with per-body templates kept apart and their
/// <c>$body</c> key as authored; a mirror installing a document registers the manifest's slots, and installing it again
/// — or installing a reloaded copy of it — registers nothing new, keeps every slot index, and allocates nothing; and the
/// walk over the shipped flagship world completes with a plausible count.
/// </summary>
public sealed class WorldPresentationManifestLawTests {
    private const int Repetitions = 64;

    private static readonly WorldPresentationBinding[] ExpectedBindings = [
        Number(row: "alpha"),
        Color(key: "ring", row: "palette"),
        Number(row: "alpha", target: true),
        Color(key: "accent", row: "palette"),
        Number(row: "flag"),
        Number(row: "gauge"),
        Number(row: "score", key: "home"),
        Number(row: "bar", key: "layout"),
        Number(row: "bar", key: "model"),
        Number(row: "daylight", target: true),
    ];
    private static readonly WorldPresentationBinding[] ExpectedBodyBindings = [
        Number(row: "scale", key: StateBinding.BodyKey),
        Number(key: StateBinding.BodyKey, row: "pose", target: true),
        Number(row: "tint"),
        Number(row: "swayClock"),
        Number(key: StateBinding.BodyKey, row: "armed", target: true),
        Number(row: "aim", key: StateBinding.BodyKey),
    ];

    private static WorldPresentationBinding Number(string row, string? key = null, bool target = false) => new(
        Binding: new StateBinding(
            Key: key,
            Row: row,
            Target: target
        ),
        Conversion: WorldStateConversion.Number
    );
    private static WorldPresentationBinding Color(string row, string? key = null) => new(
        Binding: new StateBinding(
            Key: key,
            Row: row,
            Target: false
        ),
        Conversion: WorldStateConversion.Color
    );
    private static T Section<T>(string json) => (JsonSerializer.Deserialize<T>(
        json: json,
        options: WorldJsonContext.Default.Options
    ) ?? throw new InvalidOperationException(message: $"The fixture's {typeof(T).Name} parsed to null."));
    // A look lane is authored as expression IR; its spelling is the one parse of that IR.
    private static WorldLooksSection WithLane(WorldLooksSection looks) {
        var look = looks.Rows![0];

        return looks with {
            Rows = [look with {
                Motion = look.Motion with { Lanes = [ExpressionProgram.Parse(text: "tint * 2")] },
            }],
        };
    }
    // The base fixture document with one binding on every surface the manifest reads, several spelled twice so the
    // dedup is observable. Each section is parsed from its authored JSON through the document's own serializer.
    private static WorldDefinition BoundDocument() {
        var baseline = Fixtures.BuildDocument();
        var dancer = CreationFixtures.Sphere(
            id: "dancer",
            scale: 1f
        );

        return baseline with {
            BindingOverlaysRaw = Section<IReadOnlyList<WorldBindingOverlay>>(json: """
                [
                  {
                    "id": "bar",
                    "document": { "chords": [], "modifiers": [], "version": "puck.input.bindings.v1" },
                    "bindingBar": { "slotSet": [], "banks": [], "layoutCell": "state.bar.layout", "modelCell": "state.bar.model" }
                  }
                ]
                """),
            CreationsRaw = [dancer with {
                Document = dancer.Document with {
                    Drivers = Section<IReadOnlyList<Puck.World.Authoring.CreationDriverDocument>>(json: """
                        [
                          { "cadence": 1, "name": "sway", "signal": "state.swayClock", "when": [ "moving", "state.armed.$body" ] },
                          { "cadence": 1, "name": "stride", "signal": "planarTravel", "when": [ "Grounded" ] }
                        ]
                        """),
                    Effectors = Section<IReadOnlyList<Puck.World.Authoring.CreationEffectorDocument>>(json: """
                        [
                          { "name": "point", "chain": [ "arm" ], "tip": "hand", "target": { "kind": "state", "reference": "state.aim.$body" } }
                        ]
                        """),
                },
            }],
            HudRaw = Section<WorldHudSection>(json: """
                {
                  "defaults": { "enabled": true },
                  "panels": [
                    {
                      "id": "scoreboard",
                      "rect": { "x": 0, "y": 0, "width": 0.5, "height": 0.2 },
                      "layer": "Over",
                      "style": "Panel",
                      "visible": { "$type": "state", "binding": "state.flag", "value": 1 },
                      "elements": [
                        {
                          "id": "gauge",
                          "kind": "Gauge",
                          "rect": { "x": 0, "y": 0, "width": 1, "height": 0.5 },
                          "style": "Primary",
                          "binding": "state.gauge"
                        },
                        {
                          "id": "line",
                          "kind": "Text",
                          "rect": { "x": 0, "y": 0.5, "width": 1, "height": 0.5 },
                          "style": "Primary",
                          "template": "{state.gauge} / {state.score.home}"
                        }
                      ]
                    }
                  ]
                }
                """),
            LooksRaw = WithLane(looks: Section<WorldLooksSection>(json: """
                {
                  "rows": [
                    {
                      "name": "dancer",
                      "scale": 1,
                      "source": { "$type": "creation", "prototypeId": "dancer" },
                      "motion": {
                        "cues": [],
                        "gaitAmplitude": 0,
                        "replayFrames": false,
                        "secondsPerFrame": 0,
                        "poses": { "wave": "state.pose.$body", "bow": "state.pose.$body" }
                      }
                    }
                  ]
                }
                """)),
            MarkersRaw = Section<IReadOnlyList<WorldMarkerRow>>(json: """
                [
                  {
                    "id": "chime",
                    "source": { "$type": "point", "position": [0, 0, 0] },
                    "icon": "dot",
                    "style": { "chipAlpha": "state.alpha", "size": 8, "ringColor": "state.palette.ring", "ringAlpha": "state.alpha.$target" }
                  },
                  {
                    "id": "bell",
                    "source": { "$type": "point", "position": [1, 0, 0] },
                    "icon": "dot",
                    "style": { "chipAlpha": "state.alpha", "size": 8, "ringColor": "state.palette.accent" }
                  }
                ]
                """),
            PopulationRaw = (baseline.Population with { ScaleRow = "scale" }),
            RenderRaw = (baseline.Render with {
                Cycle = Section<WorldRenderCycle>(json: """
                    { "state": "daylight", "keys": [ { "at": 0 }, { "at": 0.5 } ] }
                    """),
            }),
        };
    }

    [Fact]
    public void TheManifestIsExactlyTheDeduplicatedUnionOfTheDocumentsBindings() {
        var manifest = WorldPresentationManifest.Compile(definition: BoundDocument());

        Assert.Equal(
            actual: manifest.Bindings.ToArray().Select(selector: static entry => entry.ToString()).Order(),
            expected: ExpectedBindings.Select(selector: static entry => entry.ToString()).Order()
        );
        Assert.Equal(
            actual: manifest.Bindings.Length,
            expected: ExpectedBindings.Length
        );
        Assert.Equal(
            actual: manifest.BodyBindings.ToArray().Select(selector: static entry => entry.ToString()).Order(),
            expected: ExpectedBodyBindings.Select(selector: static entry => entry.ToString()).Order()
        );
        Assert.Equal(
            actual: manifest.BodyBindings.Length,
            expected: ExpectedBodyBindings.Length
        );
    }
    [Fact]
    public void InstallRegistersTheManifest_AndReinstallingAReloadedCopyKeepsEverySlot() {
        var definition = BoundDocument();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var slots = ExpectedBindings.Select(selector: entry => mirror.Register(
            binding: entry.Binding,
            conversion: entry.Conversion
        )).ToArray();

        Assert.Equal(
            actual: mirror.SlotCount,
            expected: ExpectedBindings.Length
        );

        // A reload delivers a fresh parse of the same document, whose manifest is compiled again.
        definition = BoundDocument();
        mirror.Install(
            engineTick: 0UL,
            tick: 1UL
        );

        Assert.Equal(
            actual: mirror.SlotCount,
            expected: ExpectedBindings.Length
        );
        Assert.Equal(
            actual: ExpectedBindings.Select(selector: entry => mirror.Register(
                binding: entry.Binding,
                conversion: entry.Conversion
            )).ToArray(),
            expected: slots
        );
    }
    [Fact]
    public void ReinstallingAnIdenticalManifestAllocatesNothing() {
        var definition = BoundDocument();
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var repetition = 0; (repetition < Repetitions); repetition++) {
                    mirror.Install(
                        engineTick: 0UL,
                        tick: 0UL
                    );
                }
            }),
            expected: 0L
        );
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: ExpectedBindings.Length
        );
    }
    [Fact]
    public void TheFlagshipWorldsManifestCompilesWithAPlausibleCount() {
        var definition = AuthoredGameFixtures.Nexus;
        var manifest = WorldPresentationManifest.Of(definition: definition);

        Assert.Same(
            actual: WorldPresentationManifest.Of(definition: definition),
            expected: manifest
        );
        Assert.InRange(
            actual: manifest.Bindings.Length,
            high: 256,
            low: 1
        );
        Assert.InRange(
            actual: manifest.BodyBindings.Length,
            high: 256,
            low: 1
        );
        Assert.Contains(
            collection: manifest.Bindings.ToArray(),
            expected: Number(row: "captureStation")
        );
        Assert.Contains(
            collection: manifest.BodyBindings.ToArray(),
            expected: Number(row: "swayClock")
        );
        Assert.Contains(
            collection: manifest.BodyBindings.ToArray(),
            expected: Number(row: "scale", key: StateBinding.BodyKey)
        );
    }
}
