namespace Puck.World.Protocol;

/// <summary>A delivered definition, the version it reflects and the lifetime it belongs to, held together so a reader
/// never pairs a definition with another delivery's version, or with the lifetime of a world it replaced or was
/// replaced by.</summary>
/// <param name="Definition">The delivered definition.</param>
/// <param name="Version">Its version; <see langword="default"/> for a placeholder no authority delivered.</param>
/// <param name="Lifetime">How many times an observer of the destination has seen another world replace the previous one:
/// it advances when a delivery names a different activation than the document before it, and is published in the same
/// reference write as the document it describes.</param>
public sealed record WorldDeliveredDocument(WorldDefinition Definition, WorldDocumentVersion Version, int Lifetime = 0);
