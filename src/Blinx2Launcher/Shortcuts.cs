namespace Blinx2Launcher;

/// <summary>Desktop and Start-menu shortcuts (through the shell's
/// WScript.Shell object).</summary>
public static class Shortcuts
{
    // BLINX2SETUP_SHORTCUT_DIR puts both in one folder instead (for tests).
    private static readonly string? TestDir = Environment.GetEnvironmentVariable("BLINX2SETUP_SHORTCUT_DIR");

    public static string StartMenuDir => TestDir != null ? Path.Combine(TestDir, "Start menu") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Blinx 2");

    public static string DesktopLink => Path.Combine(TestDir ?? 
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Blinx 2.lnk");

    public static IEnumerable<(string lnk, string target, string desc)> Create(string gameExe, string? setupExe)
    {
        Directory.CreateDirectory(StartMenuDir);
        var list = new List<(string, string, string)>
        {
            (DesktopLink, gameExe, "Play Blinx 2 (recompiled)"),
            (Path.Combine(StartMenuDir, "Blinx 2.lnk"), gameExe, "Play Blinx 2 (recompiled)"),
        };
        if (setupExe != null)
            list.Add((Path.Combine(StartMenuDir, "Blinx 2 Setup (update or repair).lnk"), setupExe,
                "Update, repair or rebuild Blinx 2"));
        foreach (var (lnk, target, desc) in list)
            Make(lnk, target, desc);
        return list;
    }

    private static void Make(string lnk, string target, string desc)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new SetupException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = desc;
            sc.IconLocation = target + ",0";
            sc.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
