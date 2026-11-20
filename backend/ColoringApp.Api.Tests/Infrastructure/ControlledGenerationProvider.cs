using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using ColoringApp.Api.Integrations;

namespace ColoringApp.Testing;

public sealed class ControlledGenerationProvider : IGenerationProvider
{
    private static readonly Lazy<byte[]> Png = new(CreateColoringPage);
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> calls = new();
    private string activeScenario = "success";
    private Guid currentJob;
    private string currentScenario = "success";

    public void Select(string scenario) => Volatile.Write(ref activeScenario, scenario);
    public string[] Calls(Guid id) => calls.TryGetValue(id, out var recorded) ? recorded.ToArray() : [];

    public async Task<PolishTranscription> TranscribeAsync(string recordingPath, CancellationToken cancellationToken)
    {
        currentJob = Guid.ParseExact(Path.GetFileNameWithoutExtension(recordingPath), "N");
        currentScenario = Volatile.Read(ref activeScenario);
        Record("transcribe");
        if (currentScenario == "verify_recording")
        {
            var uploaded = await File.ReadAllBytesAsync(recordingPath, cancellationToken);
            if (!uploaded.AsSpan().SequenceEqual(GenerationTestEnvironment.Recording))
            {
                throw new InvalidDataException();
            }
        }

        var text = currentScenario switch
        {
            "silence" or "unusable_audio" => "",
            "foreign_language" => "Draw a dragon reading to a knight.",
            "profanity" => "Cholerny smok na obrazku.",
            "graphic" => "Scena z drastyczną przemocą i krwią.",
            _ => "  Smok czyta książkę rycerzowi.  "
        };
        return new PolishTranscription(text, currentScenario != "foreign_language");
    }

    public Task<bool> IsTextFlaggedAsync(string text, CancellationToken cancellationToken)
    {
        var isPrompt = text.StartsWith("Create a child-friendly", StringComparison.Ordinal);
        Record(isPrompt ? "moderate_prompt" : "moderate_transcript");
        return Task.FromResult(isPrompt ? currentScenario == "prompt_flagged" : currentScenario == "graphic");
    }

    public Task<EnrichmentDecision> EnrichAsync(string transcript, CancellationToken cancellationToken)
    {
        Record("enrich");
        return Task.FromResult(currentScenario switch
        {
            "profanity" => new EnrichmentDecision("reject", null, null),
            "bad_enrichment" => new EnrichmentDecision("accept", "Drzewo", ["Smok", " smok "]),
            _ => new EnrichmentDecision("accept", "A friendly dragon reading to a seated knight.", ["smok", "rycerz", "książka"])
        });
    }

    public Task<bool> IsPromptAllowedAsync(string transcript, string prompt, CancellationToken cancellationToken)
    {
        Record("review_prompt");
        return Task.FromResult(currentScenario != "prompt_rejected");
    }

    public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken)
    {
        Record("generate_image");
        if (currentScenario == "pause_image")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        if (currentScenario == "bad_png")
        {
            return Png.Value[..^12];
        }
        if (currentScenario == "wrong_dimensions")
        {
            var wrongDimensions = Png.Value.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(wrongDimensions.AsSpan(16), 1);
            return wrongDimensions;
        }
        if (currentScenario == "corrupt_png")
        {
            var damaged = Png.Value.ToArray();
            damaged[41] = 0; // Break the zlib header of the IDAT chunk.
            var length = BinaryPrimitives.ReadInt32BigEndian(damaged.AsSpan(33));
            var crc = ~UpdateCrc(uint.MaxValue, damaged.AsSpan(37, length + 4));
            BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(41 + length), crc);
            return damaged;
        }
        if (currentScenario == "corrupt_crc")
        {
            var damaged = Png.Value.ToArray();
            damaged[32] ^= 1; // Corrupt the IHDR checksum.
            return damaged;
        }
        return Png.Value;
    }

    public async Task<bool> IsImageFlaggedAsync(byte[] png, CancellationToken cancellationToken)
    {
        Record("moderate_image");
        if (!png.AsSpan().SequenceEqual(Png.Value))
        {
            throw new InvalidDataException();
        }
        if (currentScenario == "image_check_error")
        {
            throw new HttpRequestException();
        }
        if (currentScenario == "image_check_timeout")
        {
            await Task.Delay(20, cancellationToken);
            throw new TaskCanceledException();
        }
        return currentScenario == "image_rejected";
    }

    private void Record(string stage) => calls.GetOrAdd(currentJob, _ => new()).Enqueue(stage);

    private static byte[] CreateColoringPage()
    {
        const int width = 1024;
        const int height = 1536;
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        WriteChunk(png, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var pixels = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var dx = x - width / 2.0;
                    var dy = y - 600.0;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    var petalEdge = 230 + 75 * Math.Cos(7 * Math.Atan2(dy, dx));
                    var isLine = Math.Abs(distance - petalEdge) < 4 || Math.Abs(distance - 95) < 4 ||
                        (y > 878 && y < 1280 && Math.Abs(dx) < 4);
                    row[x + 1] = isLine ? (byte)0 : (byte)255;
                }
                pixels.Write(row);
            }
        }
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> content)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, content.Length);
        png.Write(integer);
        png.Write(type);
        png.Write(content);
        var crc = UpdateCrc(UpdateCrc(uint.MaxValue, type), content);
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        png.Write(integer);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
        }
        return crc;
    }
}
