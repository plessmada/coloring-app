namespace ColoringApp.Api.Integrations;

internal enum GenerationFileKind { Audio, Staged, Image }

internal sealed class PrivateGenerationFiles
{
    private readonly string root;

    public PrivateGenerationFiles(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColoringApp");
        root = Path.GetFullPath(configuration["GenerationStorage:Root"] ?? defaultPath);
        var projectRoot = new DirectoryInfo(environment.ContentRootPath);
        while (projectRoot.Parent is { } parent && !Directory.Exists(Path.Combine(projectRoot.FullName, ".git")))
        {
            projectRoot = parent;
        }

        var insideRepository = Directory.Exists(Path.Combine(projectRoot.FullName, ".git")) && IsWithin(root, projectRoot.FullName);
        if (insideRepository || IsWithin(root, environment.ContentRootPath) ||
            (environment.WebRootPath is { } webRoot && IsWithin(root, webRoot)))
        {
            throw new InvalidOperationException("Configure GenerationStorage:Root outside the repository and web root.");
        }
    }

    public string PathFor(GenerationFileKind kind, Guid key) =>
        Path.Combine(DirectoryFor(kind), $"{key:N}{(kind == GenerationFileKind.Audio ? ".webm" : ".png")}");

    public FileStream Create(GenerationFileKind kind, Guid key)
    {
        EnsureDirectory(kind);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(PathFor(kind, key), options);
    }

    public async Task PublishAsync(Guid key, byte[] png, CancellationToken cancellationToken)
    {
        await using (var stage = Create(GenerationFileKind.Staged, key))
        {
            await stage.WriteAsync(png, cancellationToken);
        }
        EnsureDirectory(GenerationFileKind.Image);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(PathFor(GenerationFileKind.Staged, key), PathFor(GenerationFileKind.Image, key));
    }

    public FileStream? OpenImage(Guid key)
    {
        try
        {
            return new FileStream(PathFor(GenerationFileKind.Image, key), FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool TryDelete(GenerationFileKind kind, Guid key)
    {
        try
        {
            File.Delete(PathFor(kind, key));
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IEnumerable<Guid> FindUnreferencedCandidates(GenerationFileKind kind, DateTime olderThan)
    {
        var directory = DirectoryFor(kind);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(directory, kind == GenerationFileKind.Audio ? "*.webm" : "*.png"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var key) &&
                File.GetLastWriteTimeUtc(path) < olderThan)
            {
                yield return key;
            }
        }
    }

    private void EnsureDirectory(GenerationFileKind kind)
    {
        var directory = DirectoryFor(kind);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(root, privateDirectory);
            Directory.CreateDirectory(directory, privateDirectory);
        }
    }

    private string DirectoryFor(GenerationFileKind kind) => Path.Combine(root, kind switch
    {
        GenerationFileKind.Audio => "recordings",
        GenerationFileKind.Staged => "staged",
        GenerationFileKind.Image => "approved",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
