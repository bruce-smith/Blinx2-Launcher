using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Blinx2Launcher;

/// <summary>One step of the setup.</summary>
public sealed class Step
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Typical duration on a recent PC, for the estimate.</summary>
    public required TimeSpan Typical { get; init; }
    /// <summary>What the step's result depends on; the step reruns when it changes.</summary>
    public required Func<string> Key { get; init; }
    public required Func<CancellationToken, Task> Run { get; init; }
    /// <summary>When this step runs, every later step with Cascade runs too
    /// (the pipeline and the build use the previous steps' output).</summary>
    public bool Cascade { get; init; }
}

/// <summary>
/// The setup itself: extract the ISO, clone the project, run the
/// recompiler pipeline exactly as the project documents it, build, and
/// make shortcuts. The UI and the --cli mode both drive this.
/// </summary>
public sealed class Setup
{
    // ---- Fixed inputs ------------------------------------------------------

    public const string RepoUrl = "https://github.com/bruce-smith/blinx-2-recompile-full.git";

    // xdvdfs (MIT, github.com/antangelo/xdvdfs), the official Windows release.
    public const string XdvdfsVersion = "0.8.3";
    public const string XdvdfsUrl =
        "https://github.com/antangelo/xdvdfs/releases/download/v0.8.3/xdvdfs-windows-1cc850bf1b3487fad7ec7c9eed01d83e8fc75ba4.zip";
    public const string XdvdfsZipSha256 = "008b1b3a63f1ecb00d1ed51ab154053fd0f7c41c68e973455bbe77ab989a7cc5";
    public const string XdvdfsExeSha256 = "df5f19954ef706c130c256546bf67cf7725611bdb30d1815577187007a6f907a";

    // The pipeline's Python dependency (the version the project uses).
    public const string CapstoneSpec = "capstone==5.0.7";

    /// <summary>Retail NTSC-U Blinx 2: the seeds and overrides are for this XBE.</summary>
    public const uint BlinxTitleId = 0x4D530065;

    /// <summary>Sections with x86 code besides .text (AGENTS.md: load-bearing).</summary>
    public const string ExtraSections = "DSOUND,D3D,D3DX,XGRPH,XPP,SRCADV,SRCED,SRCAC,PSFD_I,PSFD_B,PSFD_P,PSFD00";

    /// <summary>Unresolved stubs expected from a correct run (188 in 2026-10);
    /// far more means the pipeline missed code.</summary>
    public const int ExpectedStubs = 188, StubWarnAbove = 400;

    // ---- Paths -------------------------------------------------------------

    public string InstallDir { get; }
    public string IsoPath { get; }
    public string GameDir => Path.Combine(InstallDir, "game_files");
    public string RepoDir => Path.Combine(InstallDir, "blinx-2-recompile-full");
    public string XboxRecomp => Path.Combine(RepoDir, "xboxrecomp");
    public string GameProject => Path.Combine(RepoDir, "blinx2-recomp");
    public string BuildDir => Path.Combine(GameProject, "build");
    public string ExePath => Path.Combine(BuildDir, "Release", "blinx_2_recomp.exe");
    public string VenvDir => Path.Combine(InstallDir, "python");
    public string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");
    public string XdvdfsDir => Path.Combine(InstallDir, "tools", "xdvdfs");
    public string XdvdfsExe => Path.Combine(XdvdfsDir, "xdvdfs.exe");
    public string Xbe => Path.Combine(GameDir, "default.xbe");
    public string LogPath => Path.Combine(InstallDir, "setup.log");

    private readonly Log _log;
    private readonly ProcRunner _run;
    private Toolchain _tc;
    private SetupState _state;

    public IReadOnlyList<Step> Steps { get; }

    /// <summary>Desktop and Start-menu shortcuts in the last step (off for tests).</summary>
    public bool MakeShortcuts { get; set; } = true;

    // ---- Progress, for the UI ----------------------------------------------

