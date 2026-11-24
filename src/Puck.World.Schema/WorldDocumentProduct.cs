namespace Puck.World;

/// <summary>
/// A product built from a document and kept while a later document changes none of the parts its build read
/// (<see cref="WorldDocumentReads"/>, <see cref="WorldDocumentInputs.Changed"/>): a reader that re-derives on every
/// delivery derives again only when a part it read moved, so an edit to another part (a views row) costs it nothing.
/// The build runs on the calling thread, which is the thread its reads are recorded on.
/// </summary>
/// <typeparam name="T">The product.</typeparam>
public sealed class WorldDocumentProduct<T> {
    private WorldDefinition? m_source;
    private WorldDocumentInputs m_inputs;
    private T m_product = default!;

    /// <summary>Gets how many times the product was built.</summary>
    public long Builds { get; private set; }
    /// <summary>Gets how many times a later document left the product standing.</summary>
    public long Stood { get; private set; }

    /// <summary>Returns the product for a document: the one held when the document changes no part its build read,
    /// otherwise the product <paramref name="build"/> makes now, whose reads are recorded for the next document.</summary>
    /// <param name="document">The document the product is for.</param>
    /// <param name="build">The build, which reads the document on the calling thread.</param>
    /// <returns>The product.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public T Of(WorldDefinition document, Func<T> build) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: build);

        if (Holds(document: document)) {
            if (!ReferenceEquals(objA: m_source, objB: document)) { Stood++; }
            m_source = document;
            return m_product;
        }
        m_product = WorldDocumentReads.Record(build: build, inputs: out var inputs);
        m_inputs = inputs;
        m_source = document;
        Builds++;
        return m_product;
    }
    /// <summary>Returns whether the held product stands for a document: one was built, and the document changes no part
    /// its build read.</summary>
    /// <param name="document">The document.</param>
    /// <returns><see langword="true"/> when the held product stands.</returns>
    public bool Holds(WorldDefinition document) => ((m_source is { } source) &&
        (ReferenceEquals(objA: source, objB: document) || !WorldDocumentInputs.Changed(after: document, before: source).Overlaps(other: m_inputs)));
    /// <summary>Forgets the held product, so the next document builds it again.</summary>
    public void Forget() {
        m_source = null;
        m_product = default!;
    }
}
