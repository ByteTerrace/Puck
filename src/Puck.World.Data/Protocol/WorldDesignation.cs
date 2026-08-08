namespace Puck.World.Protocol;

/// <summary>A subject-bearing write into one target register owned by a body.</summary>
/// <param name="EntityIndex">The body whose register is written.</param>
/// <param name="Register">The authored target-register name.</param>
/// <param name="Subject">The proposed subject; designation currently admits a concrete body.</param>
public readonly record struct WorldDesignation(int EntityIndex, string Register, GrantSubject Subject);
