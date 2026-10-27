using System.Globalization;
using Puck.Commands;
using Puck.Physics.Motion;
using IRuleOperand = Puck.State.Rules.IRuleOperand;
using OperandFamily = Puck.State.Rules.OperandFamily;
using OperandSite = Puck.State.Rules.OperandSite;
using RuleCompileContext = Puck.State.Rules.RuleCompileContext;
using RuleCompiler = Puck.State.Rules.RuleCompiler;

namespace Puck.World;

public static partial class WorldFactsVocabulary {
    /// <summary>Resolves the same admitted owner spelling for event reads and sweeps.</summary>
    public static WorldInputOwnerReference CompileInputOwner(string text, WorldFactsCompileContext world, string ruleName) => WorldFactsOperandFamily.CompileInputOwner(text, world, ruleName);
    private sealed class WorldFactsOperandFamily : OperandFamily {
        private const string BoardCellOfPrefix = "$board:cellOf:";
        private const string BoardEngagedPrefix = "$board:engaged:";
        private const string BoardPointerPrefix = "$board:pointer:";
        private const string BoardSidePrefix = "$board:side:";

        public override IReadOnlyList<string> Spellings { get; } = [
            "$inputMatch:<owner>:<pattern>",
            "$inputMatchFirst:<owner>:<pattern>",
            "$inputMatchLast:<owner>:<pattern>",
            "$inputMatchFirstTime:<owner>:<pattern>",
            "$inputMatchLastTime:<owner>:<pattern>",
            "$inputMatchFirstTick:<owner>:<pattern>",
            "$inputMatchLastTick:<owner>:<pattern>",
            "$inputEdge:<owner>:<channel>:<kind>:<occurrence>",
            "$inputTime:<owner>:<channel>:<kind>:<occurrence>",
            "$inputAge:<owner>:<channel>:<kind>:<occurrence>",
            "$inputValue:<owner>:<channel>:<kind>:<occurrence>",
            "$inputTick:<owner>:<channel>:<kind>:<occurrence>",
            "$inputHeldSince:<owner>:<channel>",
            "$inputHeldPress:<owner>:<channel>",
            WorldRuleFacts.Population,
            WorldRuleFacts.PhysicsQuiescent,
            $"{WorldRuleFacts.PhysicsQuiescent}:<scope>",
            $"{WorldRuleFacts.RegionPrefix}<placementId>",
            $"{WorldRuleFacts.InfluencePrefix}<channel>:<placementId>",
            $"{WorldRuleFacts.MachinePrefix}<screen>:<address>",
            $"{WorldRuleFacts.ArgMaxPrefix}<row>",
            $"{WorldRuleFacts.ArgMinPrefix}<row>",
            $"{WorldRuleFacts.DistancePrefix}<a>:<b>",
            $"{WorldRuleFacts.LineOfSightPrefix}<a>:<b>",
            $"{WorldRuleFacts.UprightPrefix}<bodyRef>",
            $"{WorldRuleFacts.FactPrefix}<bodyRef>:<fact>",
            $"{WorldRuleFacts.BodyPositionPrefix}<bodyRef>:x|y|z",
            $"{WorldRuleFacts.EngagedPrefix}<bodyRef>:<screen>",
            $"{WorldRuleFacts.BodyStatePrefix}<bodyRef>:<slot>",
            $"{WorldRuleFacts.IdentityPrefix}<bodyRef>:<fact>",
            $"{WorldRuleFacts.NavigationPrefix}<bodyRef>:<facet>",
            $"{WorldRuleFacts.ParkedPrefix}<bodyRef>",
            $"{WorldRuleFacts.LinkPrefix}<adjacencyName>",
            $"{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>",
            $"{WorldRuleFacts.PointerPrefix}<seat>|any:<screenIndex>:x|y|on|press:<channelName>",
            $"{WorldRuleFacts.NearestPrefix}<bodyRef>:<row>",
            $"{WorldRuleFacts.ClockPrefix}<music>:phaseError",
            $"{WorldRuleFacts.ClockPrefix}<music>:phaseErrorAt:<owner>:<channel>:<kind>:<occurrence>",
            $"{BoardCellOfPrefix}<row>:<bodyRef>",
            "$board:engaged|pointer|side:<row>:<seat>",
        ];

