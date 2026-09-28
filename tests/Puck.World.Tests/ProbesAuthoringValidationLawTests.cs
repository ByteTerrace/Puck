using System.Numerics;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Authoring-time laws for the <c>probes</c> section: the kind vocabulary hook, probe/binding cross-
/// references, source uniqueness, and the closed binding-row field ranges.</summary>
public sealed class ProbesAuthoringValidationLawTests {
    private const string ChannelName = "x";
    private const string ProbeId = "head";
    private const string ProbeKind = "ir-blob";

    // A minimal valid declared camera row — the "view" socket law's control needs one to name.
    private static WorldCamera BuildCamera(string name) => new(
        Name: name,
        Anchor: null,
        Rig: new WorldCameraProgram(
            Name: $"{name}-rig",
            Version: WorldCameraProgram.CurrentVersion,
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))]
        ),
        RenderWidth: 320U,
        RenderHeight: 240U
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
    private static WorldFrameSource CameraSource(WorldCameraSensor sensor = WorldCameraSensor.Infrared) => WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
        Profile: WorldFeedProfile.Default,
        Sensor: sensor
    ));
    private static WorldDefinition WithProbes(WorldProbe[] probes, WorldProbeBinding[] bindings, WorldViewPostPass[]? post = null) {
        var document = Fixtures.BuildDocument() with {
            ProbesRaw = ((probes.Length == 0)
            ? probes
            : [probes[0] with { Bindings = bindings }, .. probes[1..]]),
        };

        return ((post is null)
            ? document
            : (document with { ViewsRaw = document.Views with { Post = post } })
        );
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
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    // A seat-relative probe's instances are keyed '<id>$<seat>' in the probe-id namespace, so an authored id in that
    // generated form (or the file form) is refused by name, and the same id without the joiner passes.
    [InlineData("head$2")]
    [InlineData("head~2")]
    [Theory]
    public void AProbeIdInTheGeneratedFormRefusesWhileItsAuthorTwinPasses(string generated) {
        Laws.RefusalWithControl(
            lawId: "probes.id-generated-form",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(id: generated)],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(id: "head-2")],
                    bindings: []
                ),
                reason: out _
            )
        );
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
            )
        );
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
            )
        );
    }
    [Fact]
    public void AxisSeatAuthoredOnASeatRelativeProbeRefusesWhileUnauthoredPasses() {
        // BuildProbe's default camera source ("lit", sensor Infrared) carries no Seat, so the row is seat-relative;
        // an axis binding may not author its own seat there — it always takes its instance's.
        Laws.RefusalWithControl(
            lawId: "probes.axis-seat-on-seat-relative",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x",
                            Seat: 1
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x"
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void AxisSeatOutsideLocalSeatsOnASingleInstanceProbeRefusesWhileAnInRangeSeatPasses() {
        // Every camera socket names its own seat, so the row is NOT seat-relative — the ordinary range law applies,
        // exactly as it did before seat-relative instancing existed.
        static WorldFrameSource SeatedCameraSource() => WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
            Profile: WorldFeedProfile.Default,
            Sensor: WorldCameraSensor.Infrared,
            Seat: 1
        ));
        var seatedInputs = new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = SeatedCameraSource() };

        Laws.RefusalWithControl(
            lawId: "probes.axis-seat-range",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(inputs: seatedInputs)],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x",
                            Seat: 99
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(inputs: seatedInputs)],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x",
                            Seat: 1
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void BlankKindRefusesWhileANonBlankKindPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.blank-kind",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(kind: "")],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void BothInputsAndTrackRefuseWhileInputsAloneOnPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.both-inputs-and-track",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe() with { Track = "tracks/brio-head.probe-track.json" }],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void CameraControlsOnAProbeSocketRefuseWhileAnUncontrolledCameraPasses() {
        var controlled = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
            Controls: new WorldCameraControls(Brightness: 1),
            Profile: WorldFeedProfile.Default,
            Sensor: WorldCameraSensor.Infrared
        ));

        Laws.RefusalWithControl(
            lawId: "probes.camera-controls-not-hosted",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = controlled })],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void CaptureSocketRefusesWhileACameraSocketPasses() {
        var capture = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(
            WindowTitle: "OBS",
            Profile: WorldFeedProfile.Default
        ));

        Laws.RefusalWithControl(
            lawId: "probes.capture-input-not-hosted",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = capture })],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void ControlNamingNoWorldCameraControlsMemberRefusesWhileABrightnessControlPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.control-name",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Control(
                            Channel: "x",
                            ControlName: "not-a-control",
                            Minimum: 0,
                            Maximum: 255
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Control(
                            Channel: "x",
                            ControlName: "brightness",
                            Minimum: 0,
                            Maximum: 255
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    // An axis gate must be reachable: its hysteresis sits inside its deadband, and the two together stay below one.
    [InlineData("probes.deadband-plus-hysteresis", 0.80f, 0.30f, 0.60f, 0.30f)]
    [InlineData("probes.hysteresis-above-deadband", 0.05f, 0.10f, 0.10f, 0.05f)]
    [Theory]
    public void AnUnreachableAxisGateRefusesWhileAReachableOnePasses(string lawId, float deniedDeadband, float deniedHysteresis, float controlDeadband, float controlHysteresis) {
        bool Validates(float deadband, float hysteresis) => WorldDefinitionValidator.TryValidateLocally(
            definition: WithProbes(
                probes: [BuildProbe()],
                bindings: [new WorldProbeBinding.Axis(
                    Channel: ChannelName,
                    Source: "head-x",
                    Deadband: deadband,
                    Hysteresis: hysteresis
                )]
            ),
            reason: out _
        );

        Laws.RefusalWithControl(
            lawId: lawId,
            deniedOutcome: () => Validates(
                deadband: deniedDeadband,
                hysteresis: deniedHysteresis
            ),
            controlOutcome: () => Validates(
                deadband: controlDeadband,
                hysteresis: controlHysteresis
            )
        );
    }
    [Fact]
    public void DuplicateAxisSourceRefusesWhileDistinctSourcesPass() {
        Laws.RefusalWithControl(
            lawId: "probes.duplicate-source",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x"
                        ),
                    new WorldProbeBinding.Axis(
                            Channel: "y",
                            Source: "head-x"
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x"
                        ),
                    new WorldProbeBinding.Axis(
                            Channel: "y",
                            Source: "head-y"
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void MixedCameraSocketSeatsRefuseWhileMatchingSeatsPass() {
        static WorldFrameSource At(int seat) => WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(
            Profile: WorldFeedProfile.Default,
            Seat: seat,
            Sensor: WorldCameraSensor.Infrared
        ));

        Laws.RefusalWithControl(
            lawId: "probes.camera-sockets-one-graph",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = At(seat: 1), ["other"] = At(seat: 2) })],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(inputs: new Dictionary<string, WorldFrameSource>(comparer: StringComparer.Ordinal) { ["lit"] = At(seat: 1), ["other"] = At(seat: 1) })],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void NeitherInputsNorTrackRefusesWhileInputsAlonePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.neither-inputs-nor-track",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe() with { Inputs = null }],
                    bindings: []
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: []
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void ParameterTargetingAnUndeclaredPostPassRefusesWhileADeclaredOnePasses() {
        var grain = new WorldViewPostPass(Name: "grain", Package: "sdf.film-grain");

        Laws.RefusalWithControl(
            lawId: "probes.parameter-target",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Parameter(
                            Channel: "luminance",
                            Target: new WorldProbeParameterTarget.Post(
                                Field: "intensity",
                                Pass: "not-declared"
                            ),
                            Range: new Vector2(
                                x: 0f,
                                y: 1f
                            )
                        ),
                ],
                    post: [grain]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Parameter(
                            Channel: "luminance",
                            Target: new WorldProbeParameterTarget.Post(
                                Field: "intensity",
                                Pass: "grain"
                            ),
                            Range: new Vector2(
                                x: 0f,
                                y: 1f
                            )
                        ),
                ],
                    post: [grain]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void ParameterTargetingAnUndeclaredProbeRefusesWhileADeclaredOnePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.parameter-probe-target",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(), BuildProbe(id: "faerie")],
                    bindings: [
                    new WorldProbeBinding.Parameter(
                            Channel: ChannelName,
                            Target: new WorldProbeParameterTarget.Probe(
                                Field: "anchorX",
                                Id: "not-declared"
                            ),
                            Range: new Vector2(
                                x: -1f,
                                y: 1f
                            )
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(), BuildProbe(id: "faerie")],
                    bindings: [
                    new WorldProbeBinding.Parameter(
                            Channel: ChannelName,
                            Target: new WorldProbeParameterTarget.Probe(
                                Field: "anchorX",
                                Id: "faerie"
                            ),
                            Range: new Vector2(
                                x: -1f,
                                y: 1f
                            )
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void ParameterTargetingItsOwnProbeRefusesWhileAnotherProbePasses() {
        Laws.RefusalWithControl(
            lawId: "probes.parameter-self-target",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(), BuildProbe(id: "faerie")],
                    bindings: [
                    new WorldProbeBinding.Parameter(
                            Channel: ChannelName,
                            Target: new WorldProbeParameterTarget.Probe(
                                Field: "threshold",
                                Id: ProbeId
                            ),
                            Range: new Vector2(
                                x: 0f,
                                y: 1f
                            )
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(), BuildProbe(id: "faerie")],
                    bindings: [
                    new WorldProbeBinding.Parameter(
                            Channel: ChannelName,
                            Target: new WorldProbeParameterTarget.Probe(
                                Field: "threshold",
                                Id: "faerie"
                            ),
                            Range: new Vector2(
                                x: 0f,
                                y: 1f
                            )
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void QuantizeBitsOutsideOneToSixteenRefusesWhileEightPasses() {
        Laws.RefusalWithControl(
            lawId: "probes.quantize-bits",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x",
                            QuantizeBits: 17
                        ),
                ]
                ),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe()],
                    bindings: [
                    new WorldProbeBinding.Axis(
                            Channel: ChannelName,
                            Source: "head-x",
                            QuantizeBits: 8
                        ),
                ]
                ),
                reason: out _
            )
        );
    }
    [Fact]
    public void ScreenShowingAnUndeclaredProbeRefusesWhileADeclaredOnePasses() {
        static WorldDefinition WithProbeScreen(string probeId) => Fixtures.BuildDocument() with {
            ProbesRaw = [BuildProbe()],
            ScreensRaw = [
                new WorldScreen(
                Index: 0,
                Origin: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
                Right: new Vector3(
                    x: 1f,
                    y: 0f,
                    z: 0f
                ),
                Up: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
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
            )
        );
    }
    [Fact]
    public void UnregisteredKindRefusesWhileTheRegisteredOnePasses() {
        var previous = WorldProbeVocabularyHook.ProbeKindCheck;

        try {
            WorldProbeVocabularyHook.ProbeKindCheck = static kind => (kind == ProbeKind);

            Laws.RefusalWithControl(
                lawId: "probes.unregistered-kind",
                deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                    definition: WithProbes(
                        probes: [BuildProbe(kind: "not-shipped")],
                        bindings: []
                    ),
                    reason: out _
                ),
                controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                    definition: WithProbes(
                        probes: [BuildProbe()],
                        bindings: []
                    ),
                    reason: out _
                )
            );

            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: WithProbes(
                    probes: [BuildProbe(kind: "not-shipped")],
                    bindings: []
                ),
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "probes[0].kind 'not-shipped' names no registered probe kind."
            );
        } finally {
            WorldProbeVocabularyHook.ProbeKindCheck = previous;
        }
    }
}
