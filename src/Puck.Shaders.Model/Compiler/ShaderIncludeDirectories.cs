namespace Puck.Shaders;

/// <summary>
/// Where an engine shader source finds the headers every project shares: the include search path a closure walk
/// (<see cref="ShaderSourceClosure.Collect"/>) and the compiler (<see cref="ShaderCompiler"/>, as DXC's <c>-I</c>) resolve
/// an include through once it is not beside the file that names it. A source spells a shared header by its path below
/// that directory, <c>#include "Shared/luma.hlsli"</c>, and the spelling resolves in every layout. In a checkout the
/// directory is <see cref="CheckoutDirectory"/>: the build passes it explicitly (<c>$(PuckShaderIncludeDirectory)</c> in
/// <c>build/Shaders.targets</c>, which every shader project inherits), and a repository tool joins it to the checkout's
/// root. Beside an application it is <c>Assets/Shaders</c>, where <c>Puck.Shaders</c> deploys its <c>Shared</c> headers
/// as content beside the kernel trees that include them (<c>Assets/Shaders/Sdf</c>); a runtime reader of a kernel tree
/// asks <see cref="Of"/>.
/// </summary>
public static class ShaderIncludeDirectories {
    /// <summary>The directory, below an include directory, that holds the shared headers.</summary>
    public const string SharedDirectoryName = "Shared";
    /// <summary>The include directory of a checkout, relative to its root. <c>build/Shaders.targets</c> states the same
    /// directory as <c>$(PuckShaderIncludeDirectory)</c>; the two change together.</summary>
    public const string CheckoutDirectory = "src/Puck.Shaders/Assets/Shaders";

    /// <summary>Returns the include directories a runtime reader of a kernel tree resolves its sources through: the
    /// deployment the tree lies in. Walking up from the source's directory, the first directory <c>D</c> whose
    /// <c>D/Assets/Shaders</c> holds <see cref="SharedDirectoryName"/> names it; a tree in no deployment, such as a source
    /// checkout's or a copy in a scratch directory, resolves through the running application's own,
    /// <c>Assets/Shaders</c> beside <see cref="AppContext.BaseDirectory"/>.</summary>
    /// <param name="sourcePath">The source's path.</param>
    /// <returns>The include directories, as full paths, in search order.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is empty.</exception>
    public static IReadOnlyList<string> Of(string sourcePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);

        for (var directory = new DirectoryInfo(path: (Path.GetDirectoryName(path: Path.GetFullPath(path: sourcePath)) ?? Path.GetFullPath(path: sourcePath))); (directory is not null); directory = directory.Parent) {
            var root = Path.Combine(path1: directory.FullName, path2: "Assets", path3: "Shaders");

            if (Directory.Exists(path: Path.Combine(path1: root, path2: SharedDirectoryName))) {
                return [root];
            }
        }

        return [Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders")];
    }
}
