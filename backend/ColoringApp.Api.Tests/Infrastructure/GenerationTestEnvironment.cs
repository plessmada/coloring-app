using ColoringApp.Api.Generations;

namespace ColoringApp.Testing;

public sealed class GenerationTestEnvironment : IDisposable
{
    public string StorageRoot { get; } = Directory.CreateTempSubdirectory("coloring-files-").FullName;

    public static byte[] Recording { get; } = [0x00, 0x11, 0xFF, 0x00, 0x80];
    internal static GenerationPollingIntervals Polling { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public int CountFiles(string folder) => ListFiles(folder).Length;

    public string[] ListFiles(string folder)
    {
        var directory = Path.Combine(StorageRoot, folder);
        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    public void Dispose() => Directory.Delete(StorageRoot, recursive: true);
}
