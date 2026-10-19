using System.Reflection;

namespace Blinx2Launcher;

public static class Program
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";

    public const string Usage =
@"Blinx2Setup.exe                       open the setup window
Blinx2Setup.exe --cli --iso <file|folder> [--dir <install folder>] [options]
  --yes            install missing dependencies without asking
  --dry-run        detect and print what would be installed and run; change nothing
  --no-shortcuts   do not create desktop/Start-menu shortcuts
Updating an install (--dir: the install folder, or a clone of the project):
Blinx2Setup.exe --cli --check-update [--dir <folder>]    exit code 10 if an update exists
Blinx2Setup.exe --cli --update [--stash] [--dir <folder>]
  --update         fetch the latest project commits, update the clone and its
                   submodules, regenerate only if the recompiler's inputs
                   changed, and rebuild
  --stash          save local changes with git stash (never dropped) instead
                   of refusing to update
  --repo <clone>   use an existing clone of blinx-2-recompile-full
  --game-dir <dir> its game files, if not found next to it";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--cli") || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            return Cli.Run(args).GetAwaiter().GetResult();
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>Checks an install folder; returns a problem to show, or null.</summary>
    public static string? CheckInstallDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
            return "Choose a full folder path, e.g. C:\\Blinx2.";
        var full = Path.GetFullPath(dir);
        if (!Util.IsAscii(full))
            return "The folder path must use plain English letters only (no accents or other scripts): the game reads it with old file functions.";
        var gameDir = Path.Combine(full, "game_files");
        if (gameDir.Length > 95)
            return $"The folder path is too long ({gameDir.Length} characters with \\game_files; at most 95). Choose a shorter one, e.g. C:\\Blinx2.";
        if (full.TrimEnd('\\').Length <= 3)
            return "Choose a folder, not the root of a drive (e.g. C:\\Blinx2).";
        return null;
    }
}

