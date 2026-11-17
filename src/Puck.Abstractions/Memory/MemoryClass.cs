namespace Puck.Abstractions.Memory;

/// <summary>
/// What an admitted byte is for, in the order the classes are admitted: the earlier classes are what a presentation
/// cannot run without, and the later ones give way to them. The class decides what an admission that finds no room
/// answers (<see cref="MemoryClasses.GivesWay"/>): a class that gives way is refused, and its owner shows something coarser or waits;
/// a class that cannot is admitted past the limit and counted, since refusing it would end the presentation.
/// </summary>
public enum MemoryClass {
    /// <summary>Fixed by the views: view targets, cascades, screens, program tables and an installed pipeline graph.
    /// Storage that names no class is fixed.</summary>
    Fixed,
    /// <summary>The working peak of a job (a bake, a scene build, a transfer), returned when the job completes.</summary>
    Workspace,
    /// <summary>What a demanded residency cannot draw without.</summary>
    Floor,
    /// <summary>Detail no view sees and the picture still needs: interaction reach, shadow casters, light blockers.</summary>
    Support,
    /// <summary>Detail a view resolves, split across residencies by demand under one bias.</summary>
    Elastic,
    /// <summary>Storage only a reclaimable cache claim holds, trimmed first.</summary>
    Cached,
}
/// <summary>The one statement of which classes give way under a limit.</summary>
public static class MemoryClasses {
    /// <summary>The count of classes.</summary>
    public const int Count = 6;

    /// <summary>Returns whether an admission of <paramref name="memoryClass"/> that finds no room is refused. Workspace,
    /// support, elastic and cached storage is: its owner defers the job, keeps the coarser product or drops the cache.
    /// Fixed and floor storage is not: it is admitted past the limit and counted.</summary>
    /// <param name="memoryClass">The class.</param>
    /// <returns>Whether the class gives way.</returns>
    public static bool GivesWay(MemoryClass memoryClass) => (memoryClass is MemoryClass.Workspace or MemoryClass.Support or MemoryClass.Elastic or MemoryClass.Cached);
    /// <summary>Returns a class's name as a report prints it.</summary>
    /// <param name="memoryClass">The class.</param>
    /// <returns>The lower-case name.</returns>
    public static string NameOf(MemoryClass memoryClass) => memoryClass switch {
        MemoryClass.Fixed => "fixed",
        MemoryClass.Workspace => "workspace",
        MemoryClass.Floor => "floors",
        MemoryClass.Support => "support",
        MemoryClass.Elastic => "elastic",
        MemoryClass.Cached => "caches",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(memoryClass)),
    };
}
