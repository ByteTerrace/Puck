using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private static WorldSocialMemory? RestoreSocialCheckpoint(WorldServerCheckpoint checkpoint, WorldDefinition definition) {
        var policy = definition.StateRaw?.Social;
        if ((policy is null) != (checkpoint.Social is null) ||
            (checkpoint.LastSocialResult != -1 && !Enum.IsDefined((WorldSocialEvidenceResult)checkpoint.LastSocialResult)) ||
            checkpoint.LastSocialResult < -1 || checkpoint.LastSocialResult > byte.MaxValue ||
            (policy is null && checkpoint.LastSocialResult != -1) ||
            (checkpoint.Social is { } state && state.EngineTick != checkpoint.LastCompletedEngineTicks)) {
            throw new InvalidOperationException("social checkpoint policy presence, clock, or outcome is invalid");
        }
        if (policy is null) { return null; }
        try { return WorldSocialMemory.Restore(CompiledWorldSocialPolicy.Compile(policy), checkpoint.Social!); }
        catch (ArgumentException exception) { throw new InvalidOperationException("invalid social memory checkpoint", exception); }
    }
    private WorldSocialMemory? m_social;
    // Borrowed only under ExecuteAuthorityOperation, for the server's transfer coordinator.
    internal WorldSocialMemory? SocialMemory => m_social;
    private WorldSocialPolicy? m_socialSource;
    private ulong m_socialClock;
    private int m_lastSocialResult = -1;

    // Run before any install-side write. A policy replacement creates a new bank, which cannot discard an
    // unresolved source ownership hold or a destination's promised quota. An equal detached policy is harmless.
    private bool CanInstallSocial(WorldDefinition definition, out string reason) {
        reason = string.Empty;
        if (m_social is not { } social || (social.FrozenObserverCount == 0 && social.ImportReservationCount == 0) ||
            ReferenceEquals(definition.StateRaw?.Social, m_socialSource)) { return true; }
        if (definition.StateRaw?.Social is { } source && CompiledWorldSocialPolicy.Compile(source).Identity == social.Policy.Identity) { return true; }
        reason = "social policy cannot change while source ownership holds or import reservations are unresolved";
        return false;
    }

    private void ReconcileSocial(WorldDefinition definition) {
        var source = definition.StateRaw?.Social;
        if (ReferenceEquals(source, m_socialSource)) { return; }
        m_socialSource = source;
        if (source is null) { m_social = null; m_lastSocialResult = -1; return; }
        var policy = CompiledWorldSocialPolicy.Compile(source);
        if (m_social?.Policy.Identity == policy.Identity) { return; }
        m_social = new(policy);
        m_social.Advance(m_socialClock);
        m_lastSocialResult = -1;
    }

    private WorldEntityAddress? ResolveSocialEntity(CompiledWorldSocialEntity reference, ulong tick) => reference.Identity ??
        (reference.Body is { } body ? m_population.ResolveIncarnation(ResolveBodyRef(body, tick), InstanceIdentity) : null);

    private bool TryResolveSocialRelationship(CompiledWorldSocialRelationship relationship, ulong tick, out WorldSocialImpressionKey key) {
        key = default;
        if (ResolveSocialEntity(relationship.Observer, tick) is not { } observer || ResolveSocialEntity(relationship.Subject, tick) is not { } subject) { return false; }
        key = new(observer, subject, relationship.Dimension);
        return true;
    }

    private static long SocialInteger(ulong value) => (long)Math.Min(value, long.MaxValue);

    private long ReadSocialFact(CompiledWorldSocialQuery query, ulong tick) {
        if (m_social is not { } social || !TryResolveSocialRelationship(query.Relationship, tick, out var key) || !social.TryRead(key, out var impression)) { return 0; }
        return query.Facet switch {
            WorldSocialFacet.Value => impression.Value,
            WorldSocialFacet.Confidence => impression.Confidence,
            WorldSocialFacet.Uncertainty => impression.Uncertainty,
            WorldSocialFacet.Weight => impression.Weight,
            WorldSocialFacet.Known => impression.Known ? 1 : 0,
            WorldSocialFacet.EventCount => SocialInteger(impression.IndependentEvents),
            WorldSocialFacet.Age => SocialInteger(impression.AgeTicks),
            _ => 0,
        };
    }

    private void FireSocialEffect(CompiledWorldEffect effect, ulong tick) {
        if (m_social is not { } social) { return; }
        // This is the world's authored program, not an external submitter. Rule authoring is authority-gated;
        // WorldPrincipal.World has structural authority and cannot hold grant-table rows.
        if (effect.SocialRelationship is { } forget) {
            m_lastSocialResult = !TryResolveSocialRelationship(forget, tick, out var key) ? (int)WorldSocialEvidenceResult.Invalid :
                (int)(social.IsObserverFrozen(key.Observer) ? WorldSocialEvidenceResult.ObserverFrozen :
                    social.Forget(key) ? WorldSocialEvidenceResult.Accepted : WorldSocialEvidenceResult.Duplicate);
            return;
        }
        var observation = effect.SocialObservation!;
        var source = observation.Source is { } sourceReference ? ResolveSocialEntity(sourceReference, tick) : null;
        var evidence = default(WorldSocialEvidence);
        if (TryResolveSocialRelationship(observation.Relationship, tick, out var relationship) &&
            ResolveSocialEntity(observation.Origin, tick) is { } origin && (observation.Source is null || source.HasValue) &&
            TryEvaluateExpression(observation.Sequence, CellKind.Int, tick, out var sequence) && sequence >= 0 &&
            TryEvaluateExpression(observation.OccurredAt, CellKind.Int, tick, out var occurredAt) && occurredAt >= 0 &&
            TryEvaluateExpression(observation.Value, CellKind.Fixed, tick, out var value) &&
            TryEvaluateExpression(observation.Quality, CellKind.Fixed, tick, out var quality)) {
            evidence = new(relationship, new(origin, observation.Aspect, (ulong)sequence), (ulong)occurredAt, value, quality, source);
        }
        m_lastSocialResult = (int)social.Observe(evidence);
    }

    /// <summary>Returns the declared social-memory limits and current bounded work, without exposing any individual's beliefs.</summary>
    /// <returns>The social portion of the world.budget cost sheet.</returns>
    public string DescribeSocialBudget() {
        lock (m_authorityGate) {
            return m_social is not { } social ? "social disabled" :
                $"social {social.ImpressionCount}/{social.Policy.ImpressionCapacity} impressions, {social.ReceiptCount}/{social.Policy.ReceiptCapacity} receipts, attempts {social.EvidenceAttempts}/{social.Policy.EvidenceAttemptsPerTick}, expired {social.ReclaimedReceipts}/{social.Policy.ExpiredReceiptsPerTick}, imports {social.ImportReservationCount} groups/{social.ReservedObserverCount} observers holding {social.ReservedImpressionCount} impressions/{social.ReservedReceiptCount} receipts, frozen {social.FrozenObserverCount} observers";
        }
    }

    /// <summary>Operator inspection of social policy or a directed belief under the explicit caller's Observe/all grant.</summary>
    /// <param name="principal">The stamped acting principal.</param>
    /// <param name="query">Optional exact belief query; null reads policy and bounded work only.</param>
    /// <returns>A read-back or named refusal. Reading does not mint mobility identities or alter memory.</returns>
    public string DescribeSocial(WorldPrincipal principal, WorldSocialQuery? query = null) {
        lock (m_authorityGate) {
            if (!m_grants.Allows(principal, WorldCapability.Observe, GrantSubject.All)) { return "[world.social: denied Observe/all]"; }
            if (m_social is not { } social) { return "[world.social: disabled]"; }
            var result = m_lastSocialResult < 0 ? "none" : ((WorldSocialEvidenceResult)m_lastSocialResult).ToString();
            if (query is null) { return $"[world.social: {DescribeSocialBudget()} | last={result} | engineTick={social.EngineTick} | policy={social.Policy.Identity}]"; }
            try {
                var compiled = WorldRuleCompiler.CompileSocialQuery(query, m_definition);
                if (!TryResolveSocialRelationship(compiled.Relationship, m_lastCompletedTick, out var key) || !social.TryRead(key, out var impression)) { return "[world.social: unresolved individual]"; }
                var value = ReadSocialFact(compiled, m_lastCompletedTick);
                var text = compiled.Kind == CellKind.Fixed ? FixedQ4816.FromRawBits(value).ToString() : value.ToString(CultureInfo.InvariantCulture);
                return $"[world.social: {key.Observer} -> {key.Subject} {query.Relationship.Dimension}.{query.Facet}={text} known={impression.Known} events={impression.IndependentEvents} ageTicks={impression.AgeTicks} last={result} frozen={social.IsObserverFrozen(key.Observer)}]";
            } catch (WorldRuleException exception) { return $"[world.social: {exception.Message}]"; }
        }
    }
}
