namespace Puck.World.Protocol;

/// <summary>The result of the ordered intent door: permission and semantic ingress admission are separate decisions.</summary>
/// <param name="Permission">The grant decision made before reading semantic input.</param>
/// <param name="InputRefusal">The semantic refusal after permission succeeded, or null when ingress was admitted.</param>
public readonly record struct WorldIntentAdmission(GrantVerdict Permission, string? InputRefusal = null) {
    /// <summary>Whether both the permission and semantic input checks succeeded.</summary>
    public bool IsAccepted => (Permission.IsAllowed && (InputRefusal is null));
}
