using Xunit;

namespace Puck.World.Testing;

/// <summary>
/// The serialized home for every law that reads <see cref="WorldDefinitionFileSource"/>'s process-wide held
/// composed images. The images belong to the process, not to a test, so a class composing a document beside one of
/// these laws decides whether a composition it is counting merges or is served from an image. Runs one class at a
/// time, apart from every parallel collection.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class DocumentCompositionCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(DocumentCompositionCollection.Name)]</c>.</summary>
    public const string Name = "document-composition";
}
