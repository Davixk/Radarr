using System;
using System.Diagnostics;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    // Directly exercises ImportProbePool's nesting guard. The pool is used at TWO levels: an OUTER fan-out
    // across downloads (DownloadProcessingService) and, inside each download's body, an INNER fan-out across
    // that download's files (ImportDecisionMaker). Without the guard the inner Run would open its OWN degree
    // budget, so total concurrent probes could reach outer_degree * inner_degree (e.g. 4 * 4 = 16) and
    // over-subscribe a debrid/network mount. The guard collapses any Run reached from a pool worker thread
    // to degree 1, so total concurrent probes stay bounded by the OUTER degree.
    [TestFixture]
    public class ImportProbePoolFixture : TestBase
    {
        private const int OuterDegree = 4;
        private const int OuterCount = 4;
        private const int InnerCount = 4;

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", $"{OuterDegree}");
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", null);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", null);
        }

        [Test]
        public void should_bound_nested_pool_concurrency_to_outer_degree()
        {
            var tracker = new PeakConcurrencyTracker(TimeSpan.FromMilliseconds(200));

            // OUTER pool across OuterCount downloads. Each outer body runs an INNER pool across InnerCount
            // files; the guard must keep the inner pool serial so total concurrent leaves never exceed the
            // outer degree.
            ImportProbePool.Run(OuterCount, outer =>
            {
                ImportProbePool.Run(InnerCount, inner => tracker.RecordLeaf());
            });

            tracker.Peak.Should().Be(OuterDegree, "the nesting guard runs the inner pool serially so total concurrent probes stay bounded by the OUTER degree; without it this would reach OuterDegree * InnerCount (up to 16)");
            tracker.TotalExecuted.Should().Be(OuterCount * InnerCount, "every nested leaf body must still execute");
        }

        [Test]
        public void should_bound_nested_pool_concurrency_to_outer_degree_on_timeout_path()
        {
            // A generous timeout keeps the abandon-on-timeout code path (RunInParallelWithTimeout) active
            // without any leaf actually exceeding it, so the guard is proven on the timeout path too.
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", "30");

            var tracker = new PeakConcurrencyTracker(TimeSpan.FromMilliseconds(200));

            ImportProbePool.Run(OuterCount, outer =>
            {
                ImportProbePool.Run(InnerCount, inner => tracker.RecordLeaf());
            });

            tracker.Peak.Should().Be(OuterDegree, "the guard holds on the timeout path too: the inner pool runs serially so total concurrent probes stay bounded by the OUTER degree, not OuterDegree * InnerCount");
            tracker.TotalExecuted.Should().Be(OuterCount * InnerCount, "every nested leaf body must still execute on the timeout path");
        }

        [Test]
        public void should_kill_the_probe_child_process_when_it_times_out()
        {
            // fork7: a probe abandoned on timeout must SIGKILL its ffprobe child (not leak it). Stand in a real
            // long-lived child process for the wedged ffprobe: the body registers it with ProbeProcessRegistry
            // exactly as VideoFileInfoReader.RunFfprobe does, then blocks on it like a wedged read. When the
            // pool times out it must kill the process, which unblocks the read and bounds the OS process count.
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "1");
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", "1");

            Process probe = null;

            try
            {
                var timedOut = ImportProbePool.Run(1, i =>
                {
                    probe = StartSleeper();
                    ProbeProcessRegistry.Attach(probe);

                    try
                    {
                        // Block like a wedged ffprobe read; the kill closes the process and unblocks this.
                        probe.WaitForExit();
                    }
                    finally
                    {
                        ProbeProcessRegistry.Detach(probe);
                    }
                });

                timedOut[0].Should().BeTrue("the blocked probe exceeded IMPORT_PROBE_TIMEOUT and must be abandoned");
                probe.Should().NotBeNull();
                probe.WaitForExit(5000);
                probe.HasExited.Should().BeTrue("the pool must SIGKILL the wedged probe's child process on timeout instead of leaking it");
            }
            finally
            {
                if (probe is { HasExited: false })
                {
                    try
                    {
                        probe.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }

                probe?.Dispose();
            }
        }

        // Starts a real, long-lived child process to stand in for a wedged ffprobe. Cross-platform: a ~30s
        // no-op that runs headless so the test host can SIGKILL it via the pool.
        private static Process StartSleeper()
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
                : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            return Process.Start(startInfo);
        }

        // Records the peak number of leaf bodies running at the same time via a shared atomic counter. Each
        // leaf holds briefly so genuinely concurrent leaves overlap and the observed peak reflects real
        // concurrency rather than scheduling luck.
        private sealed class PeakConcurrencyTracker
        {
            private readonly TimeSpan _hold;
            private int _current;
            private int _peak;
            private int _totalExecuted;

            public PeakConcurrencyTracker(TimeSpan hold)
            {
                _hold = hold;
            }

            public int Peak => Volatile.Read(ref _peak);

            public int TotalExecuted => Volatile.Read(ref _totalExecuted);

            public void RecordLeaf()
            {
                Interlocked.Increment(ref _totalExecuted);

                var concurrent = Interlocked.Increment(ref _current);

                int observedPeak;
                while (concurrent > (observedPeak = Volatile.Read(ref _peak)))
                {
                    Interlocked.CompareExchange(ref _peak, concurrent, observedPeak);
                }

                Thread.Sleep(_hold);

                Interlocked.Decrement(ref _current);
            }
        }
    }
}