    /// <summary>A step started; the bool says whether it will actually run (false = already done).</summary>
    public event Action<Step, bool>? StepStarted;
    /// <summary>A step finished (true) or failed (false).</summary>
    public event Action<Step, bool>? StepFinished;
    /// <summary>A step knows how far it is (0..1), e.g. the build counting compiled files.</summary>
    public event Action<Step, double, string?>? StepProgress;
    /// <summary>Something worth showing that is not an error.</summary>
    public event Action<string>? Warning;

    public Setup(string installDir, string isoPath, Log log, ProcRunner run, Toolchain tc)
    {
        InstallDir = Path.GetFullPath(installDir);
        IsoPath = isoPath;
        _log = log;
        _run = run;
        _tc = tc;
        _state = SetupState.Load(InstallDir);

        Steps = new List<Step>
        {
            new() { Id = "xdvdfs", Title = "Download the disc extractor (xdvdfs)", Typical = TimeSpan.FromSeconds(10),
                    Key = () => XdvdfsVersion, Run = GetXdvdfs },
            new() { Id = "extract", Title = "Extract the game files from your disc image", Typical = TimeSpan.FromMinutes(2),
                    Key = () => "1", Run = Extract },
            new() { Id = "clone", Title = "Download the project source (git clone)", Typical = TimeSpan.FromMinutes(2),
                    Key = () => "1", Run = Clone },
            new() { Id = "python", Title = "Set up Python for the recompiler", Typical = TimeSpan.FromMinutes(1),
                    Key = () => CapstoneSpec + "|" + _tc.Python, Run = PythonEnv },
            new() { Id = "xbe_parser", Title = "Recompiler 1/5: read the game's executable", Typical = TimeSpan.FromSeconds(15),
                    Key = Revision, Cascade = true, Run = ct => Py(ct, "tools.xbe_parser", Xbe, "--json", Path.Combine(GameDir, "mygame_analysis.json")) },
            new() { Id = "disasm", Title = "Recompiler 2/5: disassemble", Typical = TimeSpan.FromMinutes(1.5),
                    Key = Revision, Cascade = true, Run = ct => Py(ct, "tools.disasm", Xbe, "--text-only",
                        "--extra-sections", ExtraSections,
                        "--seed-functions", Path.Combine(GameProject, "seeds", "seed_functions.json")) },
            new() { Id = "func_id", Title = "Recompiler 3/5: identify library functions", Typical = TimeSpan.FromSeconds(40),
                    Key = Revision, Cascade = true, Run = ct => Py(ct, "tools.func_id", Xbe, "-v") },
            new() { Id = "abi_analysis", Title = "Recompiler 4/5: analyse calling conventions", Typical = TimeSpan.FromSeconds(30),
                    Key = Revision, Cascade = true, Run = ct => Py(ct, "tools.abi_analysis", Xbe, "-v") },
            new() { Id = "recomp", Title = "Recompiler 5/5: translate the game to C", Typical = TimeSpan.FromMinutes(6),
                    Key = Revision, Cascade = true, Run = Recomp },
            new() { Id = "configure", Title = "Configure the build (CMake)", Typical = TimeSpan.FromMinutes(1.5),
                    Key = () => Revision() + "|" + _tc.Generator + "|" + _tc.VsPath, Cascade = true, Run = Configure },
            new() { Id = "build", Title = "Compile the game", Typical = TimeSpan.FromMinutes(17),
                    Key = Revision, Cascade = true, Run = Build },
            new() { Id = "finish", Title = "Point the game at its files, create shortcuts", Typical = TimeSpan.FromSeconds(5),
                    Key = Revision, Cascade = true, Run = Finish },
        };
    }

    public void UpdateToolchain(Toolchain tc) => _tc = tc;

    // ---- Planning ----------------------------------------------------------

    private string? _revision;

