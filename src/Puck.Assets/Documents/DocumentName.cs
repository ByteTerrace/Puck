namespace Puck.Assets.Documents;

/// <summary>
/// The one rule for when two document names are one name: ordinal, ignoring letter case. A document name is carried
/// by a file (its <c>.puck</c> source or its <c>.world.json</c> document), and NTFS and default APFS resolve a file
/// name case-insensitively while a case-sensitive file system (a Linux checkout, the in-memory file system a web
/// client mounts sources into) keeps two, so a set of names differing only in case would name one document on one
/// machine and two on another. Every door that admits a set of document names holds it to this rule and refuses a
/// collision with <see cref="Collision"/>: the world directory's carriers (<c>WorldDocumentName.TryCarriers</c>, and
/// <c>PuckDocumentComposer.TryCarriers</c>, where each source carries exactly the names it emits, which is also how the
/// official build holds its authored documents), the composer's resolution of one referenced name, a tree compile's
/// hand-authored documents, and the official manifest's <c>documents[]</c> (<c>OfficialCanonicalizer</c>).
/// </summary>
public static class DocumentName {
    /// <summary>Gets the comparer two document names are one name under: ordinal, ignoring letter case.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Returns the refusal for two documents whose names are one name under <see cref="Comparer"/>: names
    /// that differ only in letter case, or the same name carried twice.</summary>
    /// <param name="heldName">The document name met first.</param>
    /// <param name="heldFile">The file that carries <paramref name="heldName"/>.</param>
    /// <param name="otherName">The document name that collides with it.</param>
    /// <param name="otherFile">The file that carries <paramref name="otherName"/>.</param>
    /// <returns>The named refusal, naming both carrying files and both document names.</returns>
    public static string Collision(string heldName, string heldFile, string otherName, string otherFile) => (string.Equals(
        a: heldName,
        b: otherName,
        comparisonType: StringComparison.Ordinal
    )
        ? $"'{heldFile}' and '{otherFile}' both carry the document '{heldName}'; a document name is unique ignoring case, so rename one of them."
        : $"'{heldFile}' and '{otherFile}' carry the documents '{heldName}' and '{otherName}', whose names differ only in letter case; a document name is unique ignoring case, so rename one of them."
    );
}
