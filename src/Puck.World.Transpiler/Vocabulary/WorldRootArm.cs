namespace Puck.World.Transpiler.Vocabulary;

/// <summary>Which arm the lowering's and the printer's root dispatch take for a construct written at the document's
/// own root.</summary>
/// <remarks>Both dispatchers switch over this exhaustively, so an arm added here without a case fails the build
/// rather than letting a described root construct fall silently through to the generic path. A described root
/// construct that names no arm fails <c>ConstructRootArmLawTests</c>.</remarks>
public enum WorldRootArm {
    /// <summary>The compile-time layer: the construct is evaluated away before the document exists, so neither
    /// dispatcher has an arm for it.</summary>
    CompileTime,
    /// <summary>An ordinary nested block, lowered key by key and printed back as a field.</summary>
    Field,
    /// <summary>The <c>addons</c> array.</summary>
    Addons,
    /// <summary>The <c>cartridge</c> object, whose lowering resolves the content hash.</summary>
    Cartridge,
    /// <summary>The <c>materials</c> array.</summary>
    Materials,
    /// <summary>The <c>patterns</c> array.</summary>
    Patterns,
    /// <summary>The <c>placements</c> section.</summary>
    Placements,
    /// <summary>The <c>prototypes</c> array.</summary>
    Prototypes,
    /// <summary>The <c>ruleGroups</c> array.</summary>
    RuleGroups,
    /// <summary>The <c>rules</c> array.</summary>
    Rules,
    /// <summary>The <c>sets</c> array.</summary>
    Sets,
    /// <summary>The <c>shapes</c> array.</summary>
    Shapes,
    /// <summary>The <c>state</c> section.</summary>
    State,
    /// <summary>The <c>views</c> section.</summary>
    Views,
}