        private static WorldArgBodyOperand ArgBody(string name, StateChannelRef? cell, in OperandSite site, WorldFactsCompileContext world) {
            var ruleName = site.RuleName;

            RuleCompiler.RefuseKeyOnReservedChannel(
                key: cell,
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
        // $pointer:<seat>:<screenIndex>:x|y|on — the seat bounded like $channel's, the screen a declared Simulation row
        // whose source-normalized mapping validates, so the tick maps a ray without a refusal left to raise.
        private static WorldBodyFactOperand Pointer(string name, string ruleName, WorldFactsCompileContext world) {
            var seats = world.Definition.Population.LocalSeats;
            var parts = name[WorldRuleFacts.PointerPrefix.Length..].Split(separator: ':');
            var pointerFacet = (parts.Length switch {
                3 => parts[2] switch {
                    "x" => PointerFacet.X,
                    "y" => PointerFacet.Y,
                    "on" => PointerFacet.On,
                    _ => ((PointerFacet?)null),
                },
                4 when (parts[2] == "press") => PointerFacet.Press,
                _ => null,
            });
            // "any" is every participant allowed to point here, seats first and then live sessions; a number is one
            // seat, 1-based.
            var seat = PointerOperand.AnyParticipant;
            var participant = (
                (parts.Length > 0) &&
                ((parts[0] == "any") ||
                (int.TryParse(
                s: parts[0],
                style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture,
                result: out seat
            ) &&
                (seat >= 1) &&
                (seat <= seats)))
            );

            if (
                (pointerFacet is null) ||
                !participant ||
                !int.TryParse(
                s: parts[1],
                style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture,
                result: out var screenIndex
            )
            ) {
                throw new RuleException(
                    detail: $"'{name}' does not spell '{WorldRuleFacts.PointerPrefix}<seat>|any:<screenIndex>:x|y|on|press:<channelName>' with seat in 1..{seats}",
                    refusal: WorldRuleRefusal.PointerMalformed,
                    ruleName: ruleName
                );
            }

            var pressChannel = -1;

            if (pointerFacet == PointerFacet.Press) {
                pressChannel = world.ChannelOrdinal(name: parts[3]);

                if (pressChannel < 0) {
                    throw new RuleException(
                        detail: $"'{name}' names channel '{parts[3]}', which the document does not declare in 'channels[]'",
                        refusal: WorldRuleRefusal.PointerMalformed,
                        ruleName: ruleName
                    );
                }
            }

            WorldScreen? screen = null;

            foreach (var candidate in world.Definition.Screens) {
                if (candidate.Index == screenIndex) {
                    screen = candidate;

                    break;
                }
            }

            if (screen is null) {
                throw new RuleException(
                    detail: $"'{name}' names screen {screenIndex}, which the document does not declare",
                    refusal: WorldRuleRefusal.ScreenUnknown,
                    ruleName: ruleName
                );
            }
            if (screen.Route.Input != SourceDestination.Simulation) {
                throw new RuleException(
                    detail: $"'{name}' names screen {screenIndex}, whose route input is not Simulation, so no pointer ray maps through it",
                    refusal: WorldRuleRefusal.PointerMalformed,
                    ruleName: ruleName
                );
            }

            var mapping = WorldScreenMappings.Normalized(screen: screen);

            if (!mapping.TryValidate(refusal: out var refusal)) {
                throw new RuleException(
                    detail: $"'{name}' names screen {screenIndex}, whose row maps no ray: {refusal}",
                    refusal: WorldRuleRefusal.PointerMalformed,
                    ruleName: ruleName
                );
            }

            var operand = new PointerOperand(
                facet: pointerFacet.Value,
                mapping: mapping,
                pressChannel: pressChannel,
                screenIndex: screenIndex,
                seat: ((seat == PointerOperand.AnyParticipant)
                    ? PointerOperand.AnyParticipant
                    : (seat - 1))
            );

            return BodyFact(
                bodyA: default,
                bodyB: default,
                cost: 1L,
                describe: name,
                read: facet => facet.Read(operand: operand),
                rowOrdinal: -1,
                valueKind: operand.ValueKind,
                world: world
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

        private static WorldInputOperand CompileInput(ChannelCall call, WorldFactsCompileContext world, string ruleName) {
            var facet = call.Channel switch {
                "inputEdge" => WorldInputFacet.Edge,
                "inputTime" => WorldInputFacet.Time,
                "inputAge" => WorldInputFacet.Age,
                "inputValue" => WorldInputFacet.Value,
                "inputTick" => WorldInputFacet.Tick,
                "inputHeldSince" => WorldInputFacet.HeldSince,
                "inputMatch" => WorldInputFacet.Match,
                "inputMatchFirst" => WorldInputFacet.MatchFirst,
                "inputMatchLast" => WorldInputFacet.MatchLast,
                "inputMatchFirstTime" => WorldInputFacet.MatchFirstTime,
                "inputMatchLastTime" => WorldInputFacet.MatchLastTime,
                "inputMatchFirstTick" => WorldInputFacet.MatchFirstTick,
                "inputMatchLastTick" => WorldInputFacet.MatchLastTick,
                _ => WorldInputFacet.HeldPress,
            };
            var matching = facet >= WorldInputFacet.Match;
            var held = matching || facet is WorldInputFacet.HeldSince or WorldInputFacet.HeldPress;
            RuleException Invalid(string detail) => new(detail: $"{call.Channel}: {detail}", refusal: WorldRuleRefusal.ChannelMalformed, ruleName: ruleName);
            var ownerArguments = call.Count - (held ? 1 : 3);
            if (ownerArguments < 1) { throw Invalid(held ? "expected owner and channel" : "expected owner, channel, event kind and occurrence"); }
            var owner = CompileInputOwner(string.Join(':', call.Arguments.Take(ownerArguments).Select(static argument => argument.Spelling)), world, ruleName);
            if (matching) { return new(owner, 0, facet, default, 0, CompileInputPattern(call.Text(ownerArguments), world, ruleName)); }
            var catalog = WorldInputCatalog.For(world.Definition);
            if (!catalog.TryGetLogicalId(call.Text(ownerArguments), out var logical)) { throw Invalid($"unknown channel '{call.Text(ownerArguments)}'"); }
            var kind = WorldInputEventKind.Press;
            var occurrence = 0;
            if (!held) {
                if (!Enum.TryParse(call.Text(ownerArguments + 1), false, out kind) || !Enum.IsDefined(kind)) { throw Invalid("unknown event kind"); }
                if (call.Text(ownerArguments + 2) is "each" or "$each") {
                    if (world.Iteration is not WorldInputIteration iteration || iteration.Owner != owner) { throw Invalid("each requires inputEvents of the same owner"); }
                    occurrence = -1;
                } else if (!int.TryParse(call.Text(ownerArguments + 2), NumberStyles.None, CultureInfo.InvariantCulture, out occurrence) || occurrence < 0 ||
                    occurrence >= WorldInputCatalog.For(world.Definition).HistoryCapacity(world.Definition.InputHistory?.EventCapacity ?? 256)) { throw Invalid("occurrence outside retained capacity"); }
            }
            var shape = catalog.Channels[logical].Shape;
            if ((held || kind is WorldInputEventKind.Press or WorldInputEventKind.Release) && shape != Protocol.ChannelShape.Binary ||
                kind == WorldInputEventKind.Text && shape != Protocol.ChannelShape.Text) { throw Invalid($"event kind does not match channel '{call.Text(ownerArguments)}' shape {shape}"); }
            return new(owner, logical, facet, kind, occurrence, fixedValue: facet == WorldInputFacet.Value && shape is Protocol.ChannelShape.Bipolar or Protocol.ChannelShape.Unipolar);
        }

        public static WorldInputOwnerReference CompileInputOwner(string text, WorldFactsCompileContext world, string ruleName) {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seat) && seat >= 1 && seat <= world.Definition.Population.LocalSeats) { return new(Principal.Seat(seat - 1)); }
            var identity = text.Split(':');
            if (identity.Length == 3 && identity[0] == "session" &&
                int.TryParse(identity[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) &&
                int.TryParse(identity[2], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && ordinal >= 0 && epoch > 0) {
                return new(Principal.Session(ordinal, epoch));
            }
            var count = identity[0] switch { "cell" => 3, "body" or "argmax" or "argmin" or "placement" => 2, _ => 1 };
            if (identity.Length != count) { throw new RuleException(WorldRuleRefusal.ChannelMalformed, ruleName, "input owner has an invalid typed reference"); }
            return new(default, world.ResolveBodyRef(identity, 0, ruleName, "input owner"));
        }
        public static int CompileInputPattern(string name, WorldFactsCompileContext world, string ruleName) {
            var rows = world.Definition.InputPatterns ?? [];
            for (var index = 0; index < rows.Count; index++) { if (rows[index].Name == name) { return index; } }
            throw new RuleException(WorldRuleRefusal.ChannelMalformed, ruleName, $"unknown input pattern '{name}'");
        }

        public override bool TryCompile(StateChannelRef reference, StateChannelRef? cell, in OperandSite site, RuleCompileContext context, out IRuleOperand? fact) {
            ArgumentNullException.ThrowIfNull(argument: reference);

            var ruleName = site.RuleName;
            var world = ((WorldFactsCompileContext)context);
            // Every branch below takes its arguments from the call; the spellings are what a refusal quotes.
            var call = reference.Call;
            var key = cell?.Spelling;
            var name = reference.Spelling;

            fact = null;
            if (call is { Channel: "inputEdge" or "inputTime" or "inputAge" or "inputValue" or "inputTick" or "inputHeldSince" or "inputHeldPress" or
                "inputMatch" or "inputMatchFirst" or "inputMatchLast" or "inputMatchFirstTime" or "inputMatchLastTime" or "inputMatchFirstTick" or "inputMatchLastTick" }) {
                RuleCompiler.RefuseKeyOnReservedChannel(cell, site.KeyFieldLabel, name, ruleName);
                var input = CompileInput(call, world, ruleName);
                var capacity = world.Definition.InputHistory?.EventCapacity ?? 256;
                fact = BodyFact(input.ValueKind, world, input.Owner.Body ?? default, default, -1,
                    input.Pattern < 0 ? WorldInputCatalog.For(world.Definition).HistoryCapacity(capacity) : WorldInputCatalog.For(world.Definition).PatternWork(input.Pattern, capacity), facet => facet.Read(input), name);
                return true;
            }
            if (string.Equals(
                a: name,
                b: WorldRuleFacts.Population,
                comparisonType: StringComparison.Ordinal
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
                fact = WorldPopulationOperand.Instance;
            } else if ((name == WorldRuleFacts.PhysicsQuiescent) || name.StartsWith(comparisonType: StringComparison.Ordinal, value: (WorldRuleFacts.PhysicsQuiescent + ":"))) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
                if (name == WorldRuleFacts.PhysicsQuiescent) { fact = WorldPhysicsQuiescentOperand.Instance; } else {
                    var scopeName = name[(WorldRuleFacts.PhysicsQuiescent.Length + 1)..];
                    var scope = world.Definition.ReplaysRaw?.FirstOrDefault(predicate: scope => (scope.Name == scopeName));

                    if (scope is null) {
                        throw new RuleException(detail: $"'{name}' names no replay scope '{scopeName}'", refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName);
                    }
                    fact = new WorldPhysicsQuiescentOperand(bodies: scope.Bodies);
                }
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.InfluencePrefix
            )) {
                var parts = call!.Texts();

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
                        reference: cell,
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
                    key: cell,
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
                    key: cell,
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
                    cell: cell,
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
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var isDistance = name.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: WorldRuleFacts.DistancePrefix
                );
                var tokens = call!.Texts();
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
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();

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
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
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
                value: WorldRuleFacts.BodyPositionPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );
                var axis = ((tokens.Length == (width + 1))
                    ? tokens[width] switch {
                        "x" => 0,
                        "y" => 1,
                        "z" => 2,
                        _ => -1,
                    }
                    : -1
                );

                if (axis < 0) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.BodyPositionPrefix}<bodyRef>:<axis>' ({WorldFactsCompileContext.BodyRefVocabulary}, then x, y or z)",
                        refusal: WorldRuleRefusal.BodyPositionMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new BodyPositionOperand(
                    axis: axis,
                    body: body
                );

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
                value: WorldRuleFacts.EngagedPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (
                    (tokens.Length != (width + 1)) ||
                    !int.TryParse(
                    s: tokens[width],
                    style: NumberStyles.None,
                    provider: CultureInfo.InvariantCulture,
                    result: out var screen
                ) ||
                    !DeclaresScreen(
                    definition: world.Definition,
                    index: screen
                )
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.EngagedPrefix}<bodyRef>:<screen>' ({WorldFactsCompileContext.BodyRefVocabulary}, then the index of a declared screen)",
                        refusal: WorldRuleRefusal.EngagedMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new EngagedOperand(
                    body: body,
                    screen: screen
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
                value: WorldRuleFacts.BodyStatePrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
                var width = WorldFactsCompileContext.BodyRefTokenWidth(
                    start: 0,
                    tokens: tokens
                );

                if (
                    (tokens.Length != (width + 1)) ||
                    !WorldBodyStateReads.TryResolveSlot(
                    definition: world.Definition,
                    kind: out var slotKind,
                    name: tokens[width],
                    slot: out var slot
                )
                ) {
                    throw new RuleException(
                        detail: $"'{name}' does not spell '{WorldRuleFacts.BodyStatePrefix}<bodyRef>:<slot>' ({WorldFactsCompileContext.BodyRefVocabulary}, then a slot state.body or state.identity declares)",
                        refusal: WorldRuleRefusal.BodyStateMalformed,
                        ruleName: ruleName
                    );
                }

                var body = world.ResolveBodyRef(
                    channel: name,
                    ruleName: ruleName,
                    start: 0,
                    tokens: tokens
                );
                var operand = new BodyStateOperand(
                    body: body,
                    kind: slotKind,
                    slot: slot
                );

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
                value: WorldRuleFacts.IdentityPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
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
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();

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
                    key: cell,
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
                value: WorldRuleFacts.PointerPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
                fact = Pointer(
                    name: name,
                    ruleName: ruleName,
                    world: world
                );
            } else if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.LinkPrefix
            )) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
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
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
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
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );

