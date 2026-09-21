using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Assets;

/// <summary>Coordinates asset discovery, verification, and an explicitly requested lock refresh during one root
/// source compilation.</summary>
public sealed class AssetCompilationContext {
    private readonly List<AssetReference> m_references = [];
    private readonly Dictionary<string, string> m_physicalPaths = new(
        comparer: (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    );
    private readonly HashSet<string> m_verified = new(comparer: StringComparer.Ordinal);

    private AssetLock? m_lock;
    private AssetLock? m_preparedUpdate;
    private long m_verifiedBytes;

    /// <summary>Gets the root source whose sibling lock owns every expanded module reference.</summary>
    public string RootSourcePath { get; }
    /// <summary>Gets whether a successful compile should refresh the lock from the complete discovered set.</summary>
    public bool UpdateLock { get; }
    /// <summary>Gets the discovered module-relative references in encounter order.</summary>
    public IReadOnlyList<AssetReference> PendingReferences => m_references;

    /// <summary>Initializes a compilation asset context.</summary>
    /// <param name="rootSourcePath">The root <c>.puck</c> source path.</param>
    /// <param name="updateLock">Whether <see cref="SaveUpdatedLock"/> may write a refreshed lock.</param>
    public AssetCompilationContext(string rootSourcePath, bool updateLock = false) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rootSourcePath);
        RootSourcePath = Path.GetFullPath(path: rootSourcePath);
        UpdateLock = updateLock;
    }

    /// <summary>Discovers one asset and, in ordinary compile mode, verifies its bytes against the root lock.</summary>
    /// <param name="authoredPath">The path written after <c>asset</c>.</param>
    /// <param name="definingBasePath">The directory of the root or imported module that wrote the expression.</param>
    /// <param name="span">The expression span for a refusal.</param>
    /// <param name="diagnostics">The compilation diagnostic bag.</param>
    /// <param name="normalizedPath">The root-source-relative path to emit into the compiled document.</param>
    /// <returns><see langword="true"/> when lowering may continue with <paramref name="normalizedPath"/>.</returns>
    public bool TryResolve(
        string authoredPath,
        string definingBasePath,
        SourceSpan span,
        DiagnosticBag diagnostics,
        out string normalizedPath
    ) {
        ArgumentNullException.ThrowIfNull(argument: diagnostics);
        normalizedPath = authoredPath;
        var reference = new AssetReference(BasePath: definingBasePath, Path: authoredPath);
        string? physicalPath = null;
        var added = false;
        var addedBytes = 0L;

        try {
            if (m_preparedUpdate is not null) {
                throw new AssetLockException(message: "Asset discovery cannot continue after the update set has been validated.");
            }
            normalizedPath = AssetLock.NormalizeReferencePath(sourcePath: RootSourcePath, reference: reference);
            physicalPath = Path.GetFullPath(
                path: authoredPath.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'),
                basePath: Path.GetFullPath(path: definingBasePath)
            );
            if (m_physicalPaths.TryGetValue(key: physicalPath, value: out var existingPath) &&
                !string.Equals(a: existingPath, b: normalizedPath, comparisonType: StringComparison.Ordinal)) {
                throw new AssetLockException(message: $"Asset paths '{existingPath}' and '{normalizedPath}' resolve to the same physical file.");
            }

            m_physicalPaths[physicalPath] = normalizedPath;
            var firstOccurrence = m_verified.Add(item: normalizedPath);

            if (firstOccurrence) {
                added = true;
                if (m_verified.Count > AssetLock.MaximumAssetCount) {
                    throw new AssetLockException(message: $"Asset references exceed their {AssetLock.MaximumAssetCount}-entry limit.");
                }
                m_references.Add(item: reference);
            }

            if (!UpdateLock && firstOccurrence) {
                m_lock ??= AssetLock.Load(sourcePath: RootSourcePath);
                var resolved = m_lock.Resolve(sourcePath: RootSourcePath, references: [reference]);

                addedBytes = resolved[normalizedPath].Content.Length;
                m_verifiedBytes = checked((m_verifiedBytes + addedBytes));
                if (m_verifiedBytes > AssetLock.MaximumTotalBytes) {
                    throw new AssetLockException(message: $"Assets exceed their {AssetLock.MaximumTotalBytes}-byte total limit.");
                }
            }

            return true;
        } catch (Exception exception) when ((exception is AssetLockException or IOException or UnauthorizedAccessException)) {
            if (added) {
                _ = m_verified.Remove(item: normalizedPath);
                _ = m_references.Remove(item: reference);
                m_verifiedBytes -= addedBytes;
            }
            if (physicalPath is not null) {
                _ = m_physicalPaths.Remove(key: physicalPath);
            }
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: exception.Message,
                span: span
            );
            return false;
        }
    }
    /// <summary>Reads and hashes every pending update asset without writing, so compilation cannot succeed with
    /// a missing, unreadable, or over-budget asset.</summary>
    /// <param name="diagnostics">The compilation diagnostic bag.</param>
    /// <returns><see langword="true"/> when the pending set is valid and sealed for a later save.</returns>
    public bool Validate(DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(argument: diagnostics);

        if (!UpdateLock || diagnostics.HasErrors) {
            return !diagnostics.HasErrors;
        }
        if (m_preparedUpdate is not null) {
            return true;
        }

        try {
            m_preparedUpdate = AssetLock.Update(sourcePath: RootSourcePath, references: m_references);
            return true;
        } catch (Exception exception) when ((exception is AssetLockException or IOException or UnauthorizedAccessException)) {
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: exception.Message,
                span: SourceSpan.None
            );
            return false;
        }
    }
    /// <summary>Writes an explicitly requested, already validated refresh after every other compile and output
    /// validation stage has succeeded.</summary>
    /// <param name="diagnostics">The compilation diagnostic bag.</param>
    /// <returns><see langword="true"/> when no asset refusal occurred. This also returns true when no write was requested.</returns>
    public bool SaveUpdatedLock(DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(argument: diagnostics);

        if (!UpdateLock || diagnostics.HasErrors) {
            return !diagnostics.HasErrors;
        }
        if (m_preparedUpdate is null) {
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: "The asset update was not validated before its save was requested.",
                span: SourceSpan.None
            );
            return false;
        }

        try {
            _ = m_preparedUpdate.Write(sourcePath: RootSourcePath);
            m_lock = m_preparedUpdate;
            return true;
        } catch (Exception exception) when ((exception is AssetLockException or IOException or UnauthorizedAccessException)) {
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: exception.Message,
                span: SourceSpan.None
            );
            return false;
        }
    }
}
