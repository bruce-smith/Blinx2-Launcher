using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Blinx2Launcher;

/// <summary>
/// Runs the setup's child processes (git, python, cmake, MSBuild, xdvdfs)
/// and streams their output into the log. Every child is put in one job
/// object that is killed when the setup exits or is cancelled, so a
/// cancelled build leaves nothing running, and nothing else on the PC is
/// touched: only processes this setup started are ever stopped.
/// </summary>
public sealed class ProcRunner
{
    private readonly Log _log;
    private readonly IntPtr _job;

    public ProcRunner(Log log)
    {
        _log = log;
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        if (_job != IntPtr.Zero)
        {
            var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int len = Marshal.SizeOf(info);
            IntPtr p = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, p, false);
                Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, p, (uint)len);
            }
            finally { Marshal.FreeHGlobal(p); }
        }
    }

    /// <summary>Stops every process this setup started (and their children).</summary>
    public void KillAll()
    {
        if (_job != IntPtr.Zero) Native.TerminateJobObject(_job, 1);
    }

    public sealed class Options
    {
        public string? WorkingDir;
        public Dictionary<string, string>? Env;
        /// <summary>Called with each output line (stdout and stderr).</summary>
        public Action<string>? OnLine;
        /// <summary>Do not put the process in the setup's job (installers that
        /// must finish even if the setup is closed).</summary>
        public bool Detached;
    }

    /// <summary>Runs a process to completion; returns its exit code.</summary>
    public async Task<int> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, Options? o = null)
    {
        o ??= new Options();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = o.WorkingDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Python and MSBuild in English UTF-8, git never prompts.
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["VSLANG"] = "1033";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["PATH"] = Util.FreshPath();
        if (o.Env != null)
            foreach (var kv in o.Env) psi.Environment[kv.Key] = kv.Value;

        _log.Info($"> {Util.QuoteCommand(exe, psi.ArgumentList)}" + (o.WorkingDir != null ? $"   (in {o.WorkingDir})" : ""));

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { proc.Start(); }
        catch (Win32Exception e) { throw new SetupException($"Could not start {exe}: {e.Message}"); }
        if (!o.Detached && _job != IntPtr.Zero && !Native.AssignProcessToJobObject(_job, proc.Handle))
            _log.Info($"  (could not add the process to the setup's job: error {Marshal.GetLastWin32Error()})");
        proc.StandardInput.Close();

        void Pump(StreamReader r)
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                _log.Write("  " + line);
                o.OnLine?.Invoke(line);
            }
        }
        var tOut = Task.Run(() => Pump(proc.StandardOutput));
        var tErr = Task.Run(() => Pump(proc.StandardError));

        using (ct.Register(() =>
        {
            // Our own child only, by its handle (never by name).
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }))
        {
            await proc.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(tOut, tErr);
        }
        ct.ThrowIfCancellationRequested();
        _log.Info($"  (exit code {proc.ExitCode})");
        return proc.ExitCode;
    }

    /// <summary>Runs a process and throws a SetupException if it fails.</summary>
    public async Task RunCheckedAsync(string exe, IEnumerable<string> args, CancellationToken ct, Options? o = null, string? what = null)
    {
        int rc = await RunAsync(exe, args, ct, o);
        if (rc != 0)
            throw new SetupException($"{what ?? Path.GetFileName(exe)} failed (exit code {rc}). The log above says why.");
    }

    /// <summary>Runs a short command and returns its output (no logging); null if it could not run.</summary>
    public static string? Capture(string exe, IEnumerable<string> args, int timeoutMs = 20000, string? workDir = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (workDir != null) psi.WorkingDirectory = workDir;
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = Util.FreshPath();
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            return so.Result;
        }
        catch { return null; }
    }
}

public sealed class SetupException : Exception
{
    public SetupException(string msg) : base(msg) { }
}

internal static class Native
{
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int pid);
}
