namespace Blinx2Launcher;

/// <summary>The --cli mode: the same setup and update without the window,
/// for scripted tests and for people who prefer a console.</summary>
public static class Cli
{
    public const int Ok = 0, StepFailed = 1, BadArgs = 2, ToolsMissing = 3, NoSpace = 4, Cancelled = 5,
                     UpdateRefused = 6, UpdateAvailable = 10;

    public static async Task<int> Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Program.Usage);
            return Ok;
        }
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool yes = args.Contains("--yes"), dry = args.Contains("--dry-run");
        bool update = args.Contains("--update"), check = args.Contains("--check-update"), stash = args.Contains("--stash");

        using var log = new Log();
        log.Line += l => Console.WriteLine(l);

        // Which install: --repo <existing clone>, else --dir (a setup folder,
        // or a clone), else the last one used, else the default folder.
        var target = Arg("--repo") ?? Arg("--dir") ?? LauncherSettings.Load().InstallDir ?? Util.DefaultInstallDir();
        target = Path.GetFullPath(target);
        var (installDir, clone) = Setup.Resolve(target);
        log.Info($"Blinx 2 setup {Program.Version}");
        if (await ReleaseCheck.NewerAsync() is { } rel)
            log.Info($"A newer Blinx 2 setup is available: {rel.Tag}, {rel.Url}");

        log.Info("Checking dependencies:");
        var tc = Toolchain.Detect(log);

        if (clone != null)
        {
            var gameDir = Arg("--game-dir") ?? Setup.FindGameDir(clone);
            if (gameDir == null || !File.Exists(Path.Combine(gameDir, "default.xbe")))
            {
                log.Error($"{clone} is a clone of the project, but its game files were not found. Pass --game-dir <folder with default.xbe>.");
                return BadArgs;
            }
            if (dry)
            {
                log.Info($"Dry run: would adopt the existing clone {clone} with game files {gameDir}.");
                return Ok;
            }
            installDir = Setup.Adopt(clone, gameDir, tc, log);
        }
        else if (installDir == null)
        {
            // A new install into this folder.
            if (Arg("--repo") != null) { log.Error($"{target} is not a clone of blinx-2-recompile-full."); return BadArgs; }
            if (Program.CheckInstallDir(target) is { } bad) { log.Error(bad); return BadArgs; }
            installDir = target;
        }
        if (!dry) log.OpenFile(Path.Combine(installDir, "setup.log"));

        var state = SetupState.Load(installDir);
        var isoArg = Arg("--iso") ?? state.IsoPath;
        string? iso = null;
        bool extracted = File.Exists(Path.Combine(state.GameDir ?? Path.Combine(installDir, "game_files"), "default.xbe"));
        if (!string.IsNullOrEmpty(isoArg))
        {
            iso = Setup.ResolveIso(isoArg, out var problem);
            if (iso == null && !extracted) { log.Error(problem ?? "No disc image."); return BadArgs; }
        }
        else if (!extracted)
        {
            log.Error("Pass --iso <your Blinx 2 .iso/.xiso>.");
            Console.WriteLine(Program.Usage);
            return BadArgs;
        }
        log.Info($"Install: {installDir}" + (state.RepoDir != null ? $" (adopted clone {state.RepoDir})" : "") +
                 $", disc image {iso ?? "(already extracted)"}");

        var missing = tc.Dependencies.Where(d => !d.Found).ToList();
        var runner = new ProcRunner(log);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (missing.Count > 0 && !check)
        {
            if (!tc.Winget && !dry)
            {
                log.Error("winget is not available; install these by hand: " +
                          string.Join("; ", missing.Select(d => $"{d.Name}: {d.ManualUrl}")));
                return ToolsMissing;
            }
            if (!yes && !dry)
            {
                log.Error("Missing dependencies (run again with --yes to install them with winget):");
                foreach (var d in missing) log.Info("  " + DepInstaller.Describe(d));
                return ToolsMissing;
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
                    return ToolsMissing;
                }
            }
        }

        var setup = new Setup(installDir, iso ?? "", log, runner, tc) { MakeShortcuts = !args.Contains("--no-shortcuts") };
        setup.Warning += w => log.Info("WARNING: " + w);
        try
        {
            if (update || check)
            {
                var u = setup.MakeUpdater();
                if (u == null)
                {
                    log.Error("There is no clone of the project to update in this install yet; run the setup first.");
                    return BadArgs;
                }
                var status = await u.CheckAsync(fetch: true, cts.Token);
                foreach (var l in status.Describe().Split(Environment.NewLine)) log.Info(l);
                if (check) return status.UpdateAvailable ? UpdateAvailable : Ok;
                if (status.Blockers.Count > 0 || (status.Dirty.Count > 0 && !stash))
                {
                    log.Error(status.Blockers.Count > 0
                        ? "Not updated: " + string.Join(" ", status.Blockers)
                        : "Not updated: there are local changes. Run again with --stash to save them with git stash and update.");
                    return UpdateRefused;
                }
                if (dry)
                    log.Info($"Dry run: would update to {status.Short(status.Latest ?? "")}" +
                             (status.Dirty.Count > 0 ? ", stashing the local changes first" : "") + ".");
                else
                    foreach (var st in await setup.UpdateAsync(status, stash, cts.Token)) log.Info("Saved: " + st);
            }

            var pending = setup.Pending();
            long need = setup.SpaceNeeded(pending), free = FreeSpace(setup.RepoDir);
            log.Info($"Steps to run: {pending.Count} of {setup.Steps.Count}, about {Util.FormatDuration(setup.Estimate(pending))} " +
                     $"on a recent PC; needs about {Util.FormatSize(need)}, {Util.FormatSize(free)} free.");
            foreach (var s in pending) log.Info($"  - {s.Title}");
            if (dry) return Ok;
            if (free < need)
            {
                log.Error($"Not enough free disk space: {Util.FormatSize(need)} needed, {Util.FormatSize(free)} free.");
                return NoSpace;
            }
            LauncherSettings.Save(new LauncherSettings.Data { InstallDir = state.RepoDir ?? installDir, IsoPath = iso });
            await setup.RunAsync(cts.Token);
            log.Info($"Done. The game: {setup.ExePath}");
            return Ok;
        }
        catch (OperationCanceledException)
        {
            runner.KillAll();
            log.Error("Cancelled. Run the setup again to continue where it stopped.");
            return Cancelled;
        }
        catch (Exception e) when (e is SetupException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            log.Error(e.Message);
            return StepFailed;
        }
    }

    public static long FreeSpace(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch (Exception) { return long.MaxValue; }
    }
}