                var tokens = call!.Texts();
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
                var tokens = call!.Tokens();

                if (
                    !((tokens.Length == 3 && tokens[2] == "phaseError") || (tokens.Length >= 7 && tokens[2] == "phaseErrorAt"))
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

                if (tokens.Length >= 7) {
                    var input = CompileInput(new ChannelCall("inputTime", call.Arguments.Skip(2).ToArray()), world, ruleName);
                    var clock = new ClockOperand(input);
                    fact = BodyFact(CellKind.Fixed, world, input.Owner.Body ?? default, default, -1, WorldInputCatalog.For(world.Definition).HistoryCapacity(world.Definition.InputHistory?.EventCapacity ?? 256),
                        facet => facet.Read(clock), name);
                } else { fact = WorldClockOperand.Instance; }
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

                var tokens = call!.Texts(start: 1);
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
            } else if (BoardSeatFacetOf(name: name) is { } seatFacet) {
                RuleCompiler.RefuseKeyOnReservedChannel(
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
                fact = BoardSeat(
                    facet: seatFacet,
                    name: name,
                    ruleName: ruleName,
                    tokens: call!.Texts(start: 1),
                    world: world
                );
            }

            return (fact is not null);
        }

        private static bool DeclaresScreen(WorldDefinition definition, int index) {
            foreach (var screen in definition.Screens) {
                if (screen.Index == index) {
                    return true;
                }
            }

            return false;
        }
        private static BoardSeatFacet? BoardSeatFacetOf(string name) {
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: BoardEngagedPrefix
            )) {
                return BoardSeatFacet.Engaged;
            }

            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: BoardPointerPrefix
            )) {
                return BoardSeatFacet.Pointer;
            }

            return (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: BoardSidePrefix
            )
                ? BoardSeatFacet.Side
                : null);
        }
        // $board:engaged|pointer|side:<row>:<seat> — the row a board row whose topology a placement's board facet
        // anchors with an engagement, the seat 1-based within the document's local seats.
        private static WorldBodyFactOperand BoardSeat(BoardSeatFacet facet, string name, string ruleName, string[] tokens, WorldFactsCompileContext world) {
            var seats = world.Definition.Population.LocalSeats;
            var row = ((tokens.Length == 2)
                ? world.FindRow(name: tokens[0])
                : null);

            if (
                (row?.EffectiveDomain is not StateDomain.CellsOf board) ||
                (world.FindTopology(name: board.Topology) is not { } topology) ||
                !int.TryParse(
                s: tokens[1],
                style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture,
                result: out var seat
            ) ||
                (seat < 1) ||
                (seat > seats)
            ) {
                throw new RuleException(
                    detail: $"'{name}' does not spell '$board:engaged|pointer|side:<row>:<seat>' over a discrete board row with seat in 1..{seats}",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            WorldPlacement? anchor = null;

            foreach (var placement in world.Definition.Placements) {
                if (
                    (placement.Board is { Engage: not null } facetBoard) &&
                    string.Equals(
                    a: facetBoard.Topology,
                    b: board.Topology,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    anchor = placement;

                    break;
                }
            }

            if (anchor is null) {
                throw new RuleException(
                    detail: $"'{name}' names board row '{tokens[0]}', whose topology '{board.Topology}' no placement's board facet anchors with an engage block",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            var operand = new BoardSeatOperand(
                facet: facet,
                placementId: anchor.Id,
                row: row.Name.Value,
                seat: (seat - 1),
                surface: Puck.Maths.FixedQ4816.FromDouble(value: anchor.Board!.Engage!.Surface),
                topology: topology
            );

            return BodyFact(
                bodyA: default,
                bodyB: default,
                cost: 1L,
                describe: name,
                read: reader => reader.Read(operand: operand),
                rowOrdinal: -1,
                valueKind: CellKind.Int,
                world: world
            );
        }
    }
}
