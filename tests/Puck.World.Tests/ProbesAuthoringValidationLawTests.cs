using System.Numerics;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Authoring-time laws for the <c>probes</c> section: the kind vocabulary hook, probe/binding cross-
/// references, source uniqueness, and the closed binding-row field ranges.</summary>
public sealed class ProbesAuthoringValidationLawTests {
    private const string ProbeId = "head";
    private const string ProbeKind = "ir-blob";
    private const string ChannelName = "x";

    private static WorldProbe BuildProbe(string id = ProbeId, string kind = ProbeKind) => new(
        Id: id,
        Kind: kind,
        Input: new WorldProbeInput.Camera(Sensor: WorldCameraSensor.Infrared),
        RateHz: 30U
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
}
