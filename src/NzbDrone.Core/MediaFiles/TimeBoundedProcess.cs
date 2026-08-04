using System;
using System.Diagnostics;

namespace NzbDrone.Core.MediaFiles
{
    // fork8: runs a child process with a hard wall-clock deadline, SIGKILLing the whole process tree if it
    // overruns. VideoFileInfoReader.RunFfprobe uses this so EVERY ffprobe spawn is time-bounded at the source,
    // not only the ones dispatched through ImportProbePool. The pool's own kill (via ProbeProcessRegistry) still
    // runs for pooled probes as belt-and-suspenders; this closes the OFF-pool spawn sites that previously had no
    // deadline at all (media-info refresh on Series/MovieScannedEvent, script import, subtitle-extra sample
    // detection). A timeout of zero (or negative) means "wait indefinitely", exactly the original behaviour.
    public static class TimeBoundedProcess
    {
        // Starts startInfo and returns its stdout. If the process outlives 'timeout' its whole tree is killed
        // and whatever stdout was buffered is returned. onStarted/onFinished bracket the live process (used to
        // register/deregister it with ProbeProcessRegistry for the pool path). stderr is drained concurrently so
        // a chatty child cannot deadlock by filling the stderr pipe while we wait on stdout.
        public static string Run(ProcessStartInfo startInfo, TimeSpan timeout, Action<Process> onStarted = null, Action<Process> onFinished = null)
        {
            using var process = new Process { StartInfo = startInfo };

            process.Start();
            onStarted?.Invoke(process);

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (timeout > TimeSpan.Zero)
                {
                    var timeoutMs = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);

                    if (!process.WaitForExit(timeoutMs))
                    {
                        TryKill(process);
                    }
                }

                // Reap: waits for full exit and, after a kill, lets the closed pipes complete the async reads.
                process.WaitForExit();

                var output = stdoutTask.GetAwaiter().GetResult();
                stderrTask.GetAwaiter().GetResult();

                return output;
            }
            finally
            {
                onFinished?.Invoke(process);
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
