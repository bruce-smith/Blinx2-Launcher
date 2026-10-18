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
  --update         pull the latest project commits first, then rebuild what changed";

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

/// <summary>The --cli mode: the same setup without the window, for scripted
/// tests and for people who prefer a console.</summary>
public static class Cli
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Program.Usage);
            return 0;
        }
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool yes = args.Contains("--yes"), dry = args.Contains("--dry-run");
        var dir = Path.GetFullPath(Arg("--dir") ?? Util.DefaultInstallDir());
        var isoArg = Arg("--iso");

        using var log = new Log();
        log.Line += l => Console.WriteLine(l);
        if (Program.CheckInstallDir(dir) is { } bad) { log.Error(bad); return 2; }
        if (!dry) log.OpenFile(Path.Combine(dir, "setup.log"));

        var state = SetupState.Load(dir);
        isoArg ??= state.IsoPath;
        string? iso = null;
        if (isoArg != null)
        {
            iso = Setup.ResolveIso(isoArg, out var problem);
            if (iso == null && !File.Exists(Path.Combine(dir, "game_files", "default.xbe")))
            {
                log.Error(problem ?? "No disc image.");
                return 2;
            }
        }
        else if (!File.Exists(Path.Combine(dir, "game_files", "default.xbe")))
        {
            log.Error("Pass --iso <your Blinx 2 .iso/.xiso>.");
            Console.WriteLine(Program.Usage);
            return 2;
        }

        log.Info($"Blinx 2 setup {Program.Version}: install folder {dir}, disc image {iso ?? "(already extracted)"}");
        log.Info("Checking dependencies:");
        var tc = Toolchain.Detect(log);
        var missing = tc.Dependencies.Where(d => !d.Found).ToList();
        var runner = new ProcRunner(log);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (missing.Count > 0)
        {
            if (!tc.Winget && !dry)
            {
                log.Error("winget is not available; install these by hand: " +
                          string.Join("; ", missing.Select(d => $"{d.Name}: {d.ManualUrl}")));
                return 3;
            }
            if (!yes && !dry)
            {
                log.Error("Missing dependencies (run again with --yes to install them with winget):");
                foreach (var d in missing) log.Info("  " + DepInstaller.Describe(d));
                return 3;
            }
            await DepInstaller.InstallAsync(missing, runner, log, cts.Token, dry);
            if (!dry)
            {
                log.Info("Checking dependencies again:");
                tc = Toolchain.Detect(log);
                if (!tc.AllFound)
                {
                    log.Error("Still missing: " + string.Join(", ", tc.Dependencies.Where(d => !d.Found).Select(d => d.Name)) +
                              ". A restart of Windows may be needed after installing Visual Studio.");
                    return 3;
                }
            }
        }

        var setup = new Setup(dir, iso ?? "", log, runner, tc) { MakeShortcuts = !args.Contains("--no-shortcuts") };
        setup.Warning += w => log.Info("WARNING: " + w);
        var pending = setup.Pending();
        long need = setup.SpaceNeeded(pending), free = FreeSpace(dir);
        log.Info($"Steps to run: {pending.Count} of {setup.Steps.Count}, about {Util.FormatDuration(setup.Estimate(pending))} " +
                 $"on a recent PC; needs about {Util.FormatSize(need)}, {Util.FormatSize(free)} free.");
        foreach (var s in pending) log.Info($"  - {s.Title}");
        if (dry) return 0;
        if (free < need)
        {
            log.Error($"Not enough free disk space: {Util.FormatSize(need)} needed, {Util.FormatSize(free)} free.");
            return 4;
        }

        try
        {
            if (args.Contains("--update"))
            {
                await setup.UpdateAsync(cts.Token);
                pending = setup.Pending();
            }
            await setup.RunAsync(cts.Token);
            log.Info($"Done. The game: {setup.ExePath}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            runner.KillAll();
            log.Error("Cancelled. Run the setup again to continue where it stopped.");
            return 5;
        }
        catch (Exception e) when (e is SetupException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            log.Error(e.Message);
            return 1;
        }
    }

    public static long FreeSpace(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch (Exception) { return long.MaxValue; }
    }
}
