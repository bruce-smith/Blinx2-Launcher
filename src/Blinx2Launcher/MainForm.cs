using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Blinx2Launcher;

/// <summary>
/// The setup window: three pages. 1) the disc image and the install folder,
/// 2) the dependencies (what is missing, install with winget after asking),
/// 3) the steps with a progress bar and the log.
/// </summary>
public sealed class MainForm : Form
{
    private readonly Log _log = new();
    private readonly ProcRunner _runner;
    private Toolchain? _tc;
    private Setup? _setup;
    private CancellationTokenSource? _cts;
    private bool _running;

    // Page 1
    private readonly Panel _pageChoose = new() { Dock = DockStyle.Fill };
    private readonly TextBox _iso = new() { Width = 520 };
    private readonly TextBox _dir = new() { Width = 520 };
    private readonly Label _chooseInfo = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly CheckBox _update = new() { Text = "Update to the latest version of the project first", AutoSize = true, Visible = false };
    private readonly Button _play1 = new() { Text = "Play", AutoSize = true, Visible = false };

    // Page 2
    private readonly Panel _pageDeps = new() { Dock = DockStyle.Fill };
    private readonly ListView _depList = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _depInfo = new() { AutoSize = true, MaximumSize = new Size(760, 0), Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
    private readonly Button _installDeps = new() { Text = "Install missing", AutoSize = true };
    private readonly Button _recheck = new() { Text = "Check again", AutoSize = true };
    private readonly Button _download = new() { Text = "Open download page", AutoSize = true };
    private readonly Button _depsNext = new() { Text = "Continue", AutoSize = true };

    // Page 3
    private readonly Panel _pageRun = new() { Dock = DockStyle.Fill };
    private readonly ListView _stepList = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, Height = 250, Dock = DockStyle.Top };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 2), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
    private readonly Label _eta = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 2, 0, 6) };
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BackColor = SystemColors.Window };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly Button _retry = new() { Text = "Retry", AutoSize = true, Visible = false };
    private readonly Button _copyLog = new() { Text = "Copy log", AutoSize = true };
    private readonly Button _openLog = new() { Text = "Open log file", AutoSize = true };
    private readonly Button _play = new() { Text = "Play Blinx 2", AutoSize = true, Visible = false };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };

    private readonly ConcurrentQueue<string> _pending = new();
    private int _warningsHidden;
    private Step? _current;
    private DateTime _currentStart, _runStart;
    private double _currentFrac;
    private string? _currentNote;
    private readonly Dictionary<string, ListViewItem> _stepItems = new();
    private readonly List<string> _warnings = new();

    public MainForm()
    {
        Text = $"Blinx 2 setup {Program.Version}";
        Size = new Size(860, 680);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(14);
        Font = SystemFonts.MessageBoxFont ?? Font;
        _runner = new ProcRunner(_log);
        _log.Line += l => _pending.Enqueue(l);
        _tick.Tick += (_, _) => Flush();
        _tick.Start();

        BuildChoosePage();
        BuildDepsPage();
        BuildRunPage();
        Controls.Add(_pageChoose);
        Controls.Add(_pageDeps);
        Controls.Add(_pageRun);
        ShowPage(_pageChoose);

        var saved = LauncherSettings.Load();
        _dir.Text = saved.InstallDir ?? Util.DefaultInstallDir();
        _iso.Text = saved.IsoPath ?? "";
        RefreshChooseInfo();
        FormClosing += OnClosing;
    }

    private void ShowPage(Panel p)
    {
        foreach (var x in new[] { _pageChoose, _pageDeps, _pageRun }) x.Visible = x == p;
    }

    // ---- Page 1: disc image and folder ----------------------------------------

    private void BuildChoosePage()
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(new Label
        {
            Text = "Blinx 2: Masters of Time and Space, recompiled for Windows",
            AutoSize = true, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Padding = new Padding(0, 0, 0, 8),
        });
        flow.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(780, 0), Padding = new Padding(0, 0, 0, 12),
            Text = "This setup builds the game from your own disc image. It extracts the game files from the image, " +
                   "installs the tools the build needs (it asks first), downloads the project's source code and " +
                   "compiles it on your PC. Nothing of the game is downloaded: you need your own copy of the " +
                   "North American (NTSC-U) Blinx 2 disc as an .iso or .xiso image.\r\n\r\n" +
                   "It takes 45-60 minutes on a recent PC (longer on older ones) and needs an internet connection. " +
                   "You can leave it alone while it works; if it is interrupted, run it again and it continues.",
        });

        flow.Controls.Add(new Label { Text = "Your Blinx 2 disc image (.iso / .xiso, or the folder it is in):", AutoSize = true });
        var isoRow = Row();
        isoRow.Controls.Add(_iso);
        var bIso = new Button { Text = "Choose file...", AutoSize = true };
        bIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog
            {
                Title = "Your Blinx 2 disc image",
                Filter = "Xbox disc images (*.iso;*.xiso)|*.iso;*.xiso|All files (*.*)|*.*",
            };
            if (File.Exists(_iso.Text)) d.InitialDirectory = Path.GetDirectoryName(_iso.Text);
            if (d.ShowDialog(this) == DialogResult.OK) { _iso.Text = d.FileName; RefreshChooseInfo(); }
        };
        var bIsoDir = new Button { Text = "Choose folder...", AutoSize = true };
        bIsoDir.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "The folder with your Blinx 2 disc image", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) { _iso.Text = d.SelectedPath; RefreshChooseInfo(); }
        };
        isoRow.Controls.Add(bIso);
        isoRow.Controls.Add(bIsoDir);
        flow.Controls.Add(isoRow);

        flow.Controls.Add(new Label { Text = "Install folder (the game, its source and the build go here):", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
        var dirRow = Row();
        dirRow.Controls.Add(_dir);
        var bDir = new Button { Text = "Choose...", AutoSize = true };
        bDir.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Install folder", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                // A fresh folder gets its own Blinx2 subfolder.
                var p = d.SelectedPath;
                if (!File.Exists(SetupState.FileIn(p)) && Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any())
                    p = Path.Combine(p, "Blinx2");
                _dir.Text = p;
                RefreshChooseInfo();
            }
        };
        dirRow.Controls.Add(bDir);
        flow.Controls.Add(dirRow);
        _iso.Leave += (_, _) => RefreshChooseInfo();
        _dir.Leave += (_, _) => RefreshChooseInfo();

        flow.Controls.Add(_update);
        _chooseInfo.Padding = new Padding(0, 10, 0, 10);
        flow.Controls.Add(_chooseInfo);

        var buttons = Row();
        var next = new Button { Text = "Next", AutoSize = true };
        next.Click += (_, _) => OnChooseNext();
        _play1.Click += (_, _) => Play();
        buttons.Controls.Add(next);
        buttons.Controls.Add(_play1);
        flow.Controls.Add(buttons);
        AcceptButton = next;
        _pageChoose.Controls.Add(flow);
    }

    private static FlowLayoutPanel Row() => new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };

    private string InstallDir => Path.GetFullPath(_dir.Text.Trim().Trim('"'));

    private void RefreshChooseInfo()
    {
        var sb = new StringBuilder();
        try
        {
            if (Program.CheckInstallDir(_dir.Text.Trim().Trim('"')) is { } bad) { _chooseInfo.Text = bad; return; }
            var dir = InstallDir;
            var state = SetupState.Load(dir);
            bool installed = File.Exists(Path.Combine(dir, @"blinx-2-recompile-full\blinx2-recomp\build\Release\blinx_2_recomp.exe"));
            bool cloned = Directory.Exists(Path.Combine(dir, @"blinx-2-recompile-full\.git"));
            bool extracted = File.Exists(Path.Combine(dir, @"game_files\default.xbe"));
            if (state.IsoPath != null && _iso.Text.Length == 0) _iso.Text = state.IsoPath;
            _update.Visible = cloned;
            _play1.Visible = installed;
            if (installed) sb.AppendLine("Blinx 2 is installed in this folder. Next checks it (and updates it if ticked).");
            else if (state.Done.Count > 0) sb.AppendLine($"A setup in this folder stopped part-way ({state.Done.Count} steps done); Next continues it.");
            if (extracted) sb.AppendLine("The game files are already extracted here; the disc image is not needed again.");
            long free = Cli.FreeSpace(dir);
            long isoSize = File.Exists(_iso.Text) ? new FileInfo(_iso.Text).Length : 3_400_000_000;
            long need = (extracted ? 0 : isoSize) + 6L * 1024 * 1024 * 1024;
            if (installed) need = 0;
            sb.AppendLine($"Free space on {Path.GetPathRoot(dir)}: {Util.FormatSize(free)}" +
                          (need > 0 ? $" (a full setup needs about {Util.FormatSize(need)})" : ""));
            if (need > free) sb.AppendLine("Not enough free space: choose another drive or free some space.");
        }
        catch (Exception e) { sb.AppendLine(e.Message); }
        _chooseInfo.Text = sb.ToString().TrimEnd();
    }

    private void OnChooseNext()
    {
        RefreshChooseInfo();
        var dirText = _dir.Text.Trim().Trim('"');
        if (Program.CheckInstallDir(dirText) is { } bad) { Error(bad); return; }
        var dir = InstallDir;
        bool extracted = File.Exists(Path.Combine(dir, @"game_files\default.xbe"));
        string iso = "";
        if (!extracted || _iso.Text.Trim().Length > 0)
        {
            var r = Setup.ResolveIso(_iso.Text.Trim().Trim('"'), out var problem);
            if (r == null && !extracted) { Error(problem ?? "Choose your Blinx 2 disc image."); return; }
            iso = r ?? "";
            if (r != null) _iso.Text = r;
        }
        if (Directory.Exists(dir) && !File.Exists(SetupState.FileIn(dir)) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            if (MessageBox.Show(this, $"{dir} is not empty. Install into it anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        LauncherSettings.Save(new LauncherSettings.Data { InstallDir = dir, IsoPath = iso.Length > 0 ? iso : null });
        _log.OpenFile(Path.Combine(dir, "setup.log"));
        _log.Info($"Install folder {dir}, disc image {(iso.Length > 0 ? iso : "(already extracted)")}");
        _isoResolved = iso;
        ShowPage(_pageDeps);
        CheckDeps();
    }

    private string _isoResolved = "";

    // ---- Page 2: dependencies ------------------------------------------------

    private void BuildDepsPage()
    {
        _depList.Columns.Add("", 70);
        _depList.Columns.Add("Needed", 300);
        _depList.Columns.Add("Found", 420);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_depsNext);
        buttons.Controls.Add(_installDeps);
        buttons.Controls.Add(_recheck);
        buttons.Controls.Add(_download);
        var back = new Button { Text = "Back", AutoSize = true };
        back.Click += (_, _) => ShowPage(_pageChoose);
        buttons.Controls.Add(back);
        _recheck.Click += (_, _) => CheckDeps();
        _download.Click += (_, _) =>
        {
            // The selected missing tool's page, or the first missing one's.
            var items = (_depList.SelectedItems.Count > 0 ? _depList.SelectedItems.Cast<ListViewItem>() : _depList.Items.Cast<ListViewItem>());
            if (items.Select(it => it.Tag).OfType<Dependency>().FirstOrDefault(d => !d.Found) is { } dep) OpenUrl(dep.ManualUrl);
        };
        _installDeps.Click += async (_, _) => await InstallDeps();
        _depsNext.Click += (_, _) => StartSetup();
        _pageDeps.Controls.Add(_depList);
        _pageDeps.Controls.Add(_depInfo);
        _pageDeps.Controls.Add(buttons);
        var title = new Label { Text = "Tools the build needs", AutoSize = true, Dock = DockStyle.Top, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold), Padding = new Padding(0, 0, 0, 6) };
        _pageDeps.Controls.Add(title);
    }

    private async void CheckDeps()
    {
        _depInfo.Text = "Checking what is installed...";
        _depList.Items.Clear();
        SetDepButtons(false);
        _log.Info("Checking dependencies:");
        _tc = await Task.Run(() => Toolchain.Detect(_log));
        foreach (var d in _tc.Dependencies)
        {
            var it = new ListViewItem(new[] { d.Found ? "OK" : "missing", $"{d.Name} ({d.Why})", d.Detail }) { Tag = d };
            if (!d.Found) it.ForeColor = Color.Firebrick;
            _depList.Items.Add(it);
        }
        var missing = _tc.Dependencies.Where(d => !d.Found).ToList();
        if (missing.Count == 0)
        {
            _depInfo.Text = "Everything the build needs is installed. Press Start to set up the game.";
        }
        else if (_tc.Winget)
        {
            _depInfo.Text = $"{missing.Count} missing. \"Install missing\" installs them with Windows' package manager (winget): " +
                            string.Join(", ", missing.Select(d => $"{d.Name}, {d.InstallTime}")) + ". " +
                            "Windows may ask for permission (UAC) for each. The Visual Studio installer shows its own progress window.";
        }
        else
        {
            _depInfo.Text = $"{missing.Count} missing, and winget (Windows' package manager, part of \"App Installer\" from the Microsoft Store) " +
                            "is not available. Install them from their download pages (select one and press \"Open download page\"), " +
                            "then press \"Check again\".";
        }
        SetDepButtons(true);
        _installDeps.Text = missing.Count > 0 ? $"Install missing ({missing.Count})" : "Install missing";
        _installDeps.Enabled = missing.Count > 0 && _tc.Winget;
        _download.Enabled = missing.Count > 0;
        _depsNext.Enabled = missing.Count == 0;
        _depsNext.Text = "Start";
        if (missing.Count == 0) AcceptButton = _depsNext;
    }

    private void SetDepButtons(bool on)
    {
        foreach (var b in new[] { _installDeps, _recheck, _download, _depsNext }) b.Enabled = on;
    }

    private async Task InstallDeps()
    {
        if (_tc == null) return;
        var missing = _tc.Dependencies.Where(d => !d.Found).ToList();
        var msg = "These will be installed with winget:\r\n\r\n" +
                  string.Join("\r\n", missing.Select(d => $"  - {d.Name} ({d.InstallTime})")) +
                  "\r\n\r\nWindows may ask for permission for each one. Continue?";
        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        SetDepButtons(false);
        _depInfo.Text = "Installing... (the log is in setup.log in the install folder)";
        try
        {
            await DepInstaller.InstallAsync(missing, _runner, _log, CancellationToken.None);
        }
        catch (Exception e) when (e is SetupException or IOException)
        {
            Error(e.Message);
        }
        CheckDeps();
        if (_tc != null && !_tc.AllFound)
            Error("Some tools are still missing after the install (see the list). If Visual Studio was just installed, " +
                  "restarting Windows and running this setup again usually fixes it.");
    }

    // ---- Page 3: the setup ------------------------------------------------

    private void BuildRunPage()
    {
        _stepList.Columns.Add("", 70);
        _stepList.Columns.Add("", 460);
        _stepList.Columns.Add("", 240);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_play);
        buttons.Controls.Add(_retry);
        buttons.Controls.Add(_openLog);
        buttons.Controls.Add(_copyLog);
        _cancel.Click += (_, _) =>
        {
            if (!_running) { Close(); return; }
            if (MessageBox.Show(this, "Stop the setup? Running it again later continues from the current step.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _cts?.Cancel();
        };
        _retry.Click += (_, _) => StartSetup();
        _copyLog.Click += (_, _) => CopyLog();
        _openLog.Click += (_, _) =>
        {
            if (_log.FilePath != null && File.Exists(_log.FilePath)) Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
        };
        _play.Click += (_, _) => Play();
        var logLabel = new Label { Text = "Log (compiler warnings are only in the log file):", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 2) };
        _pageRun.Controls.Add(_logBox);
        _pageRun.Controls.Add(logLabel);
        _pageRun.Controls.Add(_eta);
        _pageRun.Controls.Add(_progress);
        _pageRun.Controls.Add(_status);
        _pageRun.Controls.Add(_stepList);
        _pageRun.Controls.Add(buttons);
    }

    private async void StartSetup()
    {
        if (_tc == null || _running) return;
        ShowPage(_pageRun);
        _retry.Visible = false;
        _play.Visible = false;
        _cancel.Text = "Cancel";
        _warnings.Clear();
        _setup = new Setup(InstallDir, _isoResolved, _log, _runner, _tc);
        _setup.StepStarted += (s, run) => BeginInvoke(() => OnStepStarted(s, run));
        _setup.StepFinished += (s, ok) => BeginInvoke(() => OnStepFinished(s, ok));
        _setup.StepProgress += (s, f, note) => BeginInvoke(() => { if (s == _current) { _currentFrac = f; _currentNote = note; } });
        _setup.Warning += w => BeginInvoke(() => _warnings.Add(w));

        _stepList.Items.Clear();
        _stepItems.Clear();
        foreach (var s in _setup.Steps)
        {
            var it = new ListViewItem(new[] { "", s.Title, "" });
            _stepItems[s.Id] = it;
            _stepList.Items.Add(it);
        }

        _running = true;
        _cts = new CancellationTokenSource();
        _runStart = DateTime.Now;
        try
        {
            if (_update.Checked && _update.Visible)
            {
                _status.Text = "Updating the project source...";
                await _setup.UpdateAsync(_cts.Token);
                _update.Checked = false;
            }
            var pending = _setup.Pending();
            long need = _setup.SpaceNeeded(pending), free = Cli.FreeSpace(InstallDir);
            _log.Info($"{pending.Count} of {_setup.Steps.Count} steps to run, about {Util.FormatDuration(_setup.Estimate(pending))}; " +
                      $"needs about {Util.FormatSize(need)} of disk, {Util.FormatSize(free)} free.");
            foreach (var s in _setup.Steps.Except(pending)) _stepItems[s.Id].SubItems[0].Text = "done";
            if (free < need)
                throw new SetupException($"Not enough free disk space on {Path.GetPathRoot(InstallDir)}: about {Util.FormatSize(need)} " +
                                         $"needed, {Util.FormatSize(free)} free.");
            await _setup.RunAsync(_cts.Token);
            Finished();
        }
        catch (OperationCanceledException)
        {
            _runner.KillAll();
            _log.Info("Cancelled. Run the setup again to continue where it stopped.");
            Failed("Cancelled. Press Retry (or run the setup again later) to continue where it stopped.");
        }
        catch (Exception e) when (e is SetupException or IOException or UnauthorizedAccessException or HttpRequestException
                                      or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Error(e.Message);
            Failed(e.Message);
        }
        finally
        {
            _running = false;
            _current = null;
        }
    }

    private void OnStepStarted(Step s, bool run)
    {
        if (!run) { _stepItems[s.Id].SubItems[0].Text = "done"; return; }
        _current = s;
        _currentStart = DateTime.Now;
        _currentFrac = 0;
        _currentNote = null;
        var it = _stepItems[s.Id];
        it.SubItems[0].Text = "running";
        it.Font = new Font(_stepList.Font, FontStyle.Bold);
        it.EnsureVisible();
        _status.Text = s.Title + "...";
    }

    private void OnStepFinished(Step s, bool ok)
    {
        var it = _stepItems[s.Id];
        it.Font = _stepList.Font;
        if (ok)
        {
            if (it.SubItems[0].Text == "running")
                it.SubItems[2].Text = Util.FormatDuration(DateTime.Now - _currentStart);
            it.SubItems[0].Text = "done";
        }
        else
        {
            it.SubItems[0].Text = "FAILED";
            it.ForeColor = Color.Firebrick;
        }
    }

    private void Finished()
    {
        _progress.Value = _progress.Maximum;
        _status.Text = "Blinx 2 is ready.";
        _eta.Text = $"Took {Util.FormatDuration(DateTime.Now - _runStart)}. " +
                    (_setup!.MakeShortcuts ? "Shortcuts are on the desktop and in the Start menu (\"Blinx 2\"). " : "") +
                    "Escape opens the game's menu." + (_warnings.Count > 0 ? "\r\nNote: " + string.Join(" ", _warnings) : "");
        _play.Visible = true;
        _cancel.Text = "Close";
        AcceptButton = _play;
    }

    private void Failed(string msg)
    {
        _status.Text = "The setup stopped.";
        _eta.Text = msg + "\r\n\"Copy log\" copies the details for a bug report.";
        _eta.ForeColor = Color.Firebrick;
        _retry.Visible = true;
        _cancel.Text = "Close";
        MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---- Shared ---------------------------------------------------------------

    private void Flush()
    {
        if (_pending.IsEmpty && _current == null) return;
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var l))
        {
            if (Util.IsCompilerWarning(l)) { _warningsHidden++; continue; }
            sb.Append(l).Append("\r\n");
        }
        if (sb.Length > 0)
        {
            if (_logBox.TextLength > 600_000)
            {
                var t = _logBox.Text;
                _logBox.Text = t[^300_000..];
            }
            _logBox.AppendText(sb.ToString());
        }
        if (_current != null && _setup != null)
        {
            _eta.ForeColor = SystemColors.ControlText;
            var elapsed = DateTime.Now - _currentStart;
            // Remaining: the rest of this step (from its progress, or from its
            // typical duration) plus the typical durations of the steps after it.
            double frac = _currentFrac > 0.02 ? _currentFrac : Math.Min(0.95, elapsed.TotalSeconds / _current.Typical.TotalSeconds);
            var restOfStep = _currentFrac > 0.02 && elapsed.TotalSeconds > 20
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - frac) / frac)
                : _current.Typical - elapsed;
            if (restOfStep < TimeSpan.Zero) restOfStep = TimeSpan.Zero;
            int idx = _setup.Steps.ToList().IndexOf(_current);
            var after = _setup.Steps.Skip(idx + 1).Where(s => _stepItems[s.Id].SubItems[0].Text != "done");
            var left = restOfStep + _setup.Estimate(after);
            _progress.Value = (int)(Math.Clamp(frac, 0, 1) * _progress.Maximum);
            _eta.Text = $"This step: {Util.FormatDuration(elapsed)}" + (_currentNote != null ? $" ({_currentNote})" : "") +
                        $". About {Util.FormatDuration(left + TimeSpan.FromSeconds(30))} left in total. " +
                        "You can leave the PC alone; it will not go to sleep while this runs.";
        }
    }

    private void CopyLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Blinx 2 setup {Program.Version}, Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} CPUs");
        if (_tc != null)
            foreach (var d in _tc.Dependencies) sb.AppendLine($"{(d.Found ? "ok" : "MISSING")} {d.Name}: {d.Detail}");
        if (_setup != null)
            foreach (var s in _setup.Steps) sb.AppendLine($"{_stepItems.GetValueOrDefault(s.Id)?.SubItems[0].Text,-8} {s.Title}");
        sb.AppendLine($"Full log: {_log.FilePath}");
        sb.AppendLine("----");
        sb.AppendLine(_log.TailForReport(400));
        Clipboard.SetText(sb.ToString());
        _copyLog.Text = "Copied";
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) => { _copyLog.Text = "Copy log"; t.Dispose(); };
        t.Start();
    }

    private void Play()
    {
        var exe = _setup?.ExePath ?? Path.Combine(InstallDir, @"blinx-2-recompile-full\blinx2-recomp\build\Release\blinx_2_recomp.exe");
        if (!File.Exists(exe)) { Error("The game is not built yet."); return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_running) return;
        if (MessageBox.Show(this, "The setup is still running. Stop it and close? Running it again later continues from the current step.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
        _runner.KillAll();
    }

    private void Error(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
