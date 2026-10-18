using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Blinx2Launcher;

public static class Util
{
    /// <summary>
    /// PATH as it is now in the registry (machine + user), merged with this
    /// process's own: tools installed by winget during this run are found
    /// without restarting the setup.
    /// </summary>
    public static string FreshPath()
    {
        var parts = new List<string>();
        void Add(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (var p in Environment.ExpandEnvironmentVariables(s).Split(';'))
                if (p.Length > 0 && !parts.Contains(p, StringComparer.OrdinalIgnoreCase)) parts.Add(p);
        }
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        Add(Environment.GetEnvironmentVariable("PATH"));
        return string.Join(";", parts);
    }

    /// <summary>Finds an exe on the (fresh) PATH.</summary>
    public static string? Which(string exe)
    {
        foreach (var dir in FreshPath().Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static string QuoteArg(string a) =>
        a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '(', ')' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    public static string QuoteCommand(string exe, IEnumerable<string> args) =>
        string.Join(" ", new[] { QuoteArg(exe) }.Concat(args.Select(QuoteArg)));

    private static readonly Regex Warning = new(@"\bwarning (C|LNK|MSB)\d+", RegexOptions.Compiled);

    /// <summary>MSVC's warnings are most of a build log; the pane and bug
    /// reports leave them out (the log file keeps them).</summary>
    public static bool IsCompilerWarning(string line) => Warning.IsMatch(line);

    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.Seconds} s";

    public static bool IsAscii(string s) => s.All(c => c < 128);

    /// <summary>The default install folder: a short ASCII path (the game reads
    /// its game_dir with narrow-character file APIs, at most 95 characters).</summary>
    public static string DefaultInstallDir()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(profile, "Blinx2");
        if (IsAscii(candidate) && candidate.Length <= 60) return candidate;
        return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Blinx2");
    }

    public static string ForwardSlashes(string p) => p.Replace('\\', '/');

    public static void WriteUtf8(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(false));
}
