namespace Puck.World.Server;

/// <summary>The compiled body-frame policy derived from the world's existing contact requirements. Contact providers
/// report geometry; this policy decides which of those facts may orient an authoritative body.</summary>
internal enum WorldBodyUpPolicy : byte {
    /// <summary>Opposed solved gravity, or the contact field's ambient fallback, supplies up. Measured support
    /// normals decide grounding without becoming the body's frame.</summary>
    Ambient,
    /// <summary>The ambient source supplies up away from support, and a measured walkable support normal may supply
    /// grounded up.</summary>
    SurfaceFollowing,
}

internal static class WorldBodyUpPolicyCompiler {
    /// <summary>Compiles the existing <see cref="WorldContactRequirement.GradientDerivedUp"/> requirement into the
    /// body-frame policy that consumes contact and gravity facts.</summary>
    public static WorldBodyUpPolicy Compile(WorldCollision collision) => (
        collision.Requirements.Contains(value: WorldContactRequirement.GradientDerivedUp)
            ? WorldBodyUpPolicy.SurfaceFollowing
            : WorldBodyUpPolicy.Ambient
    );
}
