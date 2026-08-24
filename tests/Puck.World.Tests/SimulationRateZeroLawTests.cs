using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Proves the rate-0 "never" duration model (the owner's ruling: a world whose <c>simulation.rateHz</c> is 0 is a
/// legal, resident, non-stepping world, and a SIMULATION-tick duration authored as a POSITIVE value at that rate
/// means NEVER — not zero and not "already expired"). Four separate load-bearing facts, each proved directly
/// against its own seam rather than only through the whole-document validator:
/// <list type="bullet">
/// <item><description><see cref="WorldDefinitionValidator"/> admits <c>rateHz 0</c> and still refuses a negative
/// rate (<see cref="ValidatorAdmitsRateZero_RefusesNegativeRate"/>).</description></item>
/// <item><description><see cref="WorldSimulationTickConversion.SecondsFromTicks"/> refuses to divide by a zero rate
/// rather than producing a non-finite float (<see cref="SecondsFromTicks_RefusesAtRateZero"/>).</description></item>
/// <item><description><see cref="WorldSimulationTickConversion.CompiledDuration"/> and
/// <see cref="WorldDefinition.PopulationReconnectGraceTicks"/> distinguish NEVER (a positive authored duration at
/// rate 0) from an authored-DISABLED zero (unaffected by the rate) — the type this change
/// introduces.</description></item>
/// <item><description><see cref="WorldPopulation.DeactivateSeat"/> — the load-bearing request-time consumer — parks
/// a disconnecting seat FOREVER at rate 0 instead of tearing it down immediately, while a positive-rate world's
/// existing grace behavior is unchanged (<see cref="DeactivateSeat_AtRateZero_ParksForever"/>,
/// <see cref="DeactivateSeat_AtPositiveRate_BehaviorUnchanged"/>).</description></item>
/// </list>
/// A document authoring an ordinary <c>inputHold</c> section (the fixture's own, unmodified) must ALSO still
/// validate at rate 0 — <see cref="RateZeroDocument_WithOrdinaryInputHold_StillValidates"/> is the end-to-end proof
/// that moving that check to the AUTHORED (seconds) domain actually closed the false-refusal gap a compiled-ticks
/// check would otherwise reopen on every rate-0 world.
/// </summary>
public sealed class SimulationRateZeroLawTests {
    [Fact]
    public void ValidatorAdmitsRateZero_RefusesNegativeRate() {
        var zeroRate = (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 0) });
        var negativeRate = (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: -1) });

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: zeroRate, neighbours: null, reason: out var zeroReason), userMessage: $"rate 0 was expected to validate; refused: {zeroReason}");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: negativeRate, neighbours: null, reason: out var negativeReason), userMessage: "a negative rate was expected to refuse");
        Assert.Contains(actualString: negativeReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "rateHz");
    }
    [Fact]
    public void ValidatorAtRateZero_SkipsTheDivisorCheck() {
        // 0 does not divide 50400 (nothing does, in the ordinary sense) — if ValidateSimulation applied the SAME
        // exact-divisor check to a zero rate that it applies to a positive one, rate 0 would be refused for
        // "not a divisor" rather than admitted as the owner's distinct, legal rate. Admission alone (the law above)
        // could pass by accident if the divisor check happened to tolerate 0 mathematically; this asserts the
        // discriminating fact directly: 50400 % 0 is undefined, so the ONLY way rate 0 validates is a genuine early
        // return, not a coincidence of modulo arithmetic.
        var zeroRate = (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 0) });

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: zeroRate, neighbours: null, reason: out var reason), userMessage: $"rate 0 was expected to validate; refused: {reason}");
    }
    /// <summary>The discriminator this suite's own test audit named missing: nothing above proves the divisor check
    /// still FIRES for a positive rate — <see cref="ValidatorAtRateZero_SkipsTheDivisorCheck"/> and
    /// <see cref="ValidatorAdmitsRateZero_RefusesNegativeRate"/> both only ever exercise 0 and -1, so a change that
    /// accidentally admitted EVERY rate (deleting the divisor check outright, not merely special-casing 0) would
    /// pass both. 241 Hz does not divide 50400.</summary>
    [Fact]
    public void ValidatorAtPositiveRate_RefusesNonDivisorRate() {
        var nonDivisor = (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 241) });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: nonDivisor, neighbours: null, reason: out var reason), userMessage: "241 Hz does not divide 50400 and was expected to refuse");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "does not divide");
    }
    [Fact]
    public void RateZeroDocument_WithOrdinaryInputHold_StillValidates() {
        // The fixture's own inputHold section (ceilingSeconds 0.5, lowerAfterSeconds 0.25, defaultSeconds 0) is
        // ordinary, unremarkable authored content — every shipped world carries something in this shape. Before
        // this change, WorldDefinitionValidator validated the COMPILED ticks form: at rate 0 every compiled *Ticks
        // field collapses to 0 (WorldSimulationTickConversion.DurationTicks' own contract), and lowerAfterTicks < 1
        // fired on EVERY rate-0 world regardless of what was authored. This is the end-to-end proof that moving the
        // check to the authored (seconds) domain actually closed that false-refusal gap, not merely that
        // ValidateSimulation itself admits the rate.
        var zeroRate = (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 0) });

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: zeroRate, neighbours: null, reason: out var reason), userMessage: $"a rate-0 world authoring an ordinary inputHold section was expected to validate; refused: {reason}");
    }
    /// <summary>The discriminator <see cref="RateZeroDocument_WithOrdinaryInputHold_StillValidates"/> itself cannot
    /// provide: that test would pass identically if input-hold validation were deleted wholesale, since it only ever
    /// asserts SUCCESS. This proves the section is genuinely still checked at rate 0 — and proves it rate-
    /// independently, per the adversarial review's finding 5: a positive lowerAfterSeconds that quantizes to
    /// FixedQ4816.Zero is sub-representable as a duration at ANY rate, including 0 (where there is no compiled tick
    /// count to overflow at all — the failure is purely in the authored-domain quantization, before any rate is
    /// even consulted).</summary>
    [Fact]
    public void RateZeroDocument_SubRepresentableLowerAfterSeconds_Refuses() {
        var baseDocument = Fixtures.BuildDocument();
        var document = (baseDocument with {
            Simulation = new WorldSimulationDefaults(RateHz: 0),
            InputHoldRaw = (baseDocument.InputHold with { LowerAfterSeconds = 0.000001f }),
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a sub-representable positive lowerAfterSeconds was expected to refuse even at rate 0");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "lowerAfterSeconds");
    }
    /// <summary>The other half of the same discriminator: a non-finite authored value evades every ordered
    /// comparison the old floats-only validator made (<c>NaN &gt; anything</c> and <c>anything &gt; NaN</c> are both
    /// <see langword="false"/>), so it must be refused by an explicit finiteness check, not inferred from a
    /// comparison outcome. Proved at rate 0 specifically: at rate 0 there is no compiled tick count for a bad value
    /// to overflow, so ONLY an explicit finiteness check catches this here.</summary>
    [Fact]
    public void RateZeroDocument_NonFiniteCeilingSeconds_Refuses() {
        var baseDocument = Fixtures.BuildDocument();
        var document = (baseDocument with {
            Simulation = new WorldSimulationDefaults(RateHz: 0),
            InputHoldRaw = (baseDocument.InputHold with { CeilingSeconds = float.NaN }),
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a NaN ceilingSeconds was expected to refuse even at rate 0");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "finite");
    }
    /// <summary>The adversarial review's finding 3, with its own concrete numbers: at 240 Hz, ceilingSeconds
    /// 10,000,000 compiles to 2,400,000,000 ticks — which does not fit <see cref="WorldInputHoldSettings"/>.
    /// <c>CeilingTicks</c>' <see cref="int"/> field. The pre-fix seconds-domain validator compared floats only and
    /// admitted this (lowerAfterSeconds positive, defaultSeconds within the — enormous — ceiling), so
    /// <see cref="WorldInputHoldAuthoring.Compile"/>'s <c>checked((int)...)</c> cast threw
    /// <see cref="OverflowException"/> for real, past the door meant to catch exactly this. Proves both halves: the
    /// validator now refuses it by name, and the premise — Compile really would have thrown — still holds.</summary>
    [Fact]
    public void PositiveRateDocument_CeilingSecondsOverflowsCompiledRange_Refuses() {
        var baseDocument = Fixtures.BuildDocument();
        var document = (baseDocument with {
            InputHoldRaw = (baseDocument.InputHold with { CeilingSeconds = 10_000_000f, LowerAfterSeconds = 0.25f, DefaultSeconds = 0f }),
        });

        Assert.Equal(expected: 240, actual: document.SimulationRateHz);
        Assert.Throws<OverflowException>(testCode: () => document.InputHold.Compile(ratePerSecond: ((uint)document.SimulationRateHz)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "an uncompilable ceilingSeconds was expected to refuse");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "ceilingSeconds");
    }
    /// <summary>The adversarial review's finding 5, at the positive rate its own concrete case names: 240 Hz,
    /// lowerAfterSeconds 0.000001f. The comment above <c>WorldDefinitionValidator.ValidateInputHold</c> used to
    /// claim every positive lowerAfterSeconds compiles to at least one tick — false, because the float overload
    /// quantizes through Q48.16 BEFORE the tick math runs, and this value rounds to fixed-point zero first. The old
    /// (pre-seconds-domain) <c>LowerAfterTicks &lt; 1</c> check caught this; the bare <c>&gt; 0f</c> seconds check
    /// did not.</summary>
    [Fact]
    public void PositiveRateDocument_SubRepresentableLowerAfterSeconds_Refuses() {
        var baseDocument = Fixtures.BuildDocument();
        var document = (baseDocument with {
            InputHoldRaw = (baseDocument.InputHold with { LowerAfterSeconds = 0.000001f }),
        });

        Assert.Equal(expected: 240, actual: document.SimulationRateHz);
        Assert.Equal(expected: 0, actual: document.InputHold.Compile(ratePerSecond: ((uint)document.SimulationRateHz)).LowerAfterTicks);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a sub-representable positive lowerAfterSeconds was expected to refuse at 240 Hz");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "lowerAfterSeconds");
    }
    [Fact]
    public void SecondsFromTicks_RefusesAtRateZero() {
        Assert.Throws<InvalidOperationException>(testCode: () => WorldSimulationTickConversion.SecondsFromTicks(ratePerSecond: 0U, ticks: 720));
        // The zero-ticks case matters too: a plain division would produce NaN here (not Infinity), and both are
        // non-finite floats that later throw unguarded out of JSON serialization — the refusal must fire on EITHER
        // shape, not just the more obviously-wrong Infinity case.
        Assert.Throws<InvalidOperationException>(testCode: () => WorldSimulationTickConversion.SecondsFromTicks(ratePerSecond: 0U, ticks: 0));
    }
    /// <summary>The apply-door discriminator this suite's own test audit named missing:
    /// <see cref="SecondsFromTicks_RefusesAtRateZero"/> proves the LOW-LEVEL conversion refuses, but not that a real
    /// caller reaching it through the ordinary submission pipeline gets a normal refusal rather than an unhandled
    /// exception out of the dispatcher. <see cref="WorldMutation.SetInputHold"/> carries raw ticks — the
    /// addon-mutation ABI's own contract — and decompiles them through <c>ToAuthoring</c> →
    /// <c>SecondsFromTicks</c> on apply. A rate-0 world never <c>Step</c>s, but
    /// <see cref="WorldServer.DrainAdministrative"/> still applies buffered mutations (the documented rate-0
    /// self-lock follow-on), so this IS a reachable path. Asserts BOTH halves: no exception escapes, AND the
    /// mutation was genuinely refused (the live document is byte-identical before/after) rather than silently
    /// swallowed as a no-op success.</summary>
    [Fact]
    public void SetInputHold_RawTicks_AtRateZero_RefusesThroughAdministrativeDrain_NoException() {
        using var fixture = Fixtures.FreshServer(definition: (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 0) }));
        var before = fixture.DefinitionBytes();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetInputHold(
            Principal: WorldPrincipal.Console,
            Settings: new WorldInputHoldSettings(CeilingTicks: 120, DefaultTicks: 0, EqualizeByDefault: true, LowerAfterTicks: 60, Participants: [])
        ));

        var exception = Record.Exception(testCode: () => fixture.Server.DrainAdministrative());
        var after = fixture.DefinitionBytes();

        Assert.Null(@object: exception);
        Assert.True(condition: before.AsSpan().SequenceEqual(other: after), userMessage: "a raw-ticks SetInputHold at rate 0 was expected to be refused (document left unchanged), not silently applied");
    }
    [Fact]
    public void SecondsFromTicks_WorksAtPositiveRate() {
        Assert.Equal(expected: 3.0f, actual: WorldSimulationTickConversion.SecondsFromTicks(ratePerSecond: 240U, ticks: 720));
    }
    [Fact]
    public void CompiledDuration_AtRateZero_PositiveSecondsIsNever_ZeroSecondsIsAuthoredDisabled() {
        var never = WorldSimulationTickConversion.CompiledDuration(ratePerSecond: 0U, seconds: 3.0f);
        var disabled = WorldSimulationTickConversion.CompiledDuration(ratePerSecond: 0U, seconds: 0f);

        Assert.True(condition: never.IsNever);
        Assert.False(condition: disabled.IsNever);
        Assert.True(condition: disabled.IsZero);
        // The whole point of the type: Never and an authored-disabled zero are NOT the same value, and Ticks on
        // Never is not merely "some number that happens to be 0" — it throws, so no caller can silently read it as
        // a tick count.
        Assert.NotEqual(actual: disabled, expected: never);
        Assert.Throws<InvalidOperationException>(testCode: () => never.Ticks);
        Assert.Equal(expected: 0, actual: disabled.Ticks);
    }
    [Fact]
    public void CompiledDuration_AtPositiveRate_MatchesDurationTicks() {
        var compiled = WorldSimulationTickConversion.CompiledDuration(ratePerSecond: 240U, seconds: 3.0f);

        Assert.False(condition: compiled.IsNever);
        Assert.Equal(expected: checked((int)WorldSimulationTickConversion.DurationTicks(ratePerSecond: 240U, seconds: 3.0f)), actual: compiled.Ticks);
        Assert.Equal(expected: 720, actual: compiled.Ticks);
    }
    [Fact]
    public void PopulationReconnectGraceTicks_AtRateZero_ReflectsNeverAndDisabled() {
        var neverDocument = BuildWithRateAndGrace(rateHz: 0, reconnectGraceSeconds: 3.0f);
        var disabledDocument = BuildWithRateAndGrace(rateHz: 0, reconnectGraceSeconds: 0f);

        Assert.True(condition: neverDocument.PopulationReconnectGraceTicks.IsNever);
        Assert.False(condition: disabledDocument.PopulationReconnectGraceTicks.IsNever);
        Assert.True(condition: disabledDocument.PopulationReconnectGraceTicks.IsZero);
    }
    [Fact]
    public void PopulationReconnectGraceTicks_AtPositiveRate_IsFiniteAndUnchanged() {
        var document = BuildWithRateAndGrace(rateHz: 240, reconnectGraceSeconds: 3.0f);
        var compiled = document.PopulationReconnectGraceTicks;

        Assert.False(condition: compiled.IsNever);
        Assert.Equal(expected: 720, actual: compiled.Ticks);
    }
    /// <summary>THE load-bearing law: a seat that disconnects in a rate-0 world parks FOREVER — never torn down —
    /// rather than the pre-park immediate-teardown behavior a stale <c>&lt;= 0</c> read on the compiled grace would
    /// produce (compiled ticks collapse to 0 at rate 0 for ANY positive authored grace, which the old shape could
    /// not distinguish from an authored-disabled 0). Even a huge tick advance through
    /// <see cref="WorldPopulation.ReclaimExpiredParks"/> must not reclaim it.
    /// <para>The suite's own test audit named this test's original shape (asserting only <c>int.MaxValue</c> at the
    /// starting tick, then sweeping to 1,000,000,000) as passing a WRONG implementation too — one that stamped a
    /// finite <c>start + int.MaxValue</c> deadline instead of actually storing <see langword="null"/>, since
    /// sweeping to a mere billion would still read as parked either way. <see cref="WorldPopulation.ParkedRemainingTicks"/>
    /// now returns the honest <see cref="long"/>? shape — <see langword="null"/> for forever, never a finite
    /// stand-in — so the assertion below IS the direct discriminator: it reads the entry's OWN deadline state (null
    /// vs. a huge-but-finite number), not merely whether a sweep happened to fall short of it.</para></summary>
    [Fact]
    public void DeactivateSeat_AtRateZero_ParksForever() {
        var document = BuildWithRateAndGrace(rateHz: 0, reconnectGraceSeconds: 3.0f);
        var population = new WorldPopulation(definition: document);

        population.ActivateSeat(profile: null, slot: 0);
        population.DeactivateSeat(slot: 0, tick: 100UL);

        Assert.True(condition: population.IsActive(index: 0), userMessage: "a parked seat must stay Active (still IsHumanOccupied)");
        Assert.True(condition: population.IsSeatParked(slot: 0), userMessage: "a rate-0 disconnect must park, never tear down immediately");
        Assert.Null(@object: population.ParkedRemainingTicks(index: 0, tick: 100UL));

        // A huge tick advance — nothing about ReclaimExpiredParks may ever reclaim a NEVER park while the world
        // stays at rate 0 (the compiled grace itself stays NEVER — see ReclaimExpiredParks' own "Revival re-stamp"
        // remarks for the ONE condition, a rate revival through Rebuild, that would end this).
        population.ReclaimExpiredParks(tick: 1_000_000_000UL);

        Assert.True(condition: population.IsActive(index: 0));
        Assert.True(condition: population.IsSeatParked(slot: 0));
        Assert.Null(@object: population.ParkedRemainingTicks(index: 0, tick: 1_000_000_000UL));
    }
    /// <summary>The regression control for <see cref="DeactivateSeat_AtRateZero_ParksForever"/>: a positive-rate
    /// world's existing park-with-grace behavior (finite deadline, reclaimed once the grace window passes) must be
    /// byte-for-byte unchanged by introducing <see cref="CompiledTickDuration"/>.</summary>
    [Fact]
    public void DeactivateSeat_AtPositiveRate_BehaviorUnchanged() {
        var document = BuildWithRateAndGrace(rateHz: 240, reconnectGraceSeconds: 3.0f);
        var population = new WorldPopulation(definition: document);

        population.ActivateSeat(profile: null, slot: 0);
        population.DeactivateSeat(slot: 0, tick: 0UL);

        Assert.True(condition: population.IsSeatParked(slot: 0));
        Assert.Equal(expected: ((long?)720L), actual: population.ParkedRemainingTicks(index: 0, tick: 0UL));

        population.ReclaimExpiredParks(tick: 719UL);
        Assert.True(condition: population.IsSeatParked(slot: 0), userMessage: "the grace window has not elapsed yet");

        population.ReclaimExpiredParks(tick: 720UL);
        Assert.False(condition: population.IsActive(index: 0), userMessage: "the grace window elapsed; the body must be torn down");
        Assert.False(condition: population.IsSeatParked(slot: 0));
    }
    /// <summary>An authored-DISABLED grace (0 seconds) keeps the immediate-teardown behavior at ANY rate, including
    /// 0 — disabled is a real, distinct meaning from NEVER, not the same zero read two ways.</summary>
    [Fact]
    public void DeactivateSeat_AuthoredDisabledGrace_TearsDownImmediately_AtRateZero() {
        var document = BuildWithRateAndGrace(rateHz: 0, reconnectGraceSeconds: 0f);
        var population = new WorldPopulation(definition: document);

        population.ActivateSeat(profile: null, slot: 0);
        population.DeactivateSeat(slot: 0, tick: 100UL);

        Assert.False(condition: population.IsActive(index: 0));
        Assert.False(condition: population.IsSeatParked(slot: 0));
    }
    /// <summary><see cref="WorldPopulation.ApplyPeerDisconnected"/> shares the identical NEVER/authored-disabled/
    /// finite branch shape as <see cref="DeactivateSeat_AtRateZero_ParksForever"/> — proved directly here (a REAL
    /// remote-admitted peer, via <see cref="WorldPopulation.TryAdmitRemotePeer"/>, not a hand-poked field) rather
    /// than only inferred from reading the two methods side by side. Carries the same null-deadline discriminator as
    /// the seat law — see that test's own remarks for why a bare sweep-to-a-billion assertion is not enough on its
    /// own.</summary>
    [Fact]
    public void ApplyPeerDisconnected_AtRateZero_ParksForever() {
        var document = BuildWithRateGraceAndPeerCapacity(rateHz: 0, reconnectGraceSeconds: 3.0f);
        var population = new WorldPopulation(definition: document);

        Assert.True(condition: population.TryAdmitRemotePeer(source: IntentSource.Idle, grantTemplates: [], identityDomain: string.Empty, identitySubject: string.Empty, admitted: out var admitted, refusal: out var refusal), userMessage: $"peer admission was expected to succeed; refused: {refusal}");

        population.ApplyPeerDisconnected(peer: admitted, tick: 100UL);

        Assert.True(condition: population.IsActive(index: admitted.BodyIndex));
        Assert.True(condition: population.IsParked(index: admitted.BodyIndex));
        Assert.Null(@object: population.ParkedRemainingTicks(index: admitted.BodyIndex, tick: 100UL));

        population.ReclaimExpiredParks(tick: 1_000_000_000UL);

        Assert.True(condition: population.IsActive(index: admitted.BodyIndex));
        Assert.True(condition: population.IsParked(index: admitted.BodyIndex));
        Assert.Null(@object: population.ParkedRemainingTicks(index: admitted.BodyIndex, tick: 1_000_000_000UL));
    }
    /// <summary>The regression control for <see cref="ApplyPeerDisconnected_AtRateZero_ParksForever"/>.</summary>
    [Fact]
    public void ApplyPeerDisconnected_AtPositiveRate_BehaviorUnchanged() {
        var document = BuildWithRateGraceAndPeerCapacity(rateHz: 240, reconnectGraceSeconds: 3.0f);
        var population = new WorldPopulation(definition: document);

        Assert.True(condition: population.TryAdmitRemotePeer(source: IntentSource.Idle, grantTemplates: [], identityDomain: string.Empty, identitySubject: string.Empty, admitted: out var admitted, refusal: out var refusal), userMessage: $"peer admission was expected to succeed; refused: {refusal}");

        population.ApplyPeerDisconnected(peer: admitted, tick: 0UL);

        Assert.True(condition: population.IsParked(index: admitted.BodyIndex));
        Assert.Equal(expected: ((long?)720L), actual: population.ParkedRemainingTicks(index: admitted.BodyIndex, tick: 0UL));

        population.ReclaimExpiredParks(tick: 720UL);

        Assert.False(condition: population.IsActive(index: admitted.BodyIndex));
        Assert.False(condition: population.IsParked(index: admitted.BodyIndex));
    }
    /// <summary>The peer-path counterpart of <see cref="DeactivateSeat_AuthoredDisabledGrace_TearsDownImmediately_AtRateZero"/>
    /// — the review's own test audit named this gap explicitly ("no ... peer-path authored-zero grace test"). An
    /// authored-DISABLED grace (0 seconds) tears a disconnecting peer down immediately at rate 0 too, exactly as at
    /// any positive rate — disabled is a real, distinct meaning from NEVER, not the same zero read two ways.</summary>
    [Fact]
    public void ApplyPeerDisconnected_AuthoredDisabledGrace_TearsDownImmediately_AtRateZero() {
        var document = BuildWithRateGraceAndPeerCapacity(rateHz: 0, reconnectGraceSeconds: 0f);
        var population = new WorldPopulation(definition: document);

        Assert.True(condition: population.TryAdmitRemotePeer(source: IntentSource.Idle, grantTemplates: [], identityDomain: string.Empty, identitySubject: string.Empty, admitted: out var admitted, refusal: out var refusal), userMessage: $"peer admission was expected to succeed; refused: {refusal}");

        population.ApplyPeerDisconnected(peer: admitted, tick: 100UL);

        Assert.False(condition: population.IsActive(index: admitted.BodyIndex));
        Assert.False(condition: population.IsParked(index: admitted.BodyIndex));
    }
    /// <summary>Finding A's regression test — the adversarial review's own words: "the highest-risk lifecycle: park
    /// at rate 0, rebuild/reload at 240 Hz, then reclaim/admit a replacement." Before the fix,
    /// <see cref="WorldPopulation.Rebuild"/> recompiled the grace table but never walked live entries, so a
    /// <see langword="null"/> deadline stayed <see langword="null"/> forever even after revival — the seat/peer slot
    /// was permanently unreclaimable, and <see cref="WorldPopulation.TryAdmitRemotePeer"/>'s own admission-cap
    /// arithmetic (<c>CountActiveCensus</c>) counts a parked-but-<c>Active</c> entry as occupied, so a few such
    /// disconnects could exhaust the world's usable population.
    /// <para>The fix re-derives rather than strands: the FIRST <see cref="WorldPopulation.ReclaimExpiredParks"/>
    /// sweep after a revival (a rate-0 world never runs that method at all, so this really is the first
    /// opportunity) drops the null deadline and re-stamps it as <c>revival tick + the newly compiled grace</c> — the
    /// visitor's window restarts at revival, never insta-torn-down on the very sweep that revives it.</para></summary>
    [Fact]
    public void ParkedForever_RevivedAtPositiveRate_ReclaimsAndAdmitsReplacement() {
        var zeroRateDocument = BuildWithRateGraceAndPeerCapacity(rateHz: 0, reconnectGraceSeconds: 3.0f);
        var population = new WorldPopulation(definition: zeroRateDocument);

        Assert.True(condition: population.TryAdmitRemotePeer(source: IntentSource.Idle, grantTemplates: [], identityDomain: string.Empty, identitySubject: string.Empty, admitted: out var admitted, refusal: out var refusal), userMessage: $"peer admission was expected to succeed; refused: {refusal}");

        population.ApplyPeerDisconnected(peer: admitted, tick: 100UL);

        Assert.Null(@object: population.ParkedRemainingTicks(index: admitted.BodyIndex, tick: 100UL));

        // A rate-0 world never steps, so ReclaimExpiredParks never runs here — the park sits untouched until a
        // whole-document reload revives the world. 240 Hz, 3 authored grace seconds => 720 compiled ticks.
        var revivedDocument = (zeroRateDocument with { Simulation = new WorldSimulationDefaults(RateHz: 240) });

        population.Rebuild(definition: revivedDocument, solids: null);

        // The FIRST sweep after revival, at tick 500: must re-derive a real deadline (500 + 720 = 1220), and must
        // NOT tear the entry down on this same tick.
        population.ReclaimExpiredParks(tick: 500UL);

        Assert.True(condition: population.IsActive(index: admitted.BodyIndex), userMessage: "the revival sweep must not tear the entry down on the same tick it gets a fresh deadline");
        Assert.True(condition: population.IsParked(index: admitted.BodyIndex));
        Assert.Equal(expected: ((long?)720L), actual: population.ParkedRemainingTicks(index: admitted.BodyIndex, tick: 500UL));

        // Short of the re-derived deadline (500 + 720 - 1 = 1219): still parked.
        population.ReclaimExpiredParks(tick: 1219UL);
        Assert.True(condition: population.IsParked(index: admitted.BodyIndex), userMessage: "the re-derived grace window has not elapsed yet");

        // At the re-derived deadline: reclaimed.
        population.ReclaimExpiredParks(tick: 1220UL);
        Assert.False(condition: population.IsActive(index: admitted.BodyIndex));
        Assert.False(condition: population.IsParked(index: admitted.BodyIndex));

        // And the freed slot admits a replacement peer — the population is not permanently exhausted.
        Assert.True(condition: population.TryAdmitRemotePeer(source: IntentSource.Idle, grantTemplates: [], identityDomain: string.Empty, identitySubject: string.Empty, admitted: out var replacement, refusal: out var replacementRefusal), userMessage: $"a replacement peer was expected to be admitted into the reclaimed slot; refused: {replacementRefusal}");
        Assert.Equal(expected: admitted.BodyIndex, actual: replacement.BodyIndex);
    }

    private static WorldDefinition BuildWithRateGraceAndPeerCapacity(int rateHz, float reconnectGraceSeconds) {
        var document = BuildWithRateAndGrace(rateHz: rateHz, reconnectGraceSeconds: reconnectGraceSeconds);

        // One free peer slot above the four local seats, with room in networkPlayers to admit it — the fixture's own
        // default document pins Capacity to LocalSeatCount specifically to keep the census/inhabitant loop empty
        // (see Fixtures.BuildDocumentCore's own remarks); a peer-path law needs that loop non-empty instead.
        return (document with { PopulationRaw = (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1), NetworkPlayers = 1 }) });
    }
    private static WorldDefinition BuildWithRateAndGrace(int rateHz, float reconnectGraceSeconds) {
        var document = Fixtures.BuildDocument();

        return (document with {
            Simulation = new WorldSimulationDefaults(RateHz: rateHz),
            PopulationRaw = (document.Population with { ReconnectGraceSeconds = reconnectGraceSeconds }),
        });
    }
}
