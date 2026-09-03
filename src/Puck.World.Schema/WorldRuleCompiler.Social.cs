using Puck.Maths;

namespace Puck.World;

public static partial class WorldRuleCompiler {
    /// <summary>Compiles a standalone read-back query. Per-rule each/left/right bindings are unavailable.</summary>
    /// <param name="query">The authored query.</param>
    /// <param name="definition">The world that declares the social policy.</param>
    /// <returns>The validated query.</returns>
    /// <exception cref="WorldRuleException">A reference, dimension, facet, or policy is invalid.</exception>
    public static CompiledWorldSocialQuery CompileSocialQuery(WorldSocialQuery query, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(definition);
        var bindings = s_bindingScope;
        var cachedPolicy = s_socialPolicy;
        s_bindingScope = null;
        s_socialPolicy = null;
        try { return ResolveSocialQuery(query, "world.social", definition); }
        finally { s_bindingScope = bindings; s_socialPolicy = cachedPolicy; }
    }

    // Scoped to one rule compilation, never retained across edits to authored policy lists.
    [ThreadStatic]
    private static CompiledWorldSocialPolicy? s_socialPolicy;

    private static CompiledWorldSocialPolicy RequireSocialPolicy(string ruleName, WorldDefinition definition) {
        try {
            if (definition.StateRaw?.Social is not { } policy) { throw new ArgumentException("requires a state.social policy"); }
            return s_socialPolicy ??= CompiledWorldSocialPolicy.Compile(policy);
        } catch (ArgumentException exception) {
            throw new WorldRuleException(WorldRuleRefusal.EffectKindInadmissible, ruleName, $"social: {exception.Message}");
        }
    }

    private static CompiledWorldSocialEntity ResolveSocialEntity(WorldSocialEntityReference? reference, string ruleName, WorldDefinition definition) {
        WorldRuleException Refuse() => new(WorldRuleRefusal.SpatialChannelMalformed, ruleName, "social entity requires exactly one valid body reference or stable identity");
        if (reference is null || (reference.Body is null) == (reference.Identity is null)) { throw Refuse(); }
        if (reference.Identity is { } identity) {
            if (string.IsNullOrWhiteSpace(identity.Authority) || identity.Authority.Length > 512 || identity.Index < 0 || identity.Generation < 0) { throw Refuse(); }
            return new(null, identity);
        }
        var tokens = reference.Body!.Split(':');
        if (tokens.Length != BodyRefTokenWidth(tokens, 0)) { throw Refuse(); }
        return new(ResolveBodyRefToken(tokens, 0, ruleName, definition, "social entity"), null);
    }

    private static CompiledWorldSocialRelationship ResolveSocialRelationship(WorldSocialRelationship? relationship, string ruleName, WorldDefinition definition) {
        var policy = RequireSocialPolicy(ruleName, definition);
        var dimension = relationship?.Dimension is { } name ? policy.FindDimension(name) : -1;
        if (dimension < 0) { throw new WorldRuleException(WorldRuleRefusal.StateRowUnknown, ruleName, "social relationship names no declared dimension"); }
        return new(ResolveSocialEntity(relationship!.Observer, ruleName, definition), ResolveSocialEntity(relationship.Subject, ruleName, definition), dimension);
    }

    private static CompiledWorldSocialQuery ResolveSocialQuery(WorldSocialQuery? query, string ruleName, WorldDefinition definition) {
        if (query is null || !Enum.IsDefined(query.Facet)) { throw new WorldRuleException(WorldRuleRefusal.EffectKindInadmissible, ruleName, "invalid social query facet"); }
        return new(ResolveSocialRelationship(query.Relationship, ruleName, definition), query.Facet,
            query.Facet is WorldSocialFacet.Known or WorldSocialFacet.EventCount or WorldSocialFacet.Age ? CellKind.Int : CellKind.Fixed);
    }

    private static CompiledWorldEffect ResolveSocialObservation(WorldSocialObservation? observation, string ruleName, WorldDefinition definition) {
        if (observation is null || string.IsNullOrWhiteSpace(observation.Aspect) || observation.Aspect.Length > 64) {
            throw new WorldRuleException(WorldRuleRefusal.EffectKindInadmissible, ruleName, "social observation requires an aspect of 1..64 characters");
        }
        var compiled = new CompiledWorldSocialObservation(
            ResolveSocialRelationship(observation.Relationship, ruleName, definition), ResolveSocialEntity(observation.Origin, ruleName, definition), observation.Aspect,
            CompileExpression(observation.Sequence, CellKind.Int, ruleName, "social sequence", definition),
            CompileExpression(observation.OccurredAt, CellKind.Int, ruleName, "social occurredAt", definition),
            CompileExpression(observation.Value, CellKind.Fixed, ruleName, "social value", definition),
            observation.Quality is null ? [new(WorldExpressionOp.Constant, FixedQ4816.One.Value)] : CompileExpression(observation.Quality, CellKind.Fixed, ruleName, "social quality", definition),
            observation.Source is null ? null : ResolveSocialEntity(observation.Source, ruleName, definition));
        return new(WorldRuleEffectKind.ObserveSocial, string.Empty, string.Empty, default, 0, null,
            $"observeSocial {observation.Relationship.Dimension}/{observation.Aspect}", SocialObservation: compiled);
    }
}
