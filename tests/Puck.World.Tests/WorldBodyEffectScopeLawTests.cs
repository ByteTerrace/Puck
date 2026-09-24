using System.Numerics;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>The four body-motion effects — <c>setVerticalVelocity</c>, <c>scaleVerticalVelocity</c>,
/// <c>planarImpulse</c>, <c>designate</c> — are one operation in a kit's action and in a world rule, checked and lowered
/// by <see cref="WorldBodyEffects"/>: a kit's action acts on its own body and names none, a rule has no body of its own
/// and names one with <c>key</c>, and every number is exact. Each refusal is held beside a control that differs only in
/// the refused member, through the door that reads it — the document validator for a kit, the rule compiler for a
/// rule.</summary>
public sealed class WorldBodyEffectScopeLawTests {
    private const string Register = "focus";

    private static readonly Vector3 Forward = new(
        x: 0f,
        y: 0f,
        z: 1f
    );

    private sealed record Case(ActionEffect Denied, ActionEffect Control, WorldBodyEffectScope Scope, string Mentions);

    private static readonly Dictionary<string, Case> Cases = new(comparer: StringComparer.Ordinal) {
        ["a kit's effect names a body"] = new(
            Control: new WorldEffect.SetVerticalVelocity(Velocity: 5m),
            Denied: new WorldEffect.SetVerticalVelocity(
                Key: "0",
                Velocity: 5m
            ),
            Mentions: "a kit's action acts on its own body",
            Scope: WorldBodyEffectScope.Kit
        ),
        ["a kit's impulse lasts a fraction of an engine tick"] = new(
            Control: new WorldEffect.PlanarImpulse(
                BodyDirection: Forward,
                DurationSeconds: 0.2m,
                Speed: 10m
            ),
            Denied: new WorldEffect.PlanarImpulse(
                BodyDirection: Forward,
                DurationSeconds: 0.00001m,
                Speed: 10m
            ),
            Mentions: "exact whole-engine-tick duration",
            Scope: WorldBodyEffectScope.Kit
        ),
        ["a kit's value leaves the fixed-point range"] = new(
            Control: new WorldEffect.ScaleVerticalVelocity(Factor: 0.45m),
            Denied: new WorldEffect.ScaleVerticalVelocity(Factor: decimal.MaxValue),
            Mentions: "outside the Q48.16 range",
            Scope: WorldBodyEffectScope.Kit
        ),
        ["a rule's effect names no body"] = new(
            Control: new WorldEffect.ScaleVerticalVelocity(
                Factor: 0.5m,
                Key: "0"
            ),
            Denied: new WorldEffect.ScaleVerticalVelocity(Factor: 0.5m),
            Mentions: "a world rule has no body of its own",
            Scope: WorldBodyEffectScope.Rule
        ),
        ["a rule's effect addresses a target"] = new(
            Control: new WorldEffect.SetVerticalVelocity(
                Key: "0",
                Velocity: 5m
            ),
            Denied: new WorldEffect.SetVerticalVelocity(
                Key: "0",
                Target: ActionTarget.ProducerTarget,
                Velocity: 5m
            ),
            Mentions: "carries target 'ProducerTarget'",
            Scope: WorldBodyEffectScope.Rule
        ),
        ["a rule's impulse names no body"] = new(
            Control: new WorldEffect.PlanarImpulse(
                BodyDirection: Forward,
                DurationSeconds: 0.2m,
                Key: "0",
                Speed: 3m
            ),
            Denied: new WorldEffect.PlanarImpulse(
                BodyDirection: Forward,
                DurationSeconds: 0.2m,
                Speed: 3m
            ),
            Mentions: "names no body",
            Scope: WorldBodyEffectScope.Rule
        ),
        ["a rule's designation names no body"] = new(
            Control: new WorldEffect.Designate(
                Key: "0",
                Register: Register,
                TargetKey: "1"
            ),
            Denied: new WorldEffect.Designate(
                Register: Register,
                TargetKey: "1"
            ),
            Mentions: "names no body",
            Scope: WorldBodyEffectScope.Rule
        ),
    };
    // A kit's own designation cannot clear its register or name its subject: it designates the participant that last
    // affected its body. Read through the one check itself, since a kit's program profile decides separately whether
    // it admits a designation at all.
    private static readonly Dictionary<string, Case> KitDesignations = new(comparer: StringComparer.Ordinal) {
        ["a kit's designation clears its register"] = new(
            Control: new WorldEffect.Designate(Register: Register),
            Denied: new WorldEffect.Designate(
                Kind: WorldBodyDesignationKind.Clear,
                Register: Register
            ),
            Mentions: "kind=clear is a world rule's",
            Scope: WorldBodyEffectScope.Kit
        ),
        ["a kit's designation names its subject"] = new(
            Control: new WorldEffect.Designate(Register: Register),
            Denied: new WorldEffect.Designate(
                Register: Register,
                TargetKey: "1"
            ),
            Mentions: "names targetKey '1'",
            Scope: WorldBodyEffectScope.Kit
        ),
        ["a kit's designation names an undeclared register"] = new(
            Control: new WorldEffect.Designate(Register: Register),
            Denied: new WorldEffect.Designate(Register: "elsewhere"),
            Mentions: "names undeclared register 'elsewhere'",
            Scope: WorldBodyEffectScope.Kit
        ),
    };

    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        TargetRegistersRaw = [new WorldTargetRegister(
            MaximumHalfAngleDegrees: 180f,
            MaximumRange: 100f,
            Name: Register,
            RequiresLineOfSight: false
        )],
    };
    // A kit action on the traveller kit's press, the smallest shape that reaches the validator's effect check.
    private static WorldDefinition KitDocument(ActionEffect effect) {
        var document = Document();
        var channel = new WorldChannel(
            Composition: true,
            Name: "dash",
            Shape: ChannelShape.Binary
        );
        var action = new ActionSpec(OnPress: new ActionTrigger(Effects: [effect]));

        return document with {
            ChannelsRaw = [.. document.Channels, channel],
            KitRowsRaw = [document.Kits[0] with { ActionsRaw = new Dictionary<string, ActionSpec> { ["dash"] = action } }],
        };
    }
    private static bool Admits(ActionEffect effect, WorldBodyEffectScope scope, out string reason) {
        if (scope == WorldBodyEffectScope.Kit) {
            return WorldDefinitionValidator.TryValidateLocally(
                definition: KitDocument(effect: effect),
                reason: out reason
            );
        }

        try {
            _ = WorldFactsCompiler.Compile(
                context: WorldFactsCompiler.Context(definition: Document()),
                rule: new WorldRule(
                    Effects: [effect],
                    Name: CellName.Parse(candidate: "motion")
                )
            );
            reason = string.Empty;

            return true;
        } catch (RuleException refusal) {
            reason = refusal.Message;

            return false;
        }
    }
    private static void AssertRefusedBesideItsControl(Case law, Func<ActionEffect, (bool Admitted, string Reason)> admits) {
        var denied = admits(arg: law.Denied);
        var control = admits(arg: law.Control);

        Assert.True(condition: control.Admitted, userMessage: control.Reason);
        Assert.False(condition: denied.Admitted, userMessage: $"the effect was admitted: {law.Denied}");
        Assert.Contains(
            actualString: denied.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: law.Mentions
        );
    }

    public static TheoryData<string> CaseNames() => new(values: Cases.Keys);
    public static TheoryData<string> KitDesignationNames() => new(values: KitDesignations.Keys);
    [MemberData(memberName: nameof(CaseNames))]
    [Theory]
    public void ABodyMotionEffectOutsideItsScopeIsRefusedByName(string name) => AssertRefusedBesideItsControl(
        admits: effect => (Admits(effect: effect, reason: out var reason, scope: Cases[name].Scope), reason),
        law: Cases[name]
    );
    [MemberData(memberName: nameof(KitDesignationNames))]
    [Theory]
    public void AKitDesignationIsItsOwnBodysOfItsAffectingSubject(string name) => AssertRefusedBesideItsControl(
        admits: static effect => (WorldBodyEffects.TryLower(
            effect: effect,
            form: out _,
            reason: out var reason,
            refusal: out _,
            registerDeclared: static register => (register == Register),
            scope: WorldBodyEffectScope.Kit
        ), reason),
        law: KitDesignations[name]
    );
    // One operation: the same effect lowers to the same operands in both scopes, the rule's only addition the body it
    // names, so a kit's jump and a rule's launch cannot drift apart.
    [Fact]
    public void AKitAndARuleLowerOneEffectToTheSameOperands() {
        Assert.True(condition: WorldBodyEffects.TryLower(effect: new WorldEffect.PlanarImpulse(BodyDirection: Forward, DurationSeconds: 0.2m, Speed: 3m), form: out var kit, reason: out var kitReason, refusal: out _, registerDeclared: null, scope: WorldBodyEffectScope.Kit), userMessage: kitReason);
        Assert.True(condition: WorldBodyEffects.TryLower(effect: new WorldEffect.PlanarImpulse(BodyDirection: Forward, DurationSeconds: 0.2m, Key: "0", Speed: 3m), form: out var rule, reason: out var ruleReason, refusal: out _, registerDeclared: null, scope: WorldBodyEffectScope.Rule), userMessage: ruleReason);
        Assert.Equal(expected: kit with { Key = rule.Key }, actual: rule);
    }
}
