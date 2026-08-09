using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);

        // Cross-download concurrent import pipeline (coordinated by DownloadProcessingService):
        //  - PrepareImport: cheap serial setup (resolve import item, validate path/movie). Kept serial
        //    because it can call the download client, which is not guaranteed concurrency-safe.
        //  - ProbeImport: expensive read-only decide (the ffprobe/media-info/decision work). Safe to run
        //    concurrently across downloads.
        //  - CompleteImport: mutating serial commit (re-validate against current DB, import, verify,
        //    publish events). Must run serially and in the original download order.
        PendingImport PrepareImport(TrackedDownload trackedDownload);
        void ProbeImport(PendingImport pendingImport);
        void CompleteImport(PendingImport pendingImport);
    }

    public class CompletedDownloadService : ICompletedDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IHistoryService _historyService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IDownloadedMovieImportService _downloadedMovieImportService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IParsingService _parsingService;
        private readonly IMovieService _movieService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IRejectedImportService _rejectedImportService;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedMovieImportService downloadedMovieImportService,
                                        IMakeImportDecision importDecisionMaker,
                                        IParsingService parsingService,
                                        IMovieService movieService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IRejectedImportService rejectedImportService,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _provideImportItemService = provideImportItemService;
            _downloadedMovieImportService = downloadedMovieImportService;
            _importDecisionMaker = importDecisionMaker;
            _parsingService = parsingService;
            _movieService = movieService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _rejectedImportService = rejectedImportService;
            _logger = logger;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading or have been blocked for importing due to an issue with matching
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == MovieHistoryEventType.Grabbed).ToList();
            var historyItem = grabbedHistories.MaxBy(h => h.Date);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Radarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            Movie movie = null;

            try
            {
                movie = _parsingService.GetMovie(trackedDownload.DownloadItem.Title);
            }
            catch (MultipleMoviesFoundException)
            {
                // fork16 (option b, operator ruling): the download's title matches MULTIPLE library movies and no layer
                // can verify which film the content actually is (e.g. Dracula 1931 tt0021814 vs tt0021815). Do NOT
                // auto-resolve via grab history - that would import an assumption. Block VISIBLY for manual import.
                trackedDownload.Warn("Ambiguous title - multiple library matches; manual import required");
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            if (movie == null)
            {
                if (historyItem != null)
                {
                    movie = _movieService.GetMovie(historyItem.MovieId);
                }

                if (movie == null)
                {
                    trackedDownload.Warn("Movie title mismatch, automatic import is not possible. Manual Import required.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }

                Enum.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.MOVIE_MATCH_TYPE, MovieMatchType.Unknown.ToString()), out MovieMatchType movieMatchType);
                Enum.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

                // Show a warning if the release was matched by ID and the source is not interactive search
                if (movieMatchType == MovieMatchType.Id && releaseSource != ReleaseSourceType.InteractiveSearch)
                {
                    trackedDownload.Warn("Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        public void Import(TrackedDownload trackedDownload)
        {
            var pendingImport = PrepareImport(trackedDownload);
            ProbeImport(pendingImport);
            CompleteImport(pendingImport);
        }

        public PendingImport PrepareImport(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return new PendingImport(trackedDownload, PendingImportStatus.InvalidPath);
            }

            if (trackedDownload.RemoteMovie?.Movie == null)
            {
                return new PendingImport(trackedDownload, PendingImportStatus.RemoteMovieMissing);
            }

            return new PendingImport(trackedDownload, PendingImportStatus.ReadyToProbe, trackedDownload.ImportItem.OutputPath.FullPath);
        }

        public void ProbeImport(PendingImport pendingImport)
        {
            if (pendingImport == null || pendingImport.Status != PendingImportStatus.ReadyToProbe)
            {
                return;
            }

            var trackedDownload = pendingImport.TrackedDownload;

            pendingImport.Batch = _downloadedMovieImportService.DecidePath(pendingImport.OutputPath,
                ImportMode.Auto,
                trackedDownload.RemoteMovie.Movie,
                trackedDownload.ImportItem);
        }

        public void CompleteImport(PendingImport pendingImport)
        {
            if (pendingImport == null)
            {
                return;
            }

            var trackedDownload = pendingImport.TrackedDownload;

            switch (pendingImport.Status)
            {
                case PendingImportStatus.InvalidPath:
                    // ValidatePath already warned during preparation; nothing left to commit.
                    return;
                case PendingImportStatus.RemoteMovieMissing:
                    trackedDownload.Warn("Unable to parse download, automatic import is not possible.");
                    SetStateToImportBlocked(trackedDownload);
                    return;
            }

            // ReadyToProbe but the probe was abandoned on timeout (or never ran): leave the download
            // ImportPending for a future pass rather than importing an unprobed download.
            if (pendingImport.Batch == null)
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.Importing;

            // Re-validate the cheap DB-state specifications against the now-current database before
            // committing. The probe/decide phase ran concurrently across downloads against a pre-commit
            // snapshot, so a second download for a movie that an earlier commit in this same serial pass
            // already imported is rejected here instead of being double-imported.
            pendingImport.Batch.Decisions = _importDecisionMaker.RevalidateApprovedDecisions(pendingImport.Batch.Decisions, pendingImport.Batch.DownloadClientItem);

            var outputPath = pendingImport.OutputPath;
            var importResults = _downloadedMovieImportService.ImportDecidedBatch(pendingImport.Batch);

            if (VerifyImport(trackedDownload, importResults))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;

            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);

                return;
            }

            if (importResults.Count == 1)
            {
                var firstResult = importResults.First();

                if (_rejectedImportService.Process(trackedDownload, firstResult))
                {
                    return;
                }
            }

            var statusMessages = new List<TrackedDownloadStatusMessage>
                                 {
                                    new TrackedDownloadStatusMessage("One or more movies expected in this release were not imported or missing", new List<string>())
                                 };

            if (importResults.Any(c => c.Result != ImportResultType.Imported))
            {
                statusMessages.AddRange(
                    importResults
                        .Where(v => v.Result != ImportResultType.Imported && v.ImportDecision.LocalMovie != null)
                        .OrderBy(v => v.ImportDecision.LocalMovie.Path)
                        .Select(v =>
                            new TrackedDownloadStatusMessage(Path.GetFileName(v.ImportDecision.LocalMovie.Path),
                                v.Errors)));
            }

            if (statusMessages.Any())
            {
                trackedDownload.Warn(statusMessages.ToArray());
                SetStateToImportBlocked(trackedDownload);
            }
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var allMoviesImported = importResults.Where(c => c.Result == ImportResultType.Imported)
                                       .Select(c => c.ImportDecision.LocalMovie.Movie)
                                       .Any();

            if (allMoviesImported)
            {
                _logger.Debug("All movies were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));
                return true;
            }

            // Double check if all movies were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.
            var atLeastOneMovieImported = importResults.Any(c => c.Result == ImportResultType.Imported);

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                                                  .OrderByDescending(h => h.Date)
                                                  .ToList();

            var allMoviesImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);

            if (allMoviesImportedInHistory)
            {
                // fork12: history says this movie was imported, but if it currently has NO file on disk (e.g. deleted
                // by a MissingFromDisk wave after the original import), the "already imported" claim is stale. Marking
                // this fresh grab Imported here removes it WITHOUT importing, silently eating a re-grab of a release
                // whose file is gone. Leave it unmarked so the normal import pipeline processes it (its
                // AlreadyImportedSpecification correctly skips the already-imported check for a movie without a file).
                // A genuine duplicate is unaffected: it still has its file.
                var currentMovie = _movieService.GetMovie(trackedDownload.RemoteMovie.Movie.Id);

                if (!currentMovie.HasFile)
                {
                    _logger.Debug("History reports '{0}' already imported, but the movie has no file on disk now; letting the import pipeline process the fresh grab instead of removing it", trackedDownload.DownloadItem.Title);
                    return false;
                }

                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.
                if (atLeastOneMovieImported)
                {
                    _logger.Debug("All movies were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    _logger.ForDebugEvent()
                           .Message("No Movies were just imported, but all movies were previously imported, possible issue with download history.")
                           .Property("MovieId", trackedDownload.RemoteMovie.Movie.Id)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.ImportItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));

                return true;
            }

            _logger.Debug("Not all movies have been imported for {0}", trackedDownload.DownloadItem.Title);
            return false;
        }

        private void SetStateToImportBlocked(TrackedDownload trackedDownload)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;

            if (!trackedDownload.HasNotifiedManualInteractionRequired)
            {
                var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == MovieHistoryEventType.Grabbed).ToList();

                trackedDownload.HasNotifiedManualInteractionRequired = true;

                var releaseInfo = grabbedHistories.Count > 0 ? new GrabbedReleaseInfo(grabbedHistories) : null;
                var manualInteractionEvent = new ManualInteractionRequiredEvent(trackedDownload, releaseInfo);

                _eventAggregator.PublishEvent(manualInteractionEvent);
            }
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping.", downloadItemOutputPath);
                return false;
            }

            return true;
        }
    }
}
