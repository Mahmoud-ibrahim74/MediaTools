using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaTools.Application.Abstractions;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Freezes the FFmpeg capture process while "paused" so no frames/audio are written (Windows-only).
/// </summary>
internal sealed class ProcessPauseControl(Process process) : IPausableRecordingControl
{
    private readonly Process _process = process;
    private bool _paused;

    public bool IsPaused => _paused;

    public void Pause()
    {
        if (_paused || _process.HasExited)
        {
            return;
        }

        try
        {
            if (NtSuspendProcess(_process.Handle) == 0)
            {
                _paused = true;
            }
        }
        catch
        {
            // Access denied or invalid handle — leave recording running.
        }
    }

    public void Resume()
    {
        if (!_paused || _process.HasExited)
        {
            return;
        }

        try
        {
            if (NtResumeProcess(_process.Handle) == 0)
            {
                _paused = false;
            }
        }
        catch
        {
            _paused = false;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
