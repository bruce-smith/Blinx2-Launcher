using System.Text.Json;

namespace Blinx2Launcher;

/// <summary>
/// Updating an installed project: what is installed, what origin has, and
/// moving the checkout forward without losing anything of the user's.
///
/// Only a fast-forward of the superproject's branch is done, followed by
/// "git submodule update", so the submodules land on the commits the
/// superproject pins. Tracked changes stop the update unless the user
/// chooses to stash them (git stash push -m blinx2setup-update-&lt;time&gt;;
/// the stash is never dropped). Untracked and ignored files (the build,
/// game files, saves, settings, recordings) are never touched by git here.
/// </summary>
public sealed class Updater
{
    public const string Remote = "origin";
    public const string Branch = "main";

    private readonly string _git;
    private readonly string _repo;
    private readonly Log _log;
    private readonly ProcRunner _run;

    public Updater(string git, string repoDir, Log log, ProcRunner run)
    {
        _git = git;
        _repo = repoDir;
        _log = log;
        _run = run;
    }

    public sealed class Status
    {
        public string Installed = "";             // superproject HEAD
        public string? Latest;                     // origin/main, null if unknown
        public DateTime? InstalledDate, LatestDate;
        public string? BranchName;                 // null when detached
        public int Behind, Ahead;
        public List<string> NewCommits = new();    // "abc1234 subject", newest first
        public List<(string path, string installed, string latest)> Submodules = new();
        /// <summary>Repos with tracked changes (stashable).</summary>
        public List<string> Dirty = new();
        /// <summary>Things a stash cannot fix (local commits, a submodule
        /// moved away from its pinned commit, another branch).</summary>
        public List<string> Blockers = new();
        public string? FetchError;

        public bool UpdateAvailable => Latest != null && Behind > 0;

        public string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

        public string Describe()
        {
            var lines = new List<string>
            {
                $"Installed: {Short(Installed)}" + (InstalledDate is { } d ? $" ({d:yyyy-MM-dd})" : "") +
                (Submodules.Count > 0 ? ", " + string.Join(", ", Submodules.Select(s => $"{s.path} {Short(s.installed)}")) : ""),
            };
            if (FetchError != null) lines.Add("Could not check for updates: " + FetchError);
            else if (Latest == null) lines.Add("Latest: unknown");
            else if (!UpdateAvailable) lines.Add("Up to date with the latest version.");
            else
            {
                lines.Add($"Latest: {Short(Latest)}" + (LatestDate is { } l ? $" ({l:yyyy-MM-dd})" : "") +
                          $", {Behind} new commit{(Behind == 1 ? "" : "s")}" +
                          (Submodules.Any(s => s.installed != s.latest)
                              ? "; changes " + string.Join(", ", Submodules.Where(s => s.installed != s.latest).Select(s => s.path))
                              : ""));
                foreach (var c in NewCommits.Take(10)) lines.Add("  " + c);
                if (NewCommits.Count > 10) lines.Add($"  ... and {NewCommits.Count - 10} more");
            }
            foreach (var b in Blockers) lines.Add("Cannot update: " + b);
            if (Dirty.Count > 0) lines.Add("Local changes in: " + string.Join(", ", Dirty));
            return string.Join(Environment.NewLine, lines);
        }
    }

    private string? Git(params string[] args) => ProcRunner.Capture(_git, new[] { "-C", _repo }.Concat(args), 60000)?.Trim();
    private string? GitIn(string dir, params string[] args) => ProcRunner.Capture(_git, new[] { "-C", dir }.Concat(args), 60000)?.Trim();

