using System.Text.Json;

namespace Blinx2Launcher;

/// <summary>
/// What has been done in an install folder, saved after every step in
/// &lt;install&gt;\setup_state.json. A rerun continues at the first step that
/// is not done (or whose inputs changed, e.g. after an update pulled new
/// commits): that step and every one after it run again.
/// </summary>
public sealed class SetupState
{
    public string? IsoPath { get; set; }
    public Dictionary<string, string> Done { get; set; } = new();
    /// <summary>Folders this setup created and may delete to retry a step.</summary>
    public List<string> Created { get; set; } = new();

    public static string FileIn(string installDir) => Path.Combine(installDir, "setup_state.json");

    public static SetupState Load(string installDir)
    {
        try
        {
            var f = FileIn(installDir);
            if (File.Exists(f))
                return JsonSerializer.Deserialize<SetupState>(File.ReadAllText(f)) ?? new SetupState();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new SetupState();
    }

    public void Save(string installDir)
    {
        Directory.CreateDirectory(installDir);
        var f = FileIn(installDir);
        var tmp = f + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, f, overwrite: true);
    }
}

/// <summary>Where the launcher remembers the last install folder.</summary>
public static class LauncherSettings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blinx2Launcher", "launcher.json");

    public sealed class Data
    {
        public string? InstallDir { get; set; }
        public string? IsoPath { get; set; }
    }

    public static Data Load()
    {
        try { return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data(); }
        catch { return new Data(); }
    }

    public static void Save(Data d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d));
        }
        catch (IOException) { }
    }
}
