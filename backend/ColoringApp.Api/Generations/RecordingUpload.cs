using System.Buffers;
using ColoringApp.Api.Integrations;
using Microsoft.AspNetCore.WebUtilities;

namespace ColoringApp.Api.Generations;

internal static class RecordingUpload
{
    public const int AudioLimit = 5 * 1024 * 1024;

    public static async Task<MultipartSection?> ReadSectionAsync(MultipartReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadNextSectionAsync(cancellationToken);
        }
        catch (IOException exception) when (exception is not BadHttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException();
        }
    }

    private static async Task<int> ReadRecordingAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException exception) when (exception is not BadHttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException();
        }
    }

    public static async Task<int> SaveAsync(Stream source, PrivateGenerationFiles files, Guid key,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var created = false;
        try
        {
            await using var file = files.Create(GenerationFileKind.Audio, key);
            created = true;
            var total = 0;
            int read;
            while ((read = await ReadRecordingAsync(source, buffer, cancellationToken)) != 0)
            {
                total += read;
                if (total > AudioLimit)
                {
                    return total;
                }
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return total;
        }
        catch
        {
            if (created)
            {
                files.TryDelete(GenerationFileKind.Audio, key);
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
