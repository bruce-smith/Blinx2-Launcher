using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Blinx2Launcher;

/// <summary>One thing the build needs, how it was found, and how to install it.</summary>
public sealed class Dependency
{
    public required string Name { get; init; }
    public required string Why { get; init; }
    public bool Found { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>winget package id; null if winget cannot install it.</summary>
    public string? WingetId { get; init; }
    public string[] WingetExtra { get; init; } = Array.Empty<string>();
    public required string ManualUrl { get; init; }
    /// <summary>Roughly how long the install takes, for the UI.</summary>
    public string InstallTime { get; init; } = "a few minutes";

    public string[] WingetArgs() =>
        new[] { "install", "--id", WingetId!, "--exact", "--source", "winget",
                "--accept-package-agreements", "--accept-source-agreements",
                "--disable-interactivity" }.Concat(WingetExtra).ToArray();
}

/// <summary>
/// The tools the setup found: where git, cmake, Python and Visual Studio
/// are, and which CMake generator matches the Visual Studio.
/// </summary>
public sealed class Toolchain
{
    public string? Git;
    public string? CMake;
    public Version? CMakeVersion;
    public string? Python;          // a Python 3.10+ interpreter (base, not the venv)
    public string? PythonVersion;
    public string? VsPath;          // Visual Studio instance with the C++ tools
    public string? VsName;
    public int VsMajor;
    public string? Generator;       // e.g. "Visual Studio 17 2022"
    public string? WindowsSdk;
    public bool VcRedist;
    public bool Winget;

    public static readonly Version MinCMake = new(3, 20);

    public List<Dependency> Dependencies { get; } = new();

    public static Toolchain Detect(Log? log = null)
    {
        var t = new Toolchain();
        t.DetectGit();
        t.DetectVs();
        t.DetectCMake();
        t.DetectPython();
        t.DetectSdk();
        t.DetectVcRedist();
        t.Winget = ProcRunner.Capture("winget", new[] { "--version" }) != null;
        t.BuildList();
        if (log != null)
            foreach (var d in t.Dependencies)
                log.Info($"  {(d.Found ? "found  " : "MISSING")} {d.Name}: {d.Detail}");
        return t;
    }

    public bool AllFound => Dependencies.All(d => d.Found);

    private void DetectGit()
    {
        var candidates = new[]
        {
            Util.Which("git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\cmd\git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe"),
        };
        foreach (var c in candidates)
            if (c != null && File.Exists(c) && ProcRunner.Capture(c, new[] { "--version" }) is { } v)
            {
                Git = c;
                break;
            }
    }

    private static string? VsWhere()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        return File.Exists(p) ? p : null;
    }

    private sealed record VsInstance(string Path, string Name, int Major);

    private List<VsInstance> _vsInstances = new();

    /// <summary>Every Visual Studio 2022 or newer (any edition, Build Tools
    /// included) that has the x64 C++ compiler.</summary>
    private void DetectVs()
    {
        var vswhere = VsWhere();
        if (vswhere == null) return;
        var json = ProcRunner.Capture(vswhere, new[] { "-all", "-products", "*", "-requires",
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-format", "json", "-utf8" });
        if (json == null) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var path = e.GetProperty("installationPath").GetString();
                var ver = e.GetProperty("installationVersion").GetString();
                var name = e.TryGetProperty("displayName", out var dn) ? dn.GetString() : path;
                if (e.TryGetProperty("isComplete", out var ic) && ic.ValueKind == JsonValueKind.False) continue;
                if (path == null || ver == null || !int.TryParse(ver.Split('.')[0], out int major)) continue;
                if (major < 17) continue;   // VS 2019 and older cannot build it
                if (!Directory.Exists(System.IO.Path.Combine(path, @"VC\Tools\MSVC"))) continue;
                _vsInstances.Add(new VsInstance(path, name ?? path, major));
            }
        }
        catch (JsonException) { }
        _vsInstances = _vsInstances.OrderByDescending(v => v.Major).ToList();
    }

    private static Version? CMakeVersionOf(string cmake)
    {
        var o = ProcRunner.Capture(cmake, new[] { "--version" });
        var m = o == null ? null : Regex.Match(o, @"cmake version (\d+)\.(\d+)(?:\.(\d+))?");
        return m is { Success: true } ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
    }

    /// <summary>The Visual Studio generators a cmake knows, by VS major version.</summary>
    private static Dictionary<int, string> GeneratorsOf(string cmake)
    {
        var d = new Dictionary<int, string>();
        var help = ProcRunner.Capture(cmake, new[] { "--help" }) ?? "";
        foreach (Match m in Regex.Matches(help, @"Visual Studio (\d+) (\d{4})"))
            d[int.Parse(m.Groups[1].Value)] = m.Value;
        return d;
    }

