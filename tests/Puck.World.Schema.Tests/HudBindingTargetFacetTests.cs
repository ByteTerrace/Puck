using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="HudBindingVocabulary.TryParse"/>'s trailing <c>.$target</c> facet
/// (<see cref="HudBinding.Target"/>) — the closed grammar the HUD binding resolver and
/// <c>WorldDefinitionValidator</c>'s existence check both parse against.
/// </summary>
public sealed class HudBindingTargetFacetTests {
    [Fact]
    public void KeyedTokenWithTargetFacet_ParsesTheRowAndKeyWithTargetSet() {
        Assert.True(condition: HudBindingVocabulary.TryParse(binding: out var parsed, token: "state.hp.0.$target"));
        Assert.Equal(actual: parsed.Kind, expected: HudBindingKind.StateNamed);
        Assert.Equal(actual: parsed.StateName, expected: "hp");
        Assert.Equal(actual: parsed.StateCellKey, expected: "0");
        Assert.True(condition: parsed.Target);
    }
    [Fact]
    public void SlotTokenWithTargetFacet_ParsesTheRowAloneWithTargetSet() {
        Assert.True(condition: HudBindingVocabulary.TryParse(binding: out var parsed, token: "state.hp.$target"));
        Assert.Equal(actual: parsed.Kind, expected: HudBindingKind.StateNamed);
        Assert.Equal(actual: parsed.StateName, expected: "hp");
        Assert.Null(@object: parsed.StateCellKey);
        Assert.True(condition: parsed.Target);
    }
    // "target" with no leading '$' is an ordinary author-chosen cell key, not the reserved facet — the control that
    // proves the facet is spelled '.$target', never bare '.target'.
    [Fact]
    public void KeyedTokenNamedTargetWithoutTheDollarSign_ParsesAsAnOrdinaryKeyWithTargetUnset() {
        Assert.True(condition: HudBindingVocabulary.TryParse(binding: out var parsed, token: "state.hp.target"));
        Assert.Equal(actual: parsed.Kind, expected: HudBindingKind.StateNamed);
        Assert.Equal(actual: parsed.StateName, expected: "hp");
        Assert.Equal(actual: parsed.StateCellKey, expected: "target");
        Assert.False(condition: parsed.Target);
    }
    [Fact]
    public void TargetFacetWithAnEmptyRow_Refuses() {
        Assert.False(condition: HudBindingVocabulary.TryParse(binding: out _, token: "state.$target"));
    }
    [Fact]
    public void PlainTokenWithNoFacet_ParsesWithTargetUnset() {
        Assert.True(condition: HudBindingVocabulary.TryParse(binding: out var parsed, token: "state.hp"));
        Assert.False(condition: parsed.Target);
    }
}
