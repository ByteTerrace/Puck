namespace Puck.World.Addons;

/// <summary>Thrown by <see cref="WorldAddonRuntime.Create"/> when an enabled addon row cannot prepare at boot. The
/// composition root catches exactly this type around its own early resolution of the addon runtime singleton and
/// turns it into an ordinary attributed boot refusal — never a broad catch over unrelated DI construction.</summary>
public sealed class WorldAddonInstallRefusedException : Exception {
    /// <summary>Initializes an exception with the given message.</summary>
    /// <param name="message">The refusal reason.</param>
    public WorldAddonInstallRefusedException(string message) : base(message: message) {
    }
}
