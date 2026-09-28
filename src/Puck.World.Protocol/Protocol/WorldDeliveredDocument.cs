namespace Puck.World.Protocol;

/// <summary>A delivered definition and the version it reflects, held together so a reader never pairs a definition
/// with another delivery's version.</summary>
/// <param name="Definition">The delivered definition.</param>
/// <param name="Version">Its version; <see langword="default"/> for a placeholder no authority delivered.</param>
public sealed record WorldDeliveredDocument(WorldDefinition Definition, WorldDocumentVersion Version);
