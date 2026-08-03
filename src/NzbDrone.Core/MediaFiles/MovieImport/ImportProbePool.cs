using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    // Shared bounded-concurrency scheduler for the probe/decision (ffprobe/media-info) work. It is used
    // in TWO places so both honour the same knobs and abandon-on-timeout semantics:
    //   1. WITHIN one import batch: fan-out across the video files of a single download (ImportDecisionMaker).
    //   2. ACROSS downloads: fan-out across the pending completed downloads of one processing pass
    //      (DownloadProcessingService), so a backlog of single-file downloads no longer probes one file
    //      at a time behind a serial foreach.
    //
    // IMPORT_PROBE_THREADS bounds the logical degree of parallelism (1 reproduces the original serial
    // behaviour exactly). IMPORT_PROBE_TIMEOUT (seconds, 0 = off) abandons a permanently wedged probe
    // (an ffprobe stuck in uninterruptible D-state that never returns and cannot be killed) so it cannot
    // block the whole batch; the wedged worker thread and its zombie ffprobe are left to leak.
    public static class ImportProbePool
    {
        private const int DEFAULT_PROBE_THREADS = 4;
        private const int PROBE_THREADS_LOWER_BOUND = 1;
        private const int PROBE_THREADS_UPPER_BOUND = 16;
        private const int DEFAULT_PROBE_TIMEOUT_SECONDS = 0;
        private const int DEFAULT_PROBE_TIMEOUT_STRIKES = 3;

        // Set on a pool worker thread while it runs body(). Because the pool is now nested (a per-download
        // fan-out inside DownloadProcessingService can itself fan-out across a download's files in
        // ImportDecisionMaker), a nested Run reached from a worker thread would otherwise open its OWN
        // degree budget, letting total concurrent probes reach degree*degree (e.g. 8*8=64) and
        // over-subscribing a debrid/network mount. When this flag is set the nested Run collapses to
        // degree 1 (serial within that download), so the OUTER pool is the only source of concurrency and
        // total concurrent probes stays bounded by the outer degree. ThreadStatic because each worker is a
        // dedicated thread; workers exit after body() so no reset is needed.
        [ThreadStatic]
        private static bool _insidePool;

        // Runs body(i) for each i in [0, count) with bounded logical concurrency of GetDegreeOfParallelism().
        // Returns a per-index flag array marking which items were ABANDONED because their probe exceeded
        // GetTimeout(); the array is all-false when no timeout is configured (probes are waited on
        // indefinitely, the current default). A degree of 1 or a single item runs inline (serial).
        public static bool[] Run(int count, Action<int> body)
        {
            // A Run reached from within a pool worker thread (nested pool) collapses to degree 1 so it runs
            // serially and the OUTER pool remains the only source of concurrency; this bounds total
            // concurrent probes to the outer degree instead of degree*degree. A top-level Run uses the full
            // configured degree.
            var degree = _insidePool ? 1 : GetDegreeOfParallelism();
            var timeout = GetTimeout();

            if (timeout > TimeSpan.Zero)
            {
                return RunInParallelWithTimeout(count, degree, timeout, body);
            }

            RunInParallel(count, degree, body);

            return new bool[Math.Max(count, 0)];
        }

        // Reads IMPORT_PROBE_THREADS. Defaults to DEFAULT_PROBE_THREADS and is clamped to
        // [PROBE_THREADS_LOWER_BOUND, PROBE_THREADS_UPPER_BOUND] so slow hardware is never excluded by a
        // hardcoded value and a typo cannot spawn an unbounded number of ffprobe processes.
        public static int GetDegreeOfParallelism()
        {
            var envValue = Environment.GetEnvironmentVariable("IMPORT_PROBE_THREADS") ?? $"{DEFAULT_PROBE_THREADS}";
            var threads = DEFAULT_PROBE_THREADS;

            if (int.TryParse(envValue, out var parsedThreads))
            {
                threads = parsedThreads;
            }

            threads = Math.Max(PROBE_THREADS_LOWER_BOUND, threads);
            threads = Math.Min(PROBE_THREADS_UPPER_BOUND, threads);

            return threads;
        }

        // Reads IMPORT_PROBE_TIMEOUT (whole seconds). A default of 0 (and any value <= 0) means "off":
        // probes are waited on indefinitely, exactly the original behaviour. Any positive value is the
        // per-probe budget after which a probe is abandoned.
        public static TimeSpan GetTimeout()
        {
            var envValue = Environment.GetEnvironmentVariable("IMPORT_PROBE_TIMEOUT") ?? $"{DEFAULT_PROBE_TIMEOUT_SECONDS}";
            var seconds = DEFAULT_PROBE_TIMEOUT_SECONDS;

            if (int.TryParse(envValue, out var parsedSeconds))
            {
                seconds = parsedSeconds;
            }

            seconds = Math.Max(0, seconds);

            return TimeSpan.FromSeconds(seconds);
        }

        // Reads IMPORT_PROBE_TIMEOUT_STRIKES: the number of CONSECUTIVE probe-timeouts for one completed
        // download after which it is failed (blocklist + re-search) instead of retried into the same file
        // forever (fork7 #4). Default 3; 0 (and any value <= 0) disables the escalation. Escalation is also
        // effectively gated behind IMPORT_PROBE_TIMEOUT > 0, since a probe timeout only occurs on the
        // timeout path.
        public static int GetTimeoutStrikes()
        {
            var envValue = Environment.GetEnvironmentVariable("IMPORT_PROBE_TIMEOUT_STRIKES") ?? $"{DEFAULT_PROBE_TIMEOUT_STRIKES}";
            var strikes = DEFAULT_PROBE_TIMEOUT_STRIKES;

            if (int.TryParse(envValue, out var parsedStrikes))
            {
                strikes = parsedStrikes;
            }

            return Math.Max(0, strikes);
        }

        // Runs body(i) for i in [0, count) across at most 'degree' dedicated worker threads. A degree of
        // 1 (or a single item) runs inline on the calling thread, reproducing the original serial
        // behaviour exactly. Dedicated threads are used (rather than the thread pool) so exactly 'degree'
        // probes run concurrently without waiting on thread-pool injection, bounding concurrent ffprobe
        // processes to 'degree'. The first exception thrown by any worker is rethrown to the caller.
        private static void RunInParallel(int count, int degree, Action<int> body)
        {
            if (count <= 0)
            {
                return;
            }

            if (degree <= 1 || count == 1)
            {
                for (var i = 0; i < count; i++)
                {
                    body(i);
                }

                return;
            }

            var workerCount = Math.Min(degree, count);
            var nextIndex = -1;
            Exception firstError = null;
            var threads = new Thread[workerCount];

            for (var w = 0; w < workerCount; w++)
            {
                var thread = new Thread(() =>
                {
                    // Mark this worker so any Run invoked from within body() (a nested pool) runs serially.
                    _insidePool = true;

                    int index;

                    while ((index = Interlocked.Increment(ref nextIndex)) < count)
                    {
                        try
                        {
                            body(index);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref firstError, ex, null);
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "ImportProbe"
                };

                threads[w] = thread;
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            if (firstError != null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        // Runs body(i) for i in [0, count) with bounded LOGICAL concurrency of 'degree', abandoning any
        // item whose worker exceeds 'timeout'. This is the only path that tolerates a permanently wedged
        // probe: unlike RunInParallel it NEVER joins the worker threads. Each item runs on its own
        // dedicated background thread and holds one semaphore permit. A per-item timer and the worker race
        // to "settle" the item; whichever wins first frees the permit and signals the countdown EXACTLY
        // ONCE (guarded by an Interlocked flag per index so a wedged worker that wakes up later no-ops and
        // never over-releases the semaphore). When an item times out its permit is freed without joining
        // the wedged thread, so the dispatcher's next Wait() starts a replacement worker and logical
        // concurrency stays at 'degree' while the wedged thread and its zombie ffprobe leak. The method
        // returns once every item has settled (via the countdown), never blocking on a wedged thread.
        // Results are written by input index, so ordering stays deterministic; a per-index flag is
        // returned so the caller can record the appropriate timed-out outcome.
        private static bool[] RunInParallelWithTimeout(int count, int degree, TimeSpan timeout, Action<int> body)
        {
            var timedOut = new bool[count];

            if (count <= 0)
            {
                return timedOut;
            }

            var settled = new int[count];
            var timers = new Timer[count];
            var killSlots = new ProbeKillSlot[count];
            var sem = new SemaphoreSlim(degree);
            var countdown = new CountdownEvent(count);
            Exception firstError = null;

            // Frees the permit and signals the countdown for one item EXACTLY ONCE. The Interlocked
            // exchange elects a single winner between the worker finishing and the timer firing; the loser
            // returns without touching the semaphore/countdown, which is what a wedged worker does if it
            // ever wakes after its timeout already settled the item.
            void Settle(int i, bool didTimeout)
            {
                if (Interlocked.Exchange(ref settled[i], 1) != 0)
                {
                    return;
                }

                if (didTimeout)
                {
                    // fork7: SIGKILL the wedged ffprobe (via the slot the worker published to
                    // ProbeProcessRegistry) BEFORE releasing the permit. Killing closes the stdout pipe the
                    // worker is blocked reading, so the worker unwinds and the ffprobe does not leak; the OS
                    // ffprobe count then stays bounded by the permit count for real, not just logically.
                    killSlots[i]?.Kill();
                }

                timedOut[i] = didTimeout;
                timers[i].Dispose();
                sem.Release();
                countdown.Signal();
            }

            try
            {
                for (var i = 0; i < count; i++)
                {
                    // Acquire a logical slot. When every slot is held by a wedged item this blocks only
                    // until one of their timers fires and releases, so dispatch always makes progress.
                    sem.Wait();

                    var index = i;

                    // fork7: publish this item's kill slot BEFORE arming the timer, so a fast timeout always
                    // finds a non-null slot to kill. The worker sets it as its thread's current slot so the
                    // ffprobe runner can Attach the process it spawns.
                    var killSlot = new ProbeKillSlot();
                    killSlots[index] = killSlot;

                    // Create the timer stopped, publish it, then arm it, so its callback can never run
                    // (and dispose it) before timers[index] is assigned.
                    var timer = new Timer(_ => Settle(index, true));
                    timers[index] = timer;
                    timer.Change(timeout, Timeout.InfiniteTimeSpan);

                    var thread = new Thread(() =>
                    {
                        // Mark this worker so any Run invoked from within body() (a nested pool) runs serially.
                        _insidePool = true;

                        // fork7: make this worker's ffprobe killable on timeout (see ProbeProcessRegistry).
                        ProbeProcessRegistry.CurrentSlot = killSlot;

                        try
                        {
                            body(index);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref firstError, ex, null);
                        }
                        finally
                        {
                            Settle(index, false);
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "ImportProbeTimeout"
                    };

                    thread.Start();
                }

                // Returns once every item has settled (completed or timed out). A timed-out item is
                // settled by its timer, so this never blocks on the abandoned worker thread, which is
                // deliberately never joined and is left to leak with its wedged ffprobe.
                countdown.Wait();
            }
            finally
            {
                // Safe to dispose here: every item has settled, so no further Release/Signal will run. A
                // wedged worker that wakes later hits the Interlocked guard in Settle and returns before
                // it would touch either primitive.
                sem.Dispose();
                countdown.Dispose();
            }

            if (firstError != null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }

            return timedOut;
        }
    }
}
