using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockShot.VintageStory.Core;

[JsonConverter(typeof(JsonStringEnumConverter<UploadMode>))]
public enum UploadMode
{
    Off,
    Prompt,
    Automatic
}

public sealed record BlockShotConfiguration
{
    public UploadMode UploadMode { get; set; } = UploadMode.Prompt;
    public bool Anonymous { get; set; } = true;
    public bool CopyUrlToClipboard { get; set; } = true;

    public void CycleUploadMode() => UploadMode = UploadMode switch
    {
        UploadMode.Off => UploadMode.Prompt,
        UploadMode.Prompt => UploadMode.Automatic,
        _ => UploadMode.Off
    };
}

public sealed class BlockShotConfigurationStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public BlockShotConfiguration Load()
    {
        try
        {
            if (!File.Exists(Path)) return new BlockShotConfiguration();
            return JsonSerializer.Deserialize<BlockShotConfiguration>(File.ReadAllText(Path), JsonOptions)
                ?? new BlockShotConfiguration();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new BlockShotConfiguration();
        }
    }

    public Task SaveAsync(BlockShotConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AtomicTextFile.WriteAllTextAsync(
            Path,
            JsonSerializer.Serialize(configuration, JsonOptions),
            cancellationToken);
    }
}

