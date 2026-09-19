using System.Globalization;
using Puck.Physics.Motion;
using IRuleOperand = Puck.State.Rules.IRuleOperand;
using OperandFamily = Puck.State.Rules.OperandFamily;
using OperandSite = Puck.State.Rules.OperandSite;
using RuleCompileContext = Puck.State.Rules.RuleCompileContext;
using RuleCompiler = Puck.State.Rules.RuleCompiler;
using RuleRefusal = Puck.State.Rules.RuleRefusal;

namespace Puck.World;

public static partial class WorldFactsVocabulary {
    private sealed class WorldFactsOperandFamily : OperandFamily {
        private const string BoardCellOfPrefix = "$board:cellOf:";

        public override IReadOnlyList<string> Spellings { get; } = [
            WorldRuleFacts.Population,
            WorldRuleFacts.PhysicsQuiescent,
            $"{WorldRuleFacts.RegionPrefix}<placementId>",
            $"{WorldRuleFacts.InfluencePrefix}<channel>:<placementId>",
            $"{WorldRuleFacts.MachinePrefix}<screen>:<address>",
            $"{WorldRuleFacts.ArgMaxPrefix}<row>",
            $"{WorldRuleFacts.ArgMinPrefix}<row>",
            $"{WorldRuleFacts.DistancePrefix}<a>:<b>",
            $"{WorldRuleFacts.LineOfSightPrefix}<a>:<b>",
            $"{WorldRuleFacts.UprightPrefix}<bodyRef>",
            $"{WorldRuleFacts.FactPrefix}<bodyRef>:<fact>",
            $"{WorldRuleFacts.IdentityPrefix}<bodyRef>:<fact>",
            $"{WorldRuleFacts.NavigationPrefix}<bodyRef>:<facet>",
            $"{WorldRuleFacts.ParkedPrefix}<bodyRef>",
            $"{WorldRuleFacts.LinkPrefix}<adjacencyName>",
            $"{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>",
            $"{WorldRuleFacts.NearestPrefix}<bodyRef>:<row>",
            $"{WorldRuleFacts.ClockPrefix}<music>:phaseError",
            $"{BoardCellOfPrefix}<row>:<bodyRef>",
        ];

        private static WorldArgBodyOperand ArgBody(string name, string? key, in OperandSite site, WorldFactsCompileContext world) {
            var ruleName = site.RuleName;

            RuleCompiler.RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: site.KeyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            const string WhereMarker = ":where:";

            var isMax = name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ArgMaxPrefix
            );
            var rowAndFilter = name[(isMax
                ? WorldRuleFacts.ArgMaxPrefix.Length
                : WorldRuleFacts.ArgMinPrefix.Length)..];
            var where = rowAndFilter.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: WhereMarker
            );
            var filterRowName = ((where < 0)
                ? null
                : rowAndFilter[(where + WhereMarker.Length)..]
            );
            var rowName = ((where < 0)
                ? rowAndFilter
                : rowAndFilter[..where]
            );

