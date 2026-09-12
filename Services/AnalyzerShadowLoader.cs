using System.Collections.Concurrent;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RoslynMcpServer.Services;

/// <summary>
/// Keeps workspace build-output analyzer / source-generator DLLs writable while the Roslyn workspace is live.
/// <para>
/// Mechanism of the problem: when the workspace builds a compilation (any semantic operation), Roslyn runs the
/// source generators / analyzers by loading their assemblies via <see cref="Assembly.LoadFrom"/> into the default
/// <see cref="AssemblyLoadContext"/>. On Windows that keeps the original file open (write-locked), so a subsequent
/// <c>dotnet build</c> cannot overwrite the DLL ("The process cannot access the file ... because it is being used
/// by another process"). This is exactly what happens with a centralized <c>bin/</c> (see <c>Directory.Build.props</c>).
/// </para>
/// <para>
/// Fix: before any compilation is built, copy each such DLL to a shadow directory and pre-load the shadow copy into
/// the default ALC. The default ALC deduplicates by assembly identity, so when Roslyn later requests the original
/// path the ALC returns the already-loaded shadow copy WITHOUT opening the original file. The build output stays
/// writable; the generator / analyzer runs from the (byte-identical) shadow copy.
/// </para>
/// <para>
/// Caveat: the first-loaded version is used for the process lifetime (ALC identity dedup). A rebuilt analyzer /
/// generator is picked up only after an MCP restart; a warning is logged when a newer on-disk version is detected.
/// </para>
/// </summary>
public static class AnalyzerShadowLoader
{
    private static readonly object Gate = new();

    // Assembly simple name -> LastWriteTimeUtc of the version currently loaded into the default ALC.
    private static readonly ConcurrentDictionary<string, DateTime> LoadedVersion = new(StringComparer.OrdinalIgnoreCase);

    private static string ShadowBaseDir => Path.Combine(Path.GetTempPath(), "RoslynMcpServer", "analyzer-shadow");

    /// <summary>
    /// Copies and pre-loads (into the default ALC) every analyzer DLL of <paramref name="solution"/> that is a
    /// workspace build output (under <paramref name="loadedFileDirectory"/> or inside a <c>bin</c>/<c>obj</c> folder).
    /// Idempotent and safe to call on every load. Returns the number of DLLs shadowed.
    /// </summary>
    public static int PreloadBuildOutputAnalyzers(Solution solution, string? loadedFileDirectory, ILogger logger)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                if (reference is not AnalyzerFileReference fileReference)
                {
                    continue;
                }

                var path = fileReference.FullPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                if (IsUnderDirectory(path, loadedFileDirectory) || IsBuildOutputPath(path))
                {
                    paths.Add(path);
                }
            }
        }

        if (paths.Count == 0)
        {
            return 0;
        }

        var shadowRoot = Path.Combine(ShadowBaseDir, ShortHash(loadedFileDirectory ?? string.Empty));
        var shadowed = 0;
        lock (Gate)
        {
            foreach (var path in paths)
            {
                try
                {
                    ShadowAndPreload(path, shadowRoot, logger);
                    shadowed++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Analyzer shadow preload failed for {Path}; the file may stay locked: {Message}",
                        path,
                        ex.Message);
                }
            }
        }

        return shadowed;
    }

    /// <summary>
    /// Deletes the shadow directory for <paramref name="loadedFileDirectory"/> (workspace clear). Best effort:
    /// files still referenced by loaded assemblies may be in use and are left for the OS temp cleanup.
    /// </summary>
    public static void Cleanup(string? loadedFileDirectory)
    {
        var shadowRoot = Path.Combine(ShadowBaseDir, ShortHash(loadedFileDirectory ?? string.Empty));
        try
        {
            if (Directory.Exists(shadowRoot))
            {
                Directory.Delete(shadowRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort: the files live in a temp dir; the OS reclaims them.
        }
    }

    private static void ShadowAndPreload(string path, string shadowRoot, ILogger logger)
    {
        Directory.CreateDirectory(shadowRoot);
        var shadowPath = Path.Combine(shadowRoot, ShortHash(path) + "_" + Path.GetFileName(path));

        var sourceTime = new FileInfo(path).LastWriteTimeUtc;
        if (!File.Exists(shadowPath) || new FileInfo(shadowPath).LastWriteTimeUtc < sourceTime)
        {
            File.Copy(path, shadowPath, overwrite: true);
        }

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(shadowPath);
        var name = assembly.GetName().Name ?? string.Empty;

        if (LoadedVersion.TryGetValue(name, out var loadedTime) && sourceTime > loadedTime)
        {
            logger.LogWarning(
                "Analyzer {Name} was rebuilt (newer on disk) but the already-loaded version is still in use; restart the MCP to pick up the new version.",
                name);
        }

        LoadedVersion[name] = sourceTime;
        logger.LogInformation(
            "Shadow-loaded analyzer {Name} from {Shadow} (original {Original} stays writable).",
            name,
            shadowPath,
            path);
    }

    /// <summary>True when <paramref name="path"/> is strictly under <paramref name="directory"/> (case-insensitive).</summary>
    internal static bool IsUnderDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when any path component is <c>bin</c> or <c>obj</c> (the MSBuild output folders).</summary>
    internal static bool IsBuildOutputPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        var builder = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            builder.Append(bytes[i].ToString("x2"));
        }

        return builder.ToString();
    }
}
