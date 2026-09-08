namespace Puck.World;

/// <summary>The world's widening of <see cref="IRuleReader"/>: one read per world operand fact, plus the two
/// resolutions the world's body references need. The evaluator that owns bodies, regions, machines, and channels
/// implements it; every <see cref="WorldOperandFact"/> casts the reader it is handed to this.</summary>
/// <remarks>Every member is read on the tick path and allocates nothing.</remarks>
public interface IWorldRuleReader : IRuleReader {
    RuleFact Read(PopulationOperand operand);
    RuleFact Read(PhysicsQuiescentOperand operand);
    RuleFact Read(ClockOperand operand);
    RuleFact Read(RegionOccupancyOperand operand);
    RuleFact Read(PlacementInfluenceOperand operand);
    RuleFact Read(MachineMemoryOperand operand);
    RuleFact Read(ArgBodyOperand operand);
    RuleFact Read(BodyDistanceOperand operand);
    RuleFact Read(LineOfSightOperand operand);
    RuleFact Read(ParkedOperand operand);
    RuleFact Read(UprightOperand operand);
    RuleFact Read(LinkStalenessOperand operand);
    RuleFact Read(ChannelOperand operand);
    RuleFact Read(NearestOperand operand);
    RuleFact Read(NavigationOperand operand);
    RuleFact Read(BoardCellOfOperand operand);
    /// <summary>Returns the body index a compiled reference names for the evaluation in flight, or -1.</summary>
    /// <param name="bodyRef">The reference.</param>
    int ResolveBody(in CompiledBodyRef bodyRef);
    /// <summary>Returns the cell key a <c>$pair:</c> indirection names for the evaluation in flight.</summary>
    /// <param name="key">The pair key.</param>
    string PairKey(PairKeyFact key);
}
