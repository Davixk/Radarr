using System;
using System.Diagnostics;

namespace NzbDrone.Core.MediaFiles
{
    // fork7: bridges ImportProbePool's abandon-on-timeout to the ffprobe child process so a timed-out probe's
    // ffprobe is SIGKILLed instead of leaked (the fork2/fork3 timeout only released the pool permit and left
    // the ffprobe alive in D-state, so the OS ffprobe count grew without bound). A ProbeKillSlot is created per
    // timeout-worker; the worker publishes it on its own thread via CurrentSlot; VideoFileInfoReader.RunFfprobe
    // Attaches/Detaches the ffprobe Process it spawns to whatever slot is current; ImportProbePool's timeout
    // Timer thread calls slot.Kill(). Outside the pool CurrentSlot is null, so Attach/Detach are no-ops and
    // non-pool media-info reads (manual import, refresh, the startup pixel-format probe) are unaffected.
    public static class ProbeProcessRegistry
    {
        // Set by an ImportProbePool timeout-worker at the top of its thread; null on every other thread.
        [ThreadStatic]
        internal static ProbeKillSlot CurrentSlot;

        public static void Attach(Process process) => CurrentSlot?.Set(process);

        public static void Detach(Process process) => CurrentSlot?.Clear(process);
    }

    // Holds the ffprobe Process currently running under one probe item and kills it on timeout. Kill() may be
    // called before Set() (the timer beats the spawn); the pending-kill flag makes the next Set() kill at once.
    // All state is lock-guarded because Attach/Detach run on the worker thread while Kill runs on the Timer
    // thread.
    public sealed class ProbeKillSlot
    {
        private readonly object _lock = new object();
        private Process _current;
        private bool _killRequested;

        public void Set(Process process)
        {
            lock (_lock)
            {
                _current = process;

                if (_killRequested)
                {
                    TryKill(process);
                }
            }
        }

        public void Clear(Process process)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_current, process))
                {
                    _current = null;
                }
            }
        }

        public void Kill()
        {
            lock (_lock)
            {
                _killRequested = true;

                if (_current != null)
                {
                    TryKill(_current);
                }
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Racing a normal exit, or already gone: nothing to kill.
            }
        }
    }
}
