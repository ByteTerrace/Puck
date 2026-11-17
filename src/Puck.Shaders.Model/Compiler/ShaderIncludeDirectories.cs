namespace Puck.Shaders;

/// <summary>
/// Where an engine shader source finds the headers every project shares: the include search path a closure walk
/// (<see cref="ShaderSourceClosure.Collect"/>) and the compiler (<see cref="ShaderCompiler"/>, as DXC's <c>-I</c>) resolve
/// an include through once it is not beside the file that names it. A source spells a shared header by its path below
/// that directory, <c>#include "Shared/luma.hlsli"</c>, and the spelling resolves in every layout: in a source checkout
/// the directory is <c>src/Puck.Shaders/Assets/Shaders</c>, and beside an application it is <c>Assets/Shaders</c>, where
/// <c>Puck.Shaders</c> deploys its <c>Shared</c> headers as content beside the kernel trees that include them
/// (<c>Assets/Shaders/Sdf</c>). The build, the deployed kernel tree's source reader and a reload all ask
/// <see cref="Of"/>, so one rule names the directory in each layout.
/// </summary>
public static class ShaderIncludeDirectories {
    /// <summary>The directory, below an include directory, that holds the shared headers.</summary>
    public const string SharedDirectoryName = "Shared";

    /// <summary>Returns the include directories of an engine shader source: the shader root of the layout the source lies
    /// in. Walking up from the source's directory, the first directory <c>D</c> whose <c>D/Assets/Shaders</c> holds
    /// <see cref="SharedDirectoryName"/> (a deployed application, or <c>Puck.Shaders</c>' own project) or whose
    /// <c>D/Puck.Shaders/Assets/Shaders</c> does (a checkout's <c>src</c>) names it; a source in neither layout, such as a
    /// kernel tree copied to a scratch directory, resolves through the application's own deployment,
    /// <c>Assets/Shaders</c> beside <see cref="AppContext.BaseDirectory"/>.</summary>
    /// <param name="sourcePath">The source's path.</param>
    /// <returns>The include directories, as full paths, in search order.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is empty.</exception>
    public static IReadOnlyList<string> Of(string sourcePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);

        for (var directory = new DirectoryInfo(path: (Path.GetDirectoryName(path: Path.GetFullPath(path: sourcePath)) ?? Path.GetFullPath(path: sourcePath))); (directory is not null); directory = directory.Parent) {
            foreach (var root in (ReadOnlySpan<string>)[Path.Combine(path1: directory.FullName, path2: "Assets", path3: "Shaders"), Path.Combine(path1: directory.FullName, path2: "Puck.Shaders", path3: "Assets", path4: "Shaders")]) {
                if (Directory.Exists(path: Path.Combine(path1: root, path2: SharedDirectoryName))) {
                    return [root];
                }
            }
        }

        return [Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders")];
    }
}