            if (
                string.IsNullOrEmpty(value: rowName) ||
                ((where >= 0) && string.IsNullOrEmpty(value: filterRowName))
            ) {
                throw new RuleException(
                    detail: $"'{name}' does not spell '{(isMax
                    ? WorldRuleFacts.ArgMaxPrefix
                    : WorldRuleFacts.ArgMinPrefix)}<row>'",
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName
                );
            }

            _ = RuleCompiler.ResolveNumericRow(
                channel: name,
                context: world,
                malformed: WorldRuleRefusal.SpatialChannelMalformed,
                name: rowName,
                requireKeyed: true,
                ruleName: ruleName
            );

            var filterOrdinal = -1;
            StateHandle filterHandle = default;

            if (filterRowName is not null) {
                _ = RuleCompiler.ResolveNumericRow(
                    channel: name,
                    context: world,
                    malformed: WorldRuleRefusal.SpatialChannelMalformed,
                    name: filterRowName,
                    requireKeyed: true,
                    ruleName: ruleName
                );
                filterOrdinal = RuleCompiler.ResolveRowOrdinal(
                    context: world,
                    name: filterRowName
                );
                filterHandle = world.Catalog.Descriptors[filterOrdinal].Handle;
            }

            var rowOrdinal = RuleCompiler.ResolveRowOrdinal(
                context: world,
                name: rowName
            );
            var reduce = (isMax
                ? StateReduceOp.Max
                : StateReduceOp.Min
            );

            return new WorldArgBodyOperand(
                filterOrdinal: filterOrdinal,
                operand: new ArgBodyOperand(
                    filterHandle: filterHandle,
                    filterRow: filterRowName,
                    reduce: reduce,
                    row: rowName,
                    stateHandle: world.Catalog.Descriptors[rowOrdinal].Handle
                ),
                reduce: reduce,
                rowOrdinal: rowOrdinal
            );
        }
        private static WorldBodyFactOperand BodyFact(CellKind valueKind, WorldFactsCompileContext world, CompiledBodyRef bodyA, CompiledBodyRef bodyB, int rowOrdinal, long cost, Func<IWorldFacts, RuleFact> read, string describe) => new(
            bodyA: world.Ordinals(body: in bodyA),
            bodyB: world.Ordinals(body: in bodyB),
            cost: cost,
            describe: describe,
            read: read,
            rowOrdinal: rowOrdinal,
            valueKind: valueKind
        );

        public override bool TryCompile(string name, string? key, in OperandSite site, RuleCompileContext context, out IRuleOperand? fact) {
            ArgumentNullException.ThrowIfNull(argument: name);

            var ruleName = site.RuleName;
            var world = ((WorldFactsCompileContext)context);

            fact = null;
            if (string.Equals(
                a: name,
                b: WorldRuleFacts.Population,
                comparisonType: StringComparison.Ordinal
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
                fact = WorldPopulationOperand.Instance;
            } else if (string.Equals(
                a: name,
                b: WorldRuleFacts.PhysicsQuiescent,
                comparisonType: StringComparison.Ordinal
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
                fact = WorldPhysicsQuiescentOperand.Instance;
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.InfluencePrefix
            )) {
                var parts = name[WorldRuleFacts.InfluencePrefix.Length..].Split(separator: ':');

                if (
                    (parts.Length != 2) ||
                    !CellName.TryParse(
                    candidate: parts[0],
                    name: out _,
                    reason: out _
                ) ||
                    (WorldDefinitionRows.FindPlacement(
                    id: parts[1],
                    placements: world.Definition.Placements
                ) is not { } target)
                ) {
                    throw new RuleException(
                        detail: $"'{name}' must spell '{WorldRuleFacts.InfluencePrefix}<channel>:<placementId>' with a declared placement",
                        refusal: WorldRuleRefusal.SpatialChannelMalformed,
                        ruleName: ruleName
                    );
                }

                Puck.State.Rules.CompiledCellRef? keyFrom = null;

                if (key is not null) {
                    if (target.Deal is null) {
                        throw new RuleException(
                            detail: $"'{name}' accepts a child key only for a deal template",
                            refusal: WorldRuleRefusal.SpatialChannelMalformed,
                            ruleName: ruleName
                        );
                    }
                    if (RuleCompiler.TryResolveDynamicKey(
                        cell: out var dynamicKey,
                        context: context,
                        key: key,
                        keyFieldLabel: site.KeyFieldLabel,
                        ruleName: ruleName,
                        verb: site.Verb
                    )) {
                        keyFrom = dynamicKey;
                    } else if (!CellName.TryParse(
                        candidate: key,
                        name: out _,
                        reason: out _
                    )) {
                        throw new RuleException(
                            detail: $"'{name}' has an invalid child key",
                            refusal: WorldRuleRefusal.SpatialChannelMalformed,
                            ruleName: ruleName
                        );
                    }
                }

                fact = new WorldPlacementInfluenceOperand(
                    channel: parts[0],
                    childKey: ((keyFrom is null)
                    ? key
                    : null),
                    keyFrom: keyFrom,
                    placementId: parts[1]
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.RegionPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var placementId = name[WorldRuleFacts.RegionPrefix.Length..];

                if (
                    string.IsNullOrEmpty(value: placementId) ||
                    !world.HasRegion(placementId: placementId)
                ) {
                    throw new RuleException(
                        detail: $"'{name}' names no placement carrying a region facet",
                        refusal: WorldRuleRefusal.RegionUnknown,
                        ruleName: ruleName
                    );
                }

                fact = new WorldRegionOccupancyOperand(placementId: placementId);
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.MachinePrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var suffix = name[WorldRuleFacts.MachinePrefix.Length..];
                var separator = suffix.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    value: ':'
                );

                if (
                    (separator < 0) ||
                    !int.TryParse(
                    s: suffix[..separator],
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var screen
                ) ||
                    !int.TryParse(
                    s: suffix[(separator + 1)..],
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var address
                ) ||
                    (screen < 0) ||
                    (address < 0)
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.MachinePrefix}<screen>:<address>' with non-negative integers",
                        refusal: WorldRuleRefusal.MachineChannelMalformed,
                        ruleName: ruleName
                    );
                }
                if (!world.HasScreen(index: screen)) {
                    throw new RuleException(
                        detail: $"'{name}' names screen {screen}, which the document does not declare",
                        refusal: WorldRuleRefusal.ScreenUnknown,
                        ruleName: ruleName
                    );
                }

                fact = new WorldMachineMemoryOperand(
                    address: address,
                    screen: screen
                );
            } else if (
                name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ArgMaxPrefix
            ) ||
                name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ArgMinPrefix
            )
            ) {
                fact = ArgBody(
                    key: key,
                    name: name,
                    site: in site,
                    world: world
                );
            } else if (
                name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.DistancePrefix
            ) ||
                name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.LineOfSightPrefix
            )
            ) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var isDistance = name.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: WorldRuleFacts.DistancePrefix
                );
                var tokens = name[(isDistance
                    ? WorldRuleFacts.DistancePrefix.Length
                    : WorldRuleFacts.LineOfSightPrefix.Length)..].Split(separator: ':');
                var widthA = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (tokens.Length != (widthA + WorldFactsCompileContext.BodyRefTokenWidth(
                    start: widthA,
                    tokens: tokens
                ))) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{(isDistance
                        ? WorldRuleFacts.DistancePrefix
                        : WorldRuleFacts.LineOfSightPrefix)}<bodyRefA>:<bodyRefB>' (each {WorldFactsCompileContext.BodyRefVocabulary})",
                        refusal: WorldRuleRefusal.SpatialChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var bodyA = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var bodyB = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: widthA,
                    tokens: tokens
                );

                if (isDistance) {
                    var operand = new BodyDistanceOperand(
                        bodyA: bodyA,
                        bodyB: bodyB
                    );

                    fact = BodyFact(
                        bodyA: bodyA,
                        bodyB: bodyB,
                        cost: 1L,
                        describe: name,
                        read: facet => facet.Read(operand: operand),
                        rowOrdinal: -1,
                        valueKind: CellKind.Fixed,
                        world: world
                    );
                } else {
                    var operand = new LineOfSightOperand(
                        bodyA: bodyA,
                        bodyB: bodyB
                    );

                    fact = BodyFact(
                        bodyA: bodyA,
                        bodyB: bodyB,
                        cost: WorldRuleCapacity.SightTestWork,
                        describe: name,
                        read: facet => facet.Read(operand: operand),
                        rowOrdinal: -1,
                        valueKind: CellKind.Bool,
                        world: world
                    );
                }
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.UprightPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = name[WorldRuleFacts.UprightPrefix.Length..].Split(separator: ':');

                if (tokens.Length != WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                )) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.UprightPrefix}<bodyRef>' ({WorldFactsCompileContext.BodyRefVocabulary})",
                        refusal: WorldRuleRefusal.SpatialChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new UprightOperand(bodyA: body);

                fact = BodyFact(
                    bodyA: body,
                    bodyB: default,
                    cost: 1L,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: -1,
                    valueKind: CellKind.Fixed,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.FactPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = name[WorldRuleFacts.FactPrefix.Length..].Split(separator: ':');
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (
                    (tokens.Length != (width + 1)) ||
                    !BodyFactVocabulary.TryResolve(
                    gate: out var factBit,
                    name: tokens[width]
                ) ||
                    (factBit == BodyFacts.None)
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.FactPrefix}<bodyRef>:<fact>' ({WorldFactsCompileContext.BodyRefVocabulary}, then a BodyFacts name)",
                        refusal: WorldRuleRefusal.BodyFactMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new BodyFactOperand(
                    body: body,
                    fact: factBit
                );

                fact = BodyFact(
                    bodyA: body,
                    bodyB: default,
                    cost: 1L,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: -1,
                    valueKind: CellKind.Int,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.IdentityPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = name[WorldRuleFacts.IdentityPrefix.Length..].Split(separator: ':');
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (
                    (tokens.Length != (width + 1)) ||
                    !CellName.TryParse(
                    candidate: tokens[width],
                    name: out _,
                    reason: out _
                )
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.IdentityPrefix}<bodyRef>:<fact>' ({WorldFactsCompileContext.BodyRefVocabulary}, then a fact key)",
                        refusal: WorldRuleRefusal.IdentityFactMalformed,
                        ruleName: ruleName
                    );
                }

                fact = new WorldIdentityFactOperand(
                    body: world.ResolveBodyRef(
                        channel: name,
                        ruleName: ruleName,
                        start: 0,
                        tokens: tokens
                    ),
                    capacity: world.Definition.Population.Capacity,
                    fact: tokens[width],
                    laneOrdinal: WorldFactsCompiler.ResolveIdentityLane(
                        context: world,
                        ruleName: ruleName,
                        where: $"'{name}'"
                    )
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ParkedPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = name[WorldRuleFacts.ParkedPrefix.Length..].Split(separator: ':');

                if (tokens.Length != 2) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.ParkedPrefix}<bodyRef>' (a 'body:<n>' or 'argmax:<row>'/'argmin:<row>' pair)",
                        refusal: WorldRuleRefusal.ParkedChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new ParkedOperand(bodyA: body);

                fact = BodyFact(
                    bodyA: body,
                    bodyB: default,
                    cost: 1L,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: -1,
                    valueKind: CellKind.Int,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ChannelPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var seats = world.Definition.Population.LocalSeats;
                var suffix = name[WorldRuleFacts.ChannelPrefix.Length..];
                var separator = suffix.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    value: ':'
                );

                if (
                    (separator < 0) ||
                    !int.TryParse(
                    s: suffix[..separator],
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var seat
                ) ||
                    (seat < 1) ||
                    (seat > seats) ||
                    string.IsNullOrEmpty(value: suffix[(separator + 1)..])
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>' with seat in 1..{seats}",
                        refusal: WorldRuleRefusal.ChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var channelName = suffix[(separator + 1)..];
                var channelOrdinal = world.ChannelOrdinal(name: channelName);

                if (channelOrdinal < 0) {
                    throw new RuleException(
                        detail: $"'{name}' names channel '{channelName}', which the document does not declare in 'channels[]'",
                        refusal: WorldRuleRefusal.ChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var operand = new ChannelOperand(
                    channelOrdinal: channelOrdinal,
                    seat: (seat - 1)
                );

                fact = BodyFact(
                    bodyA: default,
                    bodyB: default,
                    cost: 1L,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: -1,
                    valueKind: CellKind.Fixed,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.LinkPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var adjacencyName = name[WorldRuleFacts.LinkPrefix.Length..];

                if (
                    string.IsNullOrEmpty(value: adjacencyName) ||
                    adjacencyName.Contains(value: ':') ||
                    (WorldDefinitionRows.FindAdjacency(
                    adjacencies: world.Definition.Adjacencies,
                    name: adjacencyName
                ) is null)
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.LinkPrefix}<adjacencyName>' naming a declared 'adjacencies' row",
                        refusal: WorldRuleRefusal.LinkChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var operand = new LinkStalenessOperand(adjacencyName: adjacencyName);

                fact = BodyFact(
                    bodyA: default,
                    bodyB: default,
                    cost: 1L,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: -1,
                    valueKind: CellKind.Int,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.NavigationPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = name[WorldRuleFacts.NavigationPrefix.Length..].Split(separator: ':');
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (
                    (tokens.Length != (width + 1)) ||
                    (tokens[width] is not ("hasPath" or "active" or "arrived" or "unreachable" or "remaining" or "pending" or "capacity"))
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.NavigationPrefix}<bodyRef>:<hasPath|active|arrived|unreachable|remaining|pending|capacity>' ({WorldFactsCompileContext.BodyRefVocabulary})",
                        refusal: WorldRuleRefusal.SpatialChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new NavigationOperand(
                    bodyA: body,
                    facet: tokens[width]
                );

                fact = BodyFact(
                    bodyA: body,
                    bodyB: default,
                    cost: 1L,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: -1,
                    valueKind: CellKind.Int,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.NearestPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: key,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = name[WorldRuleFacts.NearestPrefix.Length..].Split(separator: ':');
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (tokens.Length != (width + 1)) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.NearestPrefix}<bodyRef>:<row>' ({WorldFactsCompileContext.BodyRefVocabulary}, then the keyed tag row)",
                        refusal: WorldRuleRefusal.SpatialChannelMalformed,
                        ruleName: ruleName
                    );
                }

                var from = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );

                _ = RuleCompiler.ResolveNumericRow(
                    channel: name,
                    context: world,
                    malformed: WorldRuleRefusal.SpatialChannelMalformed,
                    name: tokens[width],
                    requireKeyed: true,
                    ruleName: ruleName
                );

                var rowOrdinal = RuleCompiler.ResolveRowOrdinal(
                    context: world,
                    name: tokens[width]
                );
                var operand = new NearestOperand(
                    bodyA: from,
                    row: tokens[width],
                    stateHandle: world.Catalog.Descriptors[rowOrdinal].Handle
                );

                fact = BodyFact(
                    bodyA: from,
                    bodyB: default,
                    cost: world.Definition.Population.Capacity,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: rowOrdinal,
                    valueKind: CellKind.Int,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ClockPrefix
            )) {
                var tokens = name.Split(separator: ':');

                if (
                    (tokens.Length != 3) ||
                    (tokens[2] != "phaseError")
                ) {
                    throw new RuleException(
                        detail: "clock read requires $clock:<music>:phaseError",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }
                if (key is not null) {
                    throw new RuleException(
                        detail: "phaseError does not accept key",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }
                if (
                    (world.Definition.Music is not { Count: > 0 } music) ||
                    (music[0]?.Name != tokens[1])
                ) {
                    throw new RuleException(
                        detail: $"'{tokens[1]}' does not name the document's declared music row",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                fact = WorldClockOperand.Instance;
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: BoardCellOfPrefix
            )) {
                if (key is not null) {
                    throw new RuleException(
                        detail: "cellOf does not accept key",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                var tokens = name[BoardCellOfPrefix.Length..].Split(separator: ':');
                var row = world.FindRow(name: tokens[0]);

                if (
                    (row?.EffectiveDomain is not StateDomain.CellsOf board) ||
                    (world.FindTopology(name: board.Topology) is not { } topology)
                ) {
                    throw new RuleException(
                        detail: $"'{tokens[0]}' names no discrete board row",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                var bodyTokens = tokens[1..];

                if (bodyTokens.Length != WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: bodyTokens
                )) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{BoardCellOfPrefix}<row>:<bodyRef>' ({WorldFactsCompileContext.BodyRefVocabulary})",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: bodyTokens
                );
                var operand = new BoardCellOfOperand(
                    bodyA: body,
                    row: row.Name.Value,
                    topology: topology
                );

                fact = BodyFact(
                    bodyA: body,
                    bodyB: default,
                    cost: topology.CellCount,
                    describe: name,
                    read: facet => facet.Read(operand: operand),
                    rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                        context: world,
                        name: row.Name.Value
                    ),
                    valueKind: CellKind.Int,
                    world: world
                );
            }

            return (fact is not null);
        }
    }
}
