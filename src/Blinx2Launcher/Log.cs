using System.Text;

namespace Blinx2Launcher;

/// <summary>
/// The setup log: every line goes to a file in the install folder (the full
/// record, compiler warnings included) and to whoever listens (the log pane,
/// or the console in --cli mode).
/// </summary>
public sealed class Log : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _file;
    private readonly List<string> _tail = new();
    private const int TailMax = 4000;

    public string? FilePath { get; private set; }

    /// <summary>A line was logged. Raised on the thread that logged it.</summary>
    public event Action<string>? Line;

    public void OpenFile(string path)
    {
        lock (_lock)
        {
            _file?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            _file.WriteLine();
            _file.WriteLine($"===== Blinx 2 setup {Program.Version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            _file?.WriteLine(line);
            _tail.Add(line);
            if (_tail.Count > TailMax) _tail.RemoveRange(0, _tail.Count - TailMax);
        }
        Line?.Invoke(line);
    }

    public void Info(string msg) => Write($"[{DateTime.Now:HH:mm:ss}] {msg}");
    public void Error(string msg) => Write($"[{DateTime.Now:HH:mm:ss}] ERROR: {msg}");

    /// <summary>The last lines, without compiler warnings: what a bug report needs.</summary>
    public string TailForReport(int lines)
    {
        lock (_lock)
        {
            var keep = _tail.Where(l => !Util.IsCompilerWarning(l)).ToList();
            return string.Join(Environment.NewLine, keep.Skip(Math.Max(0, keep.Count - lines)));
        }
    }

    public void Dispose()
    {
        lock (_lock) { _file?.Dispose(); _file = null; }
    }
}
