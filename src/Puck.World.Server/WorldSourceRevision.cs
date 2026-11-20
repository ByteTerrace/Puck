namespace Puck.World.Server;

/// <summary>A revision of a world's source as an authority took it: the document its source composed, drew and settled
/// to, and the composition's content pin. A reload's delta is cut from it to the next revision, and the live document,
/// which the session's own edits move away from it, is merged against it.</summary>
/// <param name="Definition">The document the revision composed to.</param>
/// <param name="ContentHash">The composition's content pin (<see cref="WorldDefinitionFileSource.ComputeContentHash"/>),
/// or empty when the authority did not read the revision from a source it can pin (a boot document, a restored
/// checkpoint's base), so no read of a source matches it as unchanged.</param>
public sealed record WorldSourceRevision(WorldDefinition Definition, string ContentHash);
