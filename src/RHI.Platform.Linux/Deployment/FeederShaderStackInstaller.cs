using RHI.Core.Deployment;
using RHI.Core.Services;
using static RHI.Platform.Linux.Deployment.Dlss5FeederFullDeploymentService;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// DLSS5 Feeder Full (DEP-S3, #169): the RHI-owned shader tree under <c>reshade-shaders/RHI/DLSS5FeederFull</c> —
/// DLSS5_Feed.fx, the LumeniteFX kernel with its includes and textures, and the ReShade headers it needs. Every file is
/// placed inside the install's <see cref="DeploymentScope"/> (tracked, declared, verified, never over an existing file), so
/// a failure or a crash before the record is saved takes them all away again.
/// </summary>
internal static class FeederShaderStackInstaller
{
    /// <summary>Checks the staged LumeniteFX archive holds the kernel (staging only).</summary>
    internal static void EnsureLumeniteLayout(string stage)
    {
        var shaderRoot = Path.Combine(stage, "Shaders");
        if (!Directory.Exists(shaderRoot) || !SafeFileEnumeration.Files(shaderRoot, "lumenite_Kernel.fx").Any())
            throw new InvalidDataException("LumeniteFX mainline archive does not contain lumenite_Kernel.fx.");
    }

    /// <summary>Puts every required ReShade header under the staged <c>Shaders</c> folder (staging only).</summary>
    internal static void EnsureHeaderLayout(string stage)
    {
        foreach (var name in RequiredHeaderNames)
        {
            var direct = Path.Combine(stage, "Shaders", name);
            if (!File.Exists(direct))
            {
                var found = Directory.Exists(stage)
                    ? SafeFileEnumeration.Files(stage, name).FirstOrDefault()
                    : null;
                if (found is null) throw new InvalidDataException($"ReShade shader framework header is missing: {name}");
                Directory.CreateDirectory(Path.Combine(stage, "Shaders"));
                AtomicFile.Copy(found, direct);
            }
        }
    }

    /// <summary>
    /// Places the shader tree from the staged downloads. Matches the upstream Feeder refresh invariant: the Lumenite
    /// Kernel and its dependencies only, not every optional LumeniteFX effect (the pack-refresh class of bug fixed
    /// upstream on Windows). A header already in the tree is left alone.
    /// </summary>
    internal static async Task InstallAsync(
        DeploymentScope scope, string feederFx, string lumeniteStage, string headersStage, string shaderRoot, string textureRoot, List<string> copied)
    {
        await PlaceAsync(scope, feederFx, Path.Combine(shaderRoot, FeederFxName), copied).ConfigureAwait(false);

        var lumeniteShaders = Path.Combine(lumeniteStage, "Shaders");
        if (!Directory.Exists(lumeniteShaders))
            throw new InvalidDataException("LumeniteFX shader directory is missing.");
        var kernel = SafeFileEnumeration.Files(lumeniteShaders, "lumenite_Kernel.fx").FirstOrDefault()
            ?? throw new InvalidDataException("LumeniteFX kernel shader is missing.");
        await PlaceAsync(scope, kernel, Path.Combine(shaderRoot, "lumenite_Kernel.fx"), copied).ConfigureAwait(false);
        var includeRoot = Path.Combine(lumeniteShaders, "include");
        if (!Directory.Exists(includeRoot))
            throw new InvalidDataException("LumeniteFX include directory is missing.");
        foreach (var source in SafeFileEnumeration.Files(includeRoot, "*.fxh"))
            await PlaceAsync(scope, source, Path.Combine(shaderRoot, Path.GetRelativePath(lumeniteShaders, source)), copied).ConfigureAwait(false);

        var lumeniteTextures = Path.Combine(lumeniteStage, "Textures");
        if (Directory.Exists(lumeniteTextures))
            foreach (var source in SafeFileEnumeration.Files(lumeniteTextures, "lumenite_*"))
                await PlaceAsync(scope, source, Path.Combine(textureRoot, Path.GetRelativePath(lumeniteTextures, source)), copied).ConfigureAwait(false);

        foreach (var headerName in RequiredHeaderNames)
        {
            var destination = Path.Combine(shaderRoot, headerName);
            if (!File.Exists(destination))
                await PlaceAsync(scope, Path.Combine(headersStage, "Shaders", headerName), destination, copied).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One owned file into the game folder through <paramref name="scope"/>: refused when something is already there (a
    /// file that appears meanwhile also makes it throw), verified, made readable (0644) and added to <paramref name="copied"/>.
    /// </summary>
    internal static async Task PlaceAsync(DeploymentScope scope, string source, string target, List<string> copied)
    {
        if (File.Exists(target)) throw new IOException($"Protected target already exists: {target}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await scope.PlaceCopyAsync(source, target, CancellationToken.None, overwrite: false).ConfigureAwait(false);
        NormalizeReadableFile(target);
        copied.Add(target);
    }
}
