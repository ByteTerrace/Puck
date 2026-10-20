using Puck.Commands;

namespace Puck.World;

/// <summary>Injection seam for the half of <c>commands</c> validation that needs the host's command registry: a
/// command taking an engine verb's name, a line calling a verb a binding may not name, a cycle through the set. The
/// composition root installs it beside <see cref="BindingVocabularyHook"/>; while it is absent, or answers before the
/// registry exists, only the document-local half runs, and the post-build admission re-runs it.</summary>
public static class AuthoredCommandVocabularyHook {
    /// <summary>Gets or sets the check that appends a refusal per registry conflict in an authored command set.</summary>
    public static Action<AuthoredCommandSet, List<string>>? Check { get; set; }
}
