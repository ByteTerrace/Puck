using System.Numerics;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Authoring-time laws for the <c>probes</c> section: the kind vocabulary hook, probe/binding cross-
/// references, source uniqueness, and the closed binding-row field ranges.</summary>
public sealed class ProbesAuthoringValidationLawTests {
    private const string ProbeId = "head";
    private const string ProbeKind = "ir-blob";
    private const string ChannelName = "x";

    private static WorldFrameSource CameraSource(WorldCameraSensor sensor = WorldCameraSensor.Infrared) => new WorldScreenSource.Camera(
        Profile: WorldFeedProfile.Default,
        Sensor: sensor
    );
    private static WorldProbe BuildProbe(string id = ProbeId, string kind = ProbeKind, IReadOnlyDictionary<string, WorldFrameSource>? inputs = null, string? track = null) => new(
        Id: id,
        Inputs: (inputs ?? (((track is null))
            ? new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = CameraSource() }
            : null)),
        Kind: kind,
        RateHz: 30U,
        Track: track
    );

    private static WorldDefinition WithProbes(WorldProbe[] probes, WorldProbeBinding[] bindings, WorldRenderExtensionEntry[]? extensions = null) {
        var document = Fixtures.BuildDocument() with {
            ProbesRaw = ((probes.Length == 0)
                ? probes
                : [probes[0] with { Bindings = bindings }, .. probes[1..]]
            ),
        };

        return ((extensions is null)
            ? document
            : (document with { RenderRaw = WorldRenderDefaults.Absent with { Extensions = extensions } })
        );
    }
    // A minimal valid declared camera row — the "view" socket law's control needs one to name.
    private static WorldCamera BuildCamera(string name) => new(
        Name: name,
        Anchor: null,
        Rig: new WorldCameraProgram(
            Name: $"{name}-rig",
            Version: WorldCameraProgram.CurrentVersion,
            Operations: [new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 0.9f))]
        ),
        RenderWidth: 320U,
        RenderHeight: 240U
    );

    [Fact]
    public void BlankKindRefusesWhileANonBlankKindPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.blank-kind",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe(kind: "")], bindings: []),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe()], bindings: []),
                reason: out _
            ));
    }
    [Fact]
    public void UnregisteredKindRefusesWhileTheRegisteredOnePasses() {
        var previous = WorldProbeVocabularyHook.ProbeKindCheck;

        try {
            WorldProbeVocabularyHook.ProbeKindCheck = static kind => (kind == ProbeKind);

            Laws.RefusalWithControl(
                lawId: "probes.unregistered-kind",
                deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                    definition: WithProbes(probes: [BuildProbe(kind: "not-shipped")], bindings: []),
                    reason: out _
                ),
                controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                    definition: WithProbes(probes: [BuildProbe()], bindings: []),
                    reason: out _
                ));

            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe(kind: "not-shipped")], bindings: []),
                reason: out var reason
            ));
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "probes[0].kind 'not-shipped' names no registered probe kind.");
        } finally {
            WorldProbeVocabularyHook.ProbeKindCheck = previous;
        }
    }
    [Fact]
    public void DuplicateAxisSourceRefusesWhileDistinctSourcesPass() {
        Laws.RefusalWithControl(
            lawId: "probes.duplicate-source",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x"),
                    new WorldProbeBinding.Axis(Channel: "y", Source: "head-x"),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x"),
                    new WorldProbeBinding.Axis(Channel: "y", Source: "head-y"),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void QuantizeBitsOutsideOneToSixteenRefusesWhileEightPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.quantize-bits",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x", QuantizeBits: 17),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x", QuantizeBits: 8),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void HysteresisAboveTheDeadbandRefusesWhileAReachableGatePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.hysteresis-above-deadband",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x", Deadband: 0.05f, Hysteresis: 0.10f),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x", Deadband: 0.10f, Hysteresis: 0.05f),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void DeadbandPlusHysteresisReachingOneRefusesWhileABelowOneSumPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.deadband-plus-hysteresis",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x", Deadband: 0.80f, Hysteresis: 0.30f),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Axis(Channel: ChannelName, Source: "head-x", Deadband: 0.60f, Hysteresis: 0.30f),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void ParameterTargetingAnUncomposedExtensionRefusesWhileAComposedOnePasses() {
        var extension = new WorldRenderExtensionEntry(Id: "sdf-film-grain");

        Laws.RefusalWithControl(
            lawId: "probes.parameter-target",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Parameter(Channel: "luminance", Target: new WorldProbeParameterTarget.Extension(Id: "not-composed", Field: "intensity"), Range: new Vector2(x: 0f, y: 1f)),
                ],
                extensions: [extension]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Parameter(Channel: "luminance", Target: new WorldProbeParameterTarget.Extension(Id: "sdf-film-grain", Field: "intensity"), Range: new Vector2(x: 0f, y: 1f)),
                ],
                extensions: [extension]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void ParameterTargetingAnUndeclaredProbeRefusesWhileADeclaredOnePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.parameter-probe-target",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe(), BuildProbe(id: "faerie")],
                bindings: [
                    new WorldProbeBinding.Parameter(Channel: ChannelName, Target: new WorldProbeParameterTarget.Probe(Id: "not-declared", Field: "anchorX"), Range: new Vector2(x: -1f, y: 1f)),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe(), BuildProbe(id: "faerie")],
                bindings: [
                    new WorldProbeBinding.Parameter(Channel: ChannelName, Target: new WorldProbeParameterTarget.Probe(Id: "faerie", Field: "anchorX"), Range: new Vector2(x: -1f, y: 1f)),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void ParameterTargetingItsOwnProbeRefusesWhileAnotherProbePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.parameter-self-target",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe(), BuildProbe(id: "faerie")],
                bindings: [
                    new WorldProbeBinding.Parameter(Channel: ChannelName, Target: new WorldProbeParameterTarget.Probe(Id: ProbeId, Field: "threshold"), Range: new Vector2(x: 0f, y: 1f)),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe(), BuildProbe(id: "faerie")],
                bindings: [
                    new WorldProbeBinding.Parameter(Channel: ChannelName, Target: new WorldProbeParameterTarget.Probe(Id: "faerie", Field: "threshold"), Range: new Vector2(x: 0f, y: 1f)),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void ScreenShowingAnUndeclaredProbeRefusesWhileADeclaredOnePasses() {
        static WorldDefinition WithProbeScreen(string probeId) => Fixtures.BuildDocument() with {
            ProbesRaw = [BuildProbe()],
            ScreensRaw = [
                new WorldScreen(
                    Index: 0,
                    Origin: new Vector3(x: 0f, y: 1f, z: 0f),
                    Right: new Vector3(x: 1f, y: 0f, z: 0f),
                    Up: new Vector3(x: 0f, y: 1f, z: 0f),
                    HalfWidth: 1f,
                    HalfHeight: 1f,
                    HalfDepth: 0.1f,
                    Round: 0f,
                    Source: new WorldScreenSource.Probe(Id: probeId),
                    Route: WorldScreenRoute.Passive
                ),
            ],
        };

        Laws.RefusalWithControl(
            lawId: "screens.probe-source",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbeScreen(probeId: "not-declared"),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbeScreen(probeId: ProbeId),
                reason: out _
            ));
    }
    [Fact]
    public void ControlNamingNoWorldCameraControlsMemberRefusesWhileABrightnessControlPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.control-name",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Control(Channel: "x", ControlName: "not-a-control", Minimum: 0, Maximum: 255),
                ]
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [
                    new WorldProbeBinding.Control(Channel: "x", ControlName: "brightness", Minimum: 0, Maximum: 255),
                ]
            ),
                reason: out _
            ));
    }
    [Fact]
    public void BothInputsAndTrackRefuseWhileInputsAloneOnPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.both-inputs-and-track",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe() with { Track = "tracks/brio-head.probe-track.json" }], bindings: []),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe()], bindings: []),
                reason: out _
            ));
    }
    [Fact]
    public void NeitherInputsNorTrackRefusesWhileInputsAlonePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.neither-inputs-nor-track",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe() with { Inputs = null }], bindings: []),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe()], bindings: []),
                reason: out _
            ));
    }
    [Fact]
    public void ABadSocketNameRefusesWhileAnIdentifierPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.bad-socket-name",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                probes: [BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["1bad"] = CameraSource() })],
                bindings: []
            ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(probes: [BuildProbe()], bindings: []),
                reason: out _
            ));
    }
    [Fact]
    public void AViewSocketNamingNoCameraRefusesWhileADeclaredCameraPasses() {
        static WorldDefinition WithViewSocket(bool declareCamera) {
            var document = Fixtures.BuildDocument() with {
                ProbesRaw = [BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = new WorldScreenSource.View(CameraName: "gallery") })],
            };

            return (declareCamera
                ? (document with { CamerasRaw = [BuildCamera(name: "gallery")] })
                : document
            );
        }

        Laws.RefusalWithControl(
            lawId: "probes.socket-view-camera",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithViewSocket(declareCamera: false),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithViewSocket(declareCamera: true),
                reason: out _
            ));
    }
    [Fact]
    public void AProbeSocketNamingItsOwnProbeRefusesWhileAnotherProbePasses() {
        static WorldDefinition WithProbeSocket(string targetId) => Fixtures.BuildDocument() with {
            ProbesRaw = [
                BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = new WorldScreenSource.Probe(Id: targetId) }),
                BuildProbe(id: "faerie"),
            ],
        };

        Laws.RefusalWithControl(
            lawId: "probes.socket-self-probe",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbeSocket(targetId: ProbeId),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbeSocket(targetId: "faerie"),
                reason: out _
            ));
    }
}