    /// <summary>
    /// A cmake 3.20+ that has a generator for one of the Visual Studios found:
    /// the cmake on PATH (or in Program Files) first, then the one Visual
    /// Studio bundles. Picks the newest Visual Studio it can drive.
    /// </summary>
    private void DetectCMake()
    {
        var standalone = new[]
        {
            Util.Which("cmake.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"CMake\bin\cmake.exe"),
        }.Where(p => p != null && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var candidates = new List<string>();
        candidates.AddRange(standalone!);
        foreach (var vs in _vsInstances)
        {
            var b = Path.Combine(vs.Path, @"Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe");
            if (File.Exists(b)) candidates.Add(b);
        }

        // Remember any cmake, even if no VS was found, for the report.
        foreach (var c in candidates)
        {
            var v = CMakeVersionOf(c);
            if (v == null || v < MinCMake) continue;
            CMake ??= c; CMakeVersion ??= v;
            var gens = GeneratorsOf(c);
            foreach (var vs in _vsInstances)
            {
                if (!gens.TryGetValue(vs.Major, out var gen)) continue;
                if (VsPath == null || vs.Major > VsMajor)
                {
                    CMake = c; CMakeVersion = v;
                    VsPath = vs.Path; VsName = vs.Name; VsMajor = vs.Major; Generator = gen;
                }
            }
        }
        if (VsPath == null && _vsInstances.Count > 0)
        {
            // A Visual Studio with C++ exists but no cmake can drive it yet.
            var vs = _vsInstances[0];
            VsPath = vs.Path; VsName = vs.Name; VsMajor = vs.Major;
        }
    }

    private void DetectPython()
    {
        // The pipeline is documented with "py -3" (Python 3.12). Prefer 3.12,
        // accept any 3.10+.
        const string probe = "import sys, venv; print(sys.executable); print('%d.%d.%d' % sys.version_info[:3])";
        var tries = new List<(string exe, string[] args)>
        {
            ("py", new[] { "-3.12", "-c", probe }),
            ("py", new[] { "-3", "-c", probe }),
        };
        foreach (var exe in new[] { Util.Which("python.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\python.exe"),
                     @"C:\Python312\python.exe" })
            if (exe != null && File.Exists(exe) && !exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                tries.Add((exe, new[] { "-c", probe }));

        foreach (var (exe, args) in tries)
        {
            var o = ProcRunner.Capture(exe, args);
            if (o == null) continue;
            var lines = o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) continue;
            var v = Version.TryParse(lines[1], out var pv) ? pv : null;
            if (v == null || v.Major != 3 || v.Minor < 10) continue;
            Python = lines[0];
            PythonVersion = lines[1];
            return;
        }
    }

    private void DetectSdk()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots")
                      ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots");
        if (k?.GetValue("KitsRoot10") is not string root) return;
        var inc = Path.Combine(root, "Include");
        if (!Directory.Exists(inc)) return;
        WindowsSdk = Directory.GetDirectories(inc, "10.*")
            .Where(d => File.Exists(Path.Combine(d, @"um\windows.h")))
            .Select(Path.GetFileName).OrderByDescending(v => Version.TryParse(v, out var x) ? x : new Version())
            .FirstOrDefault();
    }

    private void DetectVcRedist()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = b.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (k?.GetValue("Installed") is int i && i == 1) { VcRedist = true; return; }
        }
    }

    public const string BuildToolsOverride =
        "--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended";

    private void BuildList()
    {
        Dependencies.Add(new Dependency
        {
            Name = "Git",
            Why = "downloads the project's source code",
            Found = Git != null,
            Detail = Git ?? "not found",
            WingetId = "Git.Git",
            ManualUrl = "https://git-scm.com/download/win",
            InstallTime = "about 2 minutes",
        });
        bool vsOk = VsPath != null && WindowsSdk != null;
        Dependencies.Add(new Dependency
        {
            Name = "Visual Studio C++ build tools (2022 or newer) + Windows SDK",
            Why = "compiles the game",
            Found = vsOk,
            Detail = VsPath == null ? "no Visual Studio 2022 or newer with the C++ tools found"
                   : WindowsSdk == null ? $"{VsName}, but no Windows SDK"
                   : $"{VsName} ({VsPath}), Windows SDK {WindowsSdk}",
            WingetId = "Microsoft.VisualStudio.2022.BuildTools",
            WingetExtra = new[] { "--override", BuildToolsOverride },
            ManualUrl = "https://visualstudio.microsoft.com/visual-cpp-build-tools/",
            InstallTime = "15-30 minutes, about 6 GB",
        });
        bool cmakeOk = CMake != null && (VsPath == null || Generator != null);
        Dependencies.Add(new Dependency
        {
            Name = $"CMake {MinCMake.Major}.{MinCMake.Minor} or newer",
            Why = "configures the build",
            Found = cmakeOk,
            Detail = CMake == null ? "not found"
                   : Generator == null && VsPath != null ? $"{CMake} {CMakeVersion} has no generator for {VsName}"
                   : $"{CMake} {CMakeVersion}" + (Generator != null ? $", generator \"{Generator}\"" : ""),
            WingetId = "Kitware.CMake",
            ManualUrl = "https://cmake.org/download/",
            InstallTime = "about a minute",
        });
        Dependencies.Add(new Dependency
        {
            Name = "Python 3.10 or newer (3.12 recommended)",
            Why = "runs the recompiler",
            Found = Python != null,
            Detail = Python != null ? $"{Python} ({PythonVersion})" : "not found",
            WingetId = "Python.Python.3.12",
            ManualUrl = "https://www.python.org/downloads/windows/",
            InstallTime = "about 2 minutes",
        });
        Dependencies.Add(new Dependency
        {
            Name = "Microsoft Visual C++ Redistributable (x64)",
            Why = "the game needs it to start",
            Found = VcRedist,
            Detail = VcRedist ? "installed" : "not installed",
            WingetId = "Microsoft.VCRedist.2015+.x64",
            ManualUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            InstallTime = "under a minute",
        });
    }
}
