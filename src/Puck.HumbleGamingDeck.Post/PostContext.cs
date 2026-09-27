namespace Puck.HumbleGamingDeck.Post;

/// <summary>The battery's artifact destination and resolved external corpora.</summary>
/// <param name="ArtifactsDirectory">The directory that receives stage reports.</param>
/// <param name="SstRoot">The verified instruction corpus directory, or <see langword="null"/> when absent.</param>
/// <param name="TestRomRoot">The reference-test corpus directory, or <see langword="null"/> when absent.</param>
/// <param name="AccuracyCoinRoot">The AccuracyCoin corpus directory, or <see langword="null"/> when absent.</param>
internal sealed record PostContext(string ArtifactsDirectory, string? SstRoot, string? TestRomRoot, string? AccuracyCoinRoot);