    /// <summary>Submodule paths from .gitmodules (top level; nested ones are
    /// checked through their parent).</summary>
    public List<string> SubmodulePaths()
    {
        var o = Git("config", "-f", ".gitmodules", "--get-regexp", @"^submodule\..*\.path$");
        return o == null ? new List<string>()
            : o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split(' ', 2)[1]).ToList();
    }

    private static DateTime? Date(string? unix) =>
        long.TryParse(unix, out var t) ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null;

    /// <summary>Fetches origin (unless fetch is false) and compares.</summary>
    public async Task<Status> CheckAsync(bool fetch, CancellationToken ct)
    {
        var s = new Status();
        if (fetch)
        {
            int rc = await _run.RunAsync(_git, new[] { "-C", _repo, "fetch", "--quiet", "--recurse-submodules=no", Remote }, ct);
            if (rc != 0) s.FetchError = "git fetch failed (no internet connection?)";
        }
        s.Installed = Git("rev-parse", "HEAD") ?? "";
        s.InstalledDate = Date(Git("log", "-1", "--format=%ct", "HEAD"));
        var latestRef = $"{Remote}/{Branch}";
        s.Latest = Git("rev-parse", "--verify", "--quiet", latestRef);
        if (s.Latest != null)
        {
            s.LatestDate = Date(Git("log", "-1", "--format=%ct", latestRef));
            int.TryParse(Git("rev-list", "--count", $"HEAD..{latestRef}"), out s.Behind);
            int.TryParse(Git("rev-list", "--count", $"{latestRef}..HEAD"), out s.Ahead);
            var log = Git("log", "--format=%h %s", "--no-merges", $"HEAD..{latestRef}");
            if (!string.IsNullOrEmpty(log)) s.NewCommits = log.Split('\n').Select(l => l.Trim()).ToList();
        }
        s.BranchName = Git("symbolic-ref", "--quiet", "--short", "HEAD");

        // Submodules: the commit checked out, the commit the superproject
        // pins now, and the one origin/main pins.
        foreach (var path in SubmodulePaths())
        {
            var pinned = Git("rev-parse", $"HEAD:{path}") ?? "";
            var latest = s.Latest != null ? Git("rev-parse", $"{latestRef}:{path}") ?? "" : pinned;
            var dir = Path.Combine(_repo, path);
            var head = Directory.Exists(dir) ? GitIn(dir, "rev-parse", "HEAD") : null;
            s.Submodules.Add((path, head ?? pinned, latest));
            if (head == null) continue;   // not checked out yet: the update initialises it
            // Checked out elsewhere than pinned: harmless if that commit is on
            // a branch (local or remote), since "submodule update" then loses
            // nothing. A detached commit that is on no branch would be left
            // behind, so that one stops the update.
            if (!string.IsNullOrEmpty(pinned) && head != pinned &&
                GitIn(dir, "symbolic-ref", "--quiet", "HEAD") == null &&
                string.IsNullOrEmpty(GitIn(dir, "for-each-ref", "--contains", head, "--format=%(refname)", "refs/heads", "refs/remotes")))
                s.Blockers.Add($"{path} is checked out at {s.Short(head)}, a commit that is on no branch. " +
                               "Put it on a branch (git branch <name>) or back with \"git submodule update\" first.");
            if (!string.IsNullOrEmpty(GitIn(dir, "status", "--porcelain", "--untracked-files=no")))
                s.Dirty.Add(path);
        }
        if (!string.IsNullOrEmpty(Git("status", "--porcelain", "--untracked-files=no", "--ignore-submodules=all")))
            s.Dirty.Insert(0, "the project root");

        if (s.BranchName == null)
            s.Blockers.Add($"the project is not on a branch (detached HEAD); check out {Branch} first.");
        else if (s.BranchName != Branch)
            s.Blockers.Add($"the project is on branch {s.BranchName}, not {Branch}.");
        if (s.Ahead > 0)
            s.Blockers.Add($"the project has {s.Ahead} local commit(s) that are not on {Remote}/{Branch}.");
        return s;
    }

    /// <summary>Moves the checkout to origin/main. Returns the names of the
    /// stashes made (empty if none).</summary>
    public async Task<List<string>> ApplyAsync(Status s, bool stash, CancellationToken ct)
    {
        if (s.Blockers.Count > 0)
            throw new SetupException("The project cannot be updated automatically: " + string.Join(" ", s.Blockers));
        if (s.Dirty.Count > 0 && !stash)
            throw new SetupException("Your copy of the project has local changes in " + string.Join(", ", s.Dirty) +
                                     ". Nothing was changed. Choose to stash them (they are kept and can be restored), " +
                                     "or commit or undo them first.");
        var stashes = new List<string>();
        if (s.Dirty.Count > 0)
        {
            var name = $"blinx2setup-update-{DateTime.Now:yyyyMMdd-HHmmss}";
            foreach (var d in s.Dirty)
            {
                var dir = d == "the project root" ? _repo : Path.Combine(_repo, d);
                await _run.RunCheckedAsync(_git, new[] { "-C", dir, "stash", "push", "-m", name }, ct, what: $"git stash in {d}");
                stashes.Add($"{d}: stash \"{name}\" (restore with: git -C \"{dir}\" stash list, then git stash apply <its entry>)");
                _log.Info($"Saved local changes in {d} as git stash \"{name}\".");
            }
        }
        if (!s.UpdateAvailable)
        {
            _log.Info("Already up to date.");
            return stashes;
        }
        _log.Info($"Updating {s.Short(s.Installed)} -> {s.Short(s.Latest!)} ({s.Behind} commits)");
        await _run.RunCheckedAsync(_git, new[] { "-C", _repo, "merge", "--ff-only", $"{Remote}/{Branch}" }, ct, what: "git merge --ff-only");
        await _run.RunCheckedAsync(_git, new[] { "-C", _repo, "submodule", "sync", "--recursive" }, ct, what: "git submodule sync");
        await _run.RunCheckedAsync(_git, new[] { "-C", _repo, "submodule", "update", "--init", "--recursive" }, ct,
            what: "git submodule update");
        return stashes;
    }

    /// <summary>Is this folder a clone of the project (the superproject)?</summary>
    public static bool IsProjectClone(string dir)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(dir, ".git")) && !File.Exists(Path.Combine(dir, ".git"))) return false;
            var gm = Path.Combine(dir, ".gitmodules");
            if (!File.Exists(gm)) return false;
            var t = File.ReadAllText(gm);
            return t.Contains("blinx2-recomp", StringComparison.OrdinalIgnoreCase) &&
                   t.Contains("xboxrecomp", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }
}

/// <summary>Is there a newer Blinx2Setup release than this one?</summary>
public static class ReleaseCheck
{
    public const string Api = "https://api.github.com/repos/bruce-smith/Blinx2-Launcher/releases/latest";

    public sealed record Release(Version Version, string Tag, string Url);

    /// <summary>The latest release if it is newer than this program; null
    /// otherwise or if GitHub cannot be reached.</summary>
    public static async Task<Release?> NewerAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Blinx2Setup/" + Program.Version);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (!Version.TryParse(Program.Version, out var mine)) return null;
            return latest > mine ? new Release(latest, tag, url) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }
}
