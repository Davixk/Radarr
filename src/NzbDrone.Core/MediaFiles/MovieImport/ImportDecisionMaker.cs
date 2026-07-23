using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IMakeImportDecision
    {
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, bool filterExistingFiles);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource, bool filterExistingFiles);
        ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem);
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        // Bounded, configurable degree of parallelism for the probe/decision phase. The probe/media-info
        // work (ffprobe) is IO bound, so a slow/hung probe on one file must not block the others. A degree
        // of 1 reproduces the original serial behaviour exactly. Configurable via IMPORT_PROBE_THREADS so
        // slow hardware is never excluded by a hardcoded value.
        private const int DEFAULT_PROBE_THREADS = 4;
        private const int PROBE_THREADS_LOWER_BOUND = 1;
        private const int PROBE_THREADS_UPPER_BOUND = 16;

        // Optional abandon-on-timeout for a permanently wedged probe. An ffprobe stuck in uninterruptible
        // D-state never returns and cannot be killed, so waiting on it hangs the whole batch (Phase 3 never
        // runs, nothing imports). When IMPORT_PROBE_TIMEOUT (seconds) is > 0 a probe exceeding it is
        // ABANDONED: its logical slot is freed, the item is marked, and the batch moves on while the wedged
        // OS thread and its zombie ffprobe leak until the mount read finally errors. A default of 0 keeps
        // the current behaviour exactly (wait indefinitely), so it is not a breaking change.
        private const int DEFAULT_PROBE_TIMEOUT_SECONDS = 0;

        private readonly IEnumerable<IImportDecisionEngineSpecification> _specifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAggregationService _aggregationService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDetectSample _detectSample;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly Logger _logger;

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification> specifications,
                                   IMediaFileService mediaFileService,
                                   IAggregationService aggregationService,
                                   IDiskProvider diskProvider,
                                   IDetectSample detectSample,
                                   ITrackedDownloadService trackedDownloadService,
                                   ICustomFormatCalculationService formatCalculator,
                                   Logger logger)
        {
            _specifications = specifications;
            _mediaFileService = mediaFileService;
            _aggregationService = aggregationService;
            _diskProvider = diskProvider;
            _detectSample = detectSample;
            _trackedDownloadService = trackedDownloadService;
            _formatCalculator = formatCalculator;
            _logger = logger;
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie)
        {
            return GetImportDecisions(videoFiles, movie, null, null, false);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, bool filterExistingFiles)
        {
            return GetImportDecisions(videoFiles, movie, null, null, false, filterExistingFiles);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource)
        {
            return GetImportDecisions(videoFiles, movie, downloadClientItem, folderInfo, sceneSource, true);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource, bool filterExistingFiles)
        {
            var newFiles = filterExistingFiles ? _mediaFileService.FilterExistingFiles(videoFiles.ToList(), movie) : videoFiles.ToList();

            _logger.Debug("Analyzing {0}/{1} files.", newFiles.Count, videoFiles.Count);

            ParsedMovieInfo downloadClientItemInfo = null;

            if (downloadClientItem != null)
            {
                downloadClientItemInfo = Parser.Parser.ParseMovieTitle(downloadClientItem.Title);
            }

            var degreeOfParallelism = GetProbeDegreeOfParallelism();
            var probeTimeout = GetProbeTimeout();

            // Force the lazy-loaded metadata once up front so the parallel passes below only read it.
            MovieMetadata movieMetadata = movie.MovieMetadata;

            // Phase 1 (bounded parallel): sample detection. This folds the previously serial
            // GetNonSampleVideoFileCount pre-pass into the parallel probe region so a slow probe here
            // does not serialize the whole batch. The result is reused by the sample specification so
            // the file is not probed for its runtime a second time.
            var sampleResults = new DetectSampleResult[newFiles.Count];

            Action<int> detectSampleBody = i =>
            {
                sampleResults[i] = _detectSample.IsSample(movieMetadata, newFiles[i]);
            };

            if (probeTimeout > TimeSpan.Zero)
            {
                var sampleTimedOut = RunInParallelWithTimeout(newFiles.Count, degreeOfParallelism, probeTimeout, detectSampleBody);

                for (var i = 0; i < newFiles.Count; i++)
                {
                    if (sampleTimedOut[i])
                    {
                        // An abandoned sample probe defaults to NotSample so it does not block the batch
                        // here; the same wedged file times out again in Phase 2 and is rejected there.
                        sampleResults[i] = DetectSampleResult.NotSample;
                    }
                }
            }
            else
            {
                RunInParallel(newFiles.Count, degreeOfParallelism, detectSampleBody);
            }

            var nonSampleVideoFileCount = sampleResults.Count(r => r != DetectSampleResult.Sample);
            var otherVideoFiles = nonSampleVideoFileCount > 1;

            // Phase 2 (bounded parallel): the probe/aggregate heavy per-file work (parse, media info,
            // custom formats). Results are collected by input index so ordering stays deterministic
            // regardless of the order the probes complete in.
            var prepared = new PreparedDecision[newFiles.Count];

            Func<int, LocalMovie> buildLocalMovie = i =>
            {
                var file = newFiles[i];

                return new LocalMovie
                {
                    Movie = movie,
                    DownloadClientMovieInfo = downloadClientItemInfo,
                    DownloadItem = downloadClientItem,
                    FolderMovieInfo = folderInfo,
                    Path = file,
                    SceneSource = sceneSource,
                    ExistingFile = movie.Path.IsParentPath(file),
                    OtherVideoFiles = otherVideoFiles,
                    SampleResult = sampleResults[i]
                };
            };

            Action<int> prepareBody = i =>
            {
                prepared[i] = Prepare(buildLocalMovie(i), downloadClientItem);
            };

            if (probeTimeout > TimeSpan.Zero)
            {
                var prepareTimedOut = RunInParallelWithTimeout(newFiles.Count, degreeOfParallelism, probeTimeout, prepareBody);

                for (var i = 0; i < newFiles.Count; i++)
                {
                    if (prepareTimedOut[i])
                    {
                        // The probe for this file was abandoned. Reject it this pass (reusing the generic
                        // Error reason) so the batch completes and the healthy files still import; the file
                        // is logged in serial Phase 3 and stays pending for a future pass rather than
                        // hanging the whole import.
                        var localMovie = buildLocalMovie(i);

                        prepared[i] = new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.Error, "Probe timed out")), null);
                    }
                }
            }
            else
            {
                RunInParallel(newFiles.Count, degreeOfParallelism, prepareBody);
            }

            // Phase 3 (serial, input order): evaluate specifications, assemble decisions and log.
            // Specification evaluation, history/DB lookups and logging all stay single threaded and
            // ordered to keep behaviour, logs and tests deterministic.
            var decisions = new List<ImportDecision>(prepared.Length);

            foreach (var item in prepared)
            {
                if (item.Error != null)
                {
                    _logger.Error(item.Error, "Couldn't import file. {0}", item.LocalMovie.Path);
                }

                var decision = item.Decision ?? GetDecision(item.LocalMovie, downloadClientItem);

                LogDecision(decision, item.LocalMovie);

                decisions.AddIfNotNull(decision);
            }

            return decisions;
        }

        public ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var reasons = _specifications.Select(c => EvaluateSpec(c, localMovie, downloadClientItem))
                                         .Where(c => c != null);

            return new ImportDecision(localMovie, reasons.ToArray());
        }

        private PreparedDecision Prepare(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            // Runs inside the bounded parallel region: only the probe/aggregate heavy IO happens here.
            // Any early rejection is captured and returned so the serial phase can log and assemble it
            // in deterministic input order. Exceptions are captured (not logged here) so all logging
            // stays serial.
            try
            {
                var fileMovieInfo = Parser.Parser.ParseMoviePath(localMovie.Path);

                localMovie.FileMovieInfo = fileMovieInfo;
                localMovie.Size = _diskProvider.GetFileSize(localMovie.Path);

                _aggregationService.Augment(localMovie, downloadClientItem);

                if (localMovie.Movie == null)
                {
                    return new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.InvalidMovie, "Invalid movie")), null);
                }

                if (downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
                {
                    var trackedDownload = _trackedDownloadService.Find(downloadClientItem.DownloadId);

                    if (trackedDownload?.RemoteMovie?.Release?.IndexerFlags != null)
                    {
                        localMovie.IndexerFlags = trackedDownload.RemoteMovie.Release.IndexerFlags;
                    }
                }

                localMovie.CustomFormats = _formatCalculator.ParseCustomFormat(localMovie);
                localMovie.CustomFormatScore = localMovie.Movie.QualityProfile?.CalculateCustomFormatScore(localMovie.CustomFormats) ?? 0;

                return new PreparedDecision(localMovie, null, null);
            }
            catch (AugmentingFailedException)
            {
                return new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.UnableToParse, "Unable to parse file")), null);
            }
            catch (Exception ex)
            {
                return new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.Error, "Unexpected error processing file")), ex);
            }
        }

        private void LogDecision(ImportDecision decision, LocalMovie localMovie)
        {
            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localMovie.Path);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("File rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("File accepted");
            }
        }

        private ImportRejection EvaluateSpec(IImportDecisionEngineSpecification spec, LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            try
            {
                var result = spec.IsSatisfiedBy(localMovie, downloadClientItem);

                if (!result.Accepted)
                {
                    return new ImportRejection(result.Reason, result.Message);
                }
            }
            catch (NotImplementedException e)
            {
                _logger.Warn(e, "Spec " + spec.ToString() + " currently does not implement evaluation for movies.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't evaluate decision on {0}", localMovie.Path);
                return new ImportRejection(ImportRejectionReason.DecisionError, $"{spec.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        private int GetProbeDegreeOfParallelism()
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

        // Reads IMPORT_PROBE_TIMEOUT (whole seconds) in the same style as IMPORT_PROBE_THREADS. The
        // default of 0 (and any value <= 0) means "off": probes are waited on indefinitely, exactly the
        // current behaviour. Any positive value is the per-probe budget after which a probe is abandoned.
        private static TimeSpan GetProbeTimeout()
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

                    // Create the timer stopped, publish it, then arm it, so its callback can never run
                    // (and dispose it) before timers[index] is assigned.
                    var timer = new Timer(_ => Settle(index, true));
                    timers[index] = timer;
                    timer.Change(timeout, Timeout.InfiniteTimeSpan);

                    var thread = new Thread(() =>
                    {
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

        private sealed class PreparedDecision
        {
            public PreparedDecision(LocalMovie localMovie, ImportDecision decision, Exception error)
            {
                LocalMovie = localMovie;
                Decision = decision;
                Error = error;
            }

            public LocalMovie LocalMovie { get; }

            public ImportDecision Decision { get; }

            public Exception Error { get; }
        }
    }
}
