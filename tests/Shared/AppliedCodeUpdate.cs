using System.Reflection;
using Puck.Abstractions;

namespace Puck.Tests.Shared;

/// <summary>
/// Applies a hot-reload update to the test process as the runtime's hot-reload agent does: it calls
/// <see cref="CodeRevision"/>'s metadata-update handlers by the names the agent calls them by, <c>ClearCache</c> and then
/// <c>UpdateApplication</c>, with the types the update changed. The revision it moves is process-wide, so a law that
/// applies one belongs to <see cref="AppliedCodeUpdateCollection"/>.
/// </summary>
public static class AppliedCodeUpdate {
    /// <summary>Applies an update that changed <paramref name="types"/>.</summary>
    /// <param name="types">The types the update changed.</param>
    public static void Apply(params Type[] types) {
        foreach (var handler in (string[])["ClearCache", "UpdateApplication"]) {
            _ = typeof(CodeRevision).GetMethod(name: handler, bindingAttr: (BindingFlags.NonPublic | BindingFlags.Static))!.Invoke(obj: null, parameters: [types]);
        }
    }
}

/// <summary>The laws that apply an update to the process, which run alone: a revision one moves is every test's.</summary>
[Xunit.CollectionDefinition(name: nameof(AppliedCodeUpdateCollection), DisableParallelization = true)]
public sealed class AppliedCodeUpdateCollection;
