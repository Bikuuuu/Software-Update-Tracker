using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SoftwareUpdateTracker.Core.Storage;

// Writes go to a temp file that then replaces the real one. A corrupt file is kept once as .bak.
internal static class JsonFile
{
    public static (T Value, bool Recovered) Load<T>(string path, JsonTypeInfo<T> type, Func<T> defaults)
    {
        var temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize(stream, type) is { } value) return (value, false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (defaults(), false);
        }
        catch (JsonException)
        {
        }
        File.Move(path, path + ".bak", overwrite: true);
        return (defaults(), true);
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, type);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