    /// <summary>The project revision: superproject and submodule commits.
    /// Steps that depend on it rerun after an update.</summary>
    public string Revision()
    {
        if (_revision != null) return _revision;
        if (_tc.Git == null || !Directory.Exists(Path.Combine(RepoDir, ".git"))) return "none";
        var head = ProcRunner.Capture(_tc.Git, new[] { "-C", RepoDir, "rev-parse", "HEAD" })?.Trim();
        var subs = ProcRunner.Capture(_tc.Git, new[] { "-C", RepoDir, "submodule", "status", "--recursive" });
        if (head == null || subs == null) return "none";
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(head + "\n" + subs)))[..16].ToLowerInvariant();
        return _revision = head[..Math.Min(12, head.Length)] + "-" + h;
    }

    private void ForgetRevision() => _revision = null;

    /// <summary>The steps that will run (for the estimate and the disk check).</summary>
    public List<Step> Pending()
    {
        var list = new List<Step>();
        bool cascade = false;
        foreach (var s in Steps)
        {
            // Steps after the clone depend on the repo; before it exists they all run.
            bool done = _state.Done.TryGetValue(s.Id, out var k) && k == s.Key() && !(cascade && s.Cascade);
            if (!done) { list.Add(s); if (s.Cascade) cascade = true; }
        }
        return list;
    }

    public bool IsComplete => Pending().Count == 0 && File.Exists(ExePath);

    public TimeSpan Estimate(IEnumerable<Step> steps) =>
        TimeSpan.FromTicks(steps.Sum(s => s.Typical.Ticks));

    /// <summary>Disk space the pending steps need. Measured on a full run
    /// (2026-10-18): game files 3.2 GB (the ISO's size), clone ~0.2 GB,
    /// pipeline output 0.13 GB, generated C 0.13 GB, build 0.6 GB; 4.1 GB in
    /// all. With a margin for the compiler's temporary files.</summary>
    public long SpaceNeeded(List<Step>? pending = null)
    {
        pending ??= Pending();
        const long GB = 1L << 30, MB = 1L << 20;
        long need = 0;
        foreach (var s in pending)
            need += s.Id switch
            {
                "xdvdfs" => 10 * MB,
                "extract" => File.Exists(IsoPath) ? new FileInfo(IsoPath).Length : 3 * GB,
                "clone" => 300 * MB,
                "python" => 50 * MB,
                "disasm" or "func_id" or "abi_analysis" => 100 * MB,
                "recomp" => 300 * MB,
                "configure" => 300 * MB,
                "build" => 1 * GB,
                _ => 0,
            };
        return need + 500 * MB;
    }

    // ---- Running -----------------------------------------------------------

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(InstallDir);
        _state.IsoPath = IsoPath;
        _state.Save(InstallDir);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // no sleep mid-build
        try
        {
            bool cascade = false;
            foreach (var s in Steps)
            {
                ct.ThrowIfCancellationRequested();
                bool done = _state.Done.TryGetValue(s.Id, out var k) && k == s.Key() && !(cascade && s.Cascade);
                StepStarted?.Invoke(s, !done);
                if (done)
                {
                    _log.Info($"== {s.Title}: already done");
                    StepFinished?.Invoke(s, true);
                    continue;
                }
                if (s.Cascade) cascade = true;
                _log.Info($"== {s.Title}");
                _state.Done.Remove(s.Id);
                _state.Save(InstallDir);
                var t0 = DateTime.Now;
                try
                {
                    await s.Run(ct);
                }
                catch
                {
                    StepFinished?.Invoke(s, false);
                    throw;
                }
                if (s.Id == "clone") ForgetRevision();
                _state.Done[s.Id] = s.Key();
                _state.Save(InstallDir);
                _log.Info($"== {s.Title}: done in {Util.FormatDuration(DateTime.Now - t0)}");
                StepFinished?.Invoke(s, true);
            }
        }
        finally
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        }
    }

    /// <summary>Pulls the latest project commits; the next run then
    /// regenerates and rebuilds whatever they change.</summary>
    public async Task UpdateAsync(CancellationToken ct)
    {
        if (_tc.Git == null) throw new SetupException("Git is not installed.");
        _log.Info("== Update the project source");
        await _run.RunCheckedAsync(_tc.Git, new[] { "-C", RepoDir, "pull", "--ff-only" }, ct, what: "git pull");
        await _run.RunCheckedAsync(_tc.Git, new[] { "-C", RepoDir, "submodule", "update", "--init", "--recursive" }, ct,
            what: "git submodule update");
        ForgetRevision();
    }

    private void MarkCreated(string dir)
    {
        if (!_state.Created.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            _state.Created.Add(dir);
            _state.Save(InstallDir);
        }
    }

    private bool WeCreated(string dir) => _state.Created.Contains(dir, StringComparer.OrdinalIgnoreCase);

    // ---- xdvdfs ------------------------------------------------------------

    private async Task GetXdvdfs(CancellationToken ct)
    {
        if (File.Exists(XdvdfsExe) && Util.Sha256File(XdvdfsExe) == XdvdfsExeSha256)
        {
            _log.Info($"xdvdfs {XdvdfsVersion} is already here: {XdvdfsExe}");
            return;
        }
        Directory.CreateDirectory(XdvdfsDir);
        var zip = Path.Combine(XdvdfsDir, "xdvdfs-download.zip");
        _log.Info($"Downloading {XdvdfsUrl}");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Blinx2Setup/" + Program.Version);
            using var resp = await http.GetAsync(XdvdfsUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zip);
            await resp.Content.CopyToAsync(fs, ct);
        }
        var sha = Util.Sha256File(zip);
        if (sha != XdvdfsZipSha256)
        {
            File.Delete(zip);
            throw new SetupException($"The downloaded xdvdfs does not match its expected checksum (got {sha}). " +
                                     "The download was damaged or changed; try again later.");
        }
        _log.Info("Checksum OK (SHA-256 " + sha + ")");
        using (var a = ZipFile.OpenRead(zip))
            foreach (var e in a.Entries)
                if (e.Name is "xdvdfs.exe" or "LICENSE")
                    e.ExtractToFile(Path.Combine(XdvdfsDir, e.Name), overwrite: true);
        File.Delete(zip);
        if (!File.Exists(XdvdfsExe) || Util.Sha256File(XdvdfsExe) != XdvdfsExeSha256)
            throw new SetupException("xdvdfs.exe in the download does not match its expected checksum.");
    }

    // ---- ISO ---------------------------------------------------------------

    /// <summary>The disc image the user chose: an .iso/.xiso, or a folder
    /// holding exactly one (or one with "blinx" in its name).</summary>
    public static string? ResolveIso(string path, out string? problem)
    {
        problem = null;
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) { problem = "That file or folder does not exist."; return null; }
        var found = new List<string>();
        foreach (var opt in new[] { SearchOption.TopDirectoryOnly })
            foreach (var ext in new[] { "*.iso", "*.xiso" })
                found.AddRange(Directory.GetFiles(path, ext, opt));
        if (found.Count == 0)
            foreach (var sub in Directory.GetDirectories(path))
                foreach (var ext in new[] { "*.iso", "*.xiso" })
                    try { found.AddRange(Directory.GetFiles(sub, ext)); } catch (UnauthorizedAccessException) { }
        found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count == 1) return found[0];
        var blinx = found.Where(f => Path.GetFileName(f).Contains("blinx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (blinx.Count == 1) return blinx[0];
        problem = found.Count == 0
            ? "No .iso or .xiso file in that folder."
            : $"That folder has {found.Count} disc images; please choose the Blinx 2 file itself.";
        return null;
    }

    /// <summary>The title ID from an XBE's certificate, or null if it is not an XBE.</summary>
    public static uint? XbeTitleId(string xbe)
    {
        try
        {
            using var f = File.OpenRead(xbe);
            var h = new byte[0x180];
            if (f.Read(h, 0, h.Length) < h.Length || Encoding.ASCII.GetString(h, 0, 4) != "XBEH") return null;
            uint baseAddr = BitConverter.ToUInt32(h, 0x104);
            uint certAddr = BitConverter.ToUInt32(h, 0x118);
            f.Seek(certAddr - baseAddr + 8, SeekOrigin.Begin);
            var id = new byte[4];
            if (f.Read(id, 0, 4) < 4) return null;
            return BitConverter.ToUInt32(id, 0);
        }
        catch (IOException) { return null; }
    }

    private void CheckXbe(string xbe)
    {
        var id = XbeTitleId(xbe);
        if (id == null)
            throw new SetupException($"{xbe} is not an Xbox executable. Is the disc image a Blinx 2 disc?");
        if (id != BlinxTitleId)
            throw new SetupException($"This disc is not the North American (NTSC-U) Blinx 2: title ID {id:X8}, " +
                                     $"expected {BlinxTitleId:X8}. The project only supports that version.");
        _log.Info($"default.xbe: title ID {id:X8}, Blinx 2 (NTSC-U)");
    }

    private async Task Extract(CancellationToken ct)
    {
        if (File.Exists(Xbe))
        {
            _log.Info($"The game files are already in {GameDir}");
            CheckXbe(Xbe);
            return;
        }
        if (!File.Exists(IsoPath))
            throw new SetupException($"The disc image {IsoPath} is not there any more.");
        if (Directory.Exists(GameDir) && Directory.EnumerateFileSystemEntries(GameDir).Any())
            throw new SetupException($"{GameDir} exists but has no default.xbe. Empty or remove that folder and try again.");

        var partial = GameDir + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);   // ours, from an interrupted run
        MarkCreated(partial);
        long total = new FileInfo(IsoPath).Length;
        var step = Steps.First(s => s.Id == "extract");
        using var timer = new System.Threading.Timer(_ =>
        {
            try
            {
                long have = Directory.EnumerateFiles(partial, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                StepProgress?.Invoke(step, Math.Min(1.0, have / (double)Math.Max(1, total)), Util.FormatSize(have));
            }
            catch (Exception) { }
        }, null, 1000, 2000);
        await _run.RunCheckedAsync(XdvdfsExe, new[] { "unpack", IsoPath, partial }, ct, what: "Extracting the disc image");
        timer.Dispose();
        var xbe = Path.Combine(partial, "default.xbe");
        if (!File.Exists(xbe))
            throw new SetupException("The disc image has no default.xbe. Is it an Xbox game disc image?");
        CheckXbe(xbe);
        if (Directory.Exists(GameDir)) Directory.Delete(GameDir);   // empty (checked above)
        Directory.Move(partial, GameDir);
        MarkCreated(GameDir);
    }

    // ---- Source ------------------------------------------------------------

    private async Task Clone(CancellationToken ct)
    {
        var git = _tc.Git ?? throw new SetupException("Git is not installed.");
        if (Directory.Exists(Path.Combine(RepoDir, ".git")) &&
            ProcRunner.Capture(git, new[] { "-C", RepoDir, "rev-parse", "--verify", "HEAD" }) != null)
        {
            _log.Info("The project is already cloned; fetching any missing submodules.");
            await _run.RunCheckedAsync(git, new[] { "-C", RepoDir, "submodule", "update", "--init", "--recursive" }, ct,
                what: "git submodule update");
            return;
        }
        if (Directory.Exists(RepoDir))
        {
            if (!WeCreated(RepoDir) && Directory.EnumerateFileSystemEntries(RepoDir).Any())
                throw new SetupException($"{RepoDir} already exists and is not a complete clone. Remove it and try again.");
            _log.Info("Removing an interrupted clone.");
            ForceDelete(RepoDir);
        }
        MarkCreated(RepoDir);
        await _run.RunCheckedAsync(git, new[] { "clone", "--recurse-submodules", "--progress", RepoUrl, RepoDir }, ct,
            what: "git clone");
    }

    /// <summary>Deletes a folder this setup created (git marks objects read-only).</summary>
    private static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    // ---- Python ------------------------------------------------------------

    private async Task PythonEnv(CancellationToken ct)
    {
        var py = _tc.Python ?? throw new SetupException("Python is not installed.");
        if (File.Exists(VenvPython) &&
            ProcRunner.Capture(VenvPython, new[] { "-c", "import capstone; print(capstone.__version__)" })?.Trim() is { } v &&
            CapstoneSpec.EndsWith("==" + v))
        {
            _log.Info($"Python environment ready ({VenvDir}, capstone {v}).");
            return;
        }
        if (!File.Exists(VenvPython))
        {
            if (Directory.Exists(VenvDir))
            {
                if (!WeCreated(VenvDir)) throw new SetupException($"{VenvDir} exists but is not a Python environment. Remove it and try again.");
                Directory.Delete(VenvDir, true);
            }
            MarkCreated(VenvDir);
            // A private environment: nothing is installed into the user's Python.
            await _run.RunCheckedAsync(py, new[] { "-m", "venv", VenvDir }, ct, what: "Creating the Python environment");
        }
        await _run.RunCheckedAsync(VenvPython, new[] { "-m", "pip", "install", "--disable-pip-version-check",
            "--no-input", CapstoneSpec }, ct, what: "Installing capstone");
    }

    // ---- Pipeline ----------------------------------------------------------

    /// <summary>Runs one pipeline tool. cwd must be xboxrecomp: the tools read
    /// and write their intermediate output relative to it.</summary>
    private Task Py(CancellationToken ct, string module, params string[] args) =>
        _run.RunCheckedAsync(VenvPython, new[] { "-m", module }.Concat(args), ct,
            new ProcRunner.Options { WorkingDir = XboxRecomp }, what: module);

    private async Task Recomp(CancellationToken ct)
    {
        var gen = Path.Combine(GameProject, "src", "recomp", "gen");
        var step = Steps.First(s => s.Id == "recomp");
        var t0 = DateTime.Now;
        // No progress output while it translates: report elapsed time against
        // the typical duration, capped below 100%.
        using var timer = new System.Threading.Timer(_ =>
        {
            var f = (DateTime.Now - t0).TotalSeconds / step.Typical.TotalSeconds;
            StepProgress?.Invoke(step, Math.Min(0.95, f), null);
        }, null, 1000, 5000);
        await Py(ct, "tools.recomp", Xbe, "--all", "--split", "250",
            "--gen-dir", gen,
            "--exclude-manual", Path.Combine(GameProject, "src", "recomp_manual.c"),
            "--coalesce-functions", Path.Combine(GameProject, "seeds", "coalesce.json"),
            "--yield-points", Path.Combine(GameProject, "seeds", "yield_points.json"),
            "--loop-heads");
        if (!File.Exists(Path.Combine(gen, "recomp_dispatch.c")))
            throw new SetupException("The recompiler finished but wrote no recomp_dispatch.c.");

        var summary = Path.Combine(XboxRecomp, "tools", "recomp", "output", "summary.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summary));
            var r = doc.RootElement;
            int stubs = r.GetProperty("unresolved_stubs").GetInt32();
            _log.Info($"Recompiled {r.GetProperty("translated").GetInt32()} functions " +
                      $"({r.GetProperty("failed").GetInt32()} failed), {stubs} unresolved stubs (about {ExpectedStubs} expected).");
            if (stubs > StubWarnAbove)
                Warning?.Invoke($"The recompiler left {stubs} unresolved stubs, far more than the usual ~{ExpectedStubs}. " +
                                "The game may crash; please report this with the log.");
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException)
        {
            _log.Info($"(could not read {summary}: {e.Message})");
        }
    }

    // ---- Build -------------------------------------------------------------

    private async Task Configure(CancellationToken ct)
    {
        var cmake = _tc.CMake ?? throw new SetupException("CMake is not installed.");
        if (_tc.Generator == null || _tc.VsPath == null)
            throw new SetupException("No Visual Studio C++ tools that CMake can use were found.");

        // A build folder made with another generator or Visual Studio cannot be
        // reused: drop the cache (the settings file next to the exe stays).
        var cache = Path.Combine(BuildDir, "CMakeCache.txt");
        if (File.Exists(cache))
        {
            var text = File.ReadAllText(cache);
            var gen = Regex.Match(text, @"^CMAKE_GENERATOR:INTERNAL=(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var inst = Regex.Match(text, @"^CMAKE_GENERATOR_INSTANCE:INTERNAL=(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            if (gen != _tc.Generator || !string.Equals(Path.GetFullPath(inst.Length > 0 ? inst : "."),
                    Path.GetFullPath(_tc.VsPath), StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"The build folder was configured for {gen} ({inst}); starting it over for {_tc.Generator}.");
                File.Delete(cache);
                var cf = Path.Combine(BuildDir, "CMakeFiles");
                if (Directory.Exists(cf)) Directory.Delete(cf, true);
            }
        }
        await _run.RunCheckedAsync(cmake, new[]
        {
            "-S", GameProject, "-B", BuildDir,
            "-G", _tc.Generator, "-A", "x64",
            "-DCMAKE_GENERATOR_INSTANCE=" + _tc.VsPath,
            // The exe also reads game_dir from blinx2_settings.ini (written in
            // the last step); this is the fallback baked into it.
            "-DBLINX2_GAME_DIR=" + Util.ForwardSlashes(GameDir),
        }, ct, what: "CMake configure");
    }

    private async Task Build(CancellationToken ct)
    {
        var cmake = _tc.CMake ?? throw new SetupException("CMake is not installed.");
        var step = Steps.First(s => s.Id == "build");
        var gen = Path.Combine(GameProject, "src", "recomp", "gen");
        int total = Directory.Exists(gen) ? Directory.GetFiles(gen, "*.c").Length : 0;
        int compiled = 0;
        var errors = new List<string>();
        // MSBuild prints each source file's name as cl compiles it.
        var genNames = Directory.Exists(gen)
            ? Directory.GetFiles(gen, "*.c").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>();
        var error = new Regex(@"\berror (C|LNK|MSB)\d+|fatal error", RegexOptions.IgnoreCase);
        var t0 = DateTime.Now;
        int rc = await _run.RunAsync(cmake, new[] { "--build", BuildDir, "--config", "Release", "--", "/nodeReuse:false" }, ct,
            new ProcRunner.Options
            {
                OnLine = line =>
                {
                    if (total > 0 && genNames.Remove(line.Trim()))
                    {
                        compiled++;
                        StepProgress?.Invoke(step, Math.Min(0.97, compiled / (double)total),
                            $"{compiled} of {total} generated files compiled");
                    }
                    if (error.IsMatch(line) && errors.Count < 20) errors.Add(line.Trim());
                }
            });
        if (rc != 0)
            throw new SetupException("The build failed." + (errors.Count > 0
                ? " First errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Take(8)) : ""));
        if (!File.Exists(ExePath))
            throw new SetupException($"The build finished but {ExePath} is missing.");
    }

    // ---- Finish ------------------------------------------------------------

    private Task Finish(CancellationToken ct)
    {
        var exeDir = Path.GetDirectoryName(ExePath)!;
        WriteGameDir(Path.Combine(exeDir, "blinx2_settings.ini"), GameDir);
        _log.Info($"blinx2_settings.ini: game_dir={GameDir}");

        // Keep a copy of this setup next to the install for updates and repairs.
        var self = Environment.ProcessPath;
        var selfCopy = Path.Combine(InstallDir, "Blinx2Setup.exe");
        if (self != null && !string.Equals(Path.GetFullPath(self), selfCopy, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(self, selfCopy, overwrite: true); }
            catch (IOException e) { _log.Info($"(could not copy the setup to {selfCopy}: {e.Message})"); }
        }

        if (MakeShortcuts)
            foreach (var (lnk, target, _) in Shortcuts.Create(ExePath, File.Exists(selfCopy) ? selfCopy : self))
                _log.Info($"Shortcut: {lnk} -> {target}");
        return Task.CompletedTask;
    }

    /// <summary>Sets game_dir under [paths], keeping every other line (the
    /// game's menu stores its settings in the same file).</summary>
    public static void WriteGameDir(string ini, string gameDir)
    {
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>
        {
            "; Blinx 2 (recomp) settings. The in-game menu (Escape) writes the rest.",
        };
        int paths = lines.FindIndex(l => l.Trim().Equals("[paths]", StringComparison.OrdinalIgnoreCase));
        if (paths < 0)
        {
            lines.Add("[paths]");
            lines.Add("game_dir=" + gameDir);
        }
        else
        {
            int end = lines.FindIndex(paths + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            int key = lines.FindIndex(paths + 1, end - paths - 1, l => Regex.IsMatch(l, @"^\s*game_dir\s*="));
            if (key >= 0) lines[key] = "game_dir=" + gameDir;
            else lines.Insert(paths + 1, "game_dir=" + gameDir);
        }
        Util.WriteUtf8(ini, string.Join("\r\n", lines) + "\r\n");
    }
}
