namespace Blinx2Launcher;

/// <summary>Installs missing dependencies with winget, one after another.
/// winget asks for elevation (UAC) itself where a package needs it.</summary>
public static class DepInstaller
{
    public static string Describe(Dependency d) =>
        d.WingetId == null ? $"(install by hand: {d.ManualUrl})" : Util.QuoteCommand("winget", d.WingetArgs());

    public static async Task InstallAsync(IEnumerable<Dependency> missing, ProcRunner run, Log log, CancellationToken ct,
                                          bool dryRun = false)
    {
        foreach (var d in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (d.WingetId == null)
                throw new SetupException($"{d.Name} cannot be installed automatically. Download it from {d.ManualUrl}.");
            log.Info($"Installing {d.Name} ({d.InstallTime})");
            // BLINX2SETUP_DRY_INSTALL=1: log the winget command instead (tests).
            if (dryRun || Environment.GetEnvironmentVariable("BLINX2SETUP_DRY_INSTALL") == "1")
            {
                log.Info("  dry run, would run: " + Describe(d));
                continue;
            }
            // Detached: an installer that is half done must not be killed if the
            // setup window is closed.
            int rc = await run.RunAsync("winget", d.WingetArgs(), ct, new ProcRunner.Options { Detached = true });
            // winget's exit codes are not reliable for "already installed" and
            // "installed, reboot needed": the re-detection afterwards decides.
            if (rc != 0) log.Info($"  winget returned 0x{rc:X8}");
        }
    }
}
