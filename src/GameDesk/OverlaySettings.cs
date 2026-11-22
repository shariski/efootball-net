using System.Text.Json;

namespace GameDesk;

internal sealed record OverlaySettings(bool Visible = false, bool Detailed = false)
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDesk", "overlay.json");

    public static OverlaySettings Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
