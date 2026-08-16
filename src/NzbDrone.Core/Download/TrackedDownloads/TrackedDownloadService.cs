using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadService
    {
        TrackedDownload Find(string downloadId);
        void StopTracking(string downloadId);
        void StopTracking(List<string> downloadIds);
        TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
        List<TrackedDownload> GetTrackedDownloads();
        void UpdateTrackable(List<TrackedDownload> trackedDownloads);
    }

    public class TrackedDownloadService : ITrackedDownloadService,
                                          IHandle<MovieAddedEvent>,
                                          IHandle<MovieEditedEvent>,
                                          IHandle<MoviesBulkEditedEvent>,
                                          IHandle<MoviesDeletedEvent>
    {
        private readonly IParsingService _parsingService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IConfigService _config;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly Logger _logger;
        private readonly ICached<TrackedDownload> _cache;

        public TrackedDownloadService(IParsingService parsingService,
                                      ICacheManager cacheManager,
                                      IHistoryService historyService,
                                      IConfigService config,
                                      IRemoteMovieAggregationService aggregationService,
                                      ICustomFormatCalculationService formatCalculator,
                                      IEventAggregator eventAggregator,
                                      IDownloadHistoryService downloadHistoryService,
                                      Logger logger)
        {
            _parsingService = parsingService;
            _historyService = historyService;
            _cache = cacheManager.GetCache<TrackedDownload>(GetType());
            _config = config;
            _aggregationService = aggregationService;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _downloadHistoryService = downloadHistoryService;
            _logger = logger;
        }

        public TrackedDownload Find(string downloadId)
        {
            return _cache.Find(downloadId);
        }

        public void StopTracking(string downloadId)
        {
            var trackedDownload = _cache.Find(downloadId);

            _cache.Remove(downloadId);
            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { trackedDownload }));
        }

        public void StopTracking(List<string> downloadIds)
        {
            var trackedDownloads = new List<TrackedDownload>();

            foreach (var downloadId in downloadIds)
            {
                var trackedDownload = _cache.Find(downloadId);

                _cache.Remove(downloadId);
                trackedDownloads.Add(trackedDownload);
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(trackedDownloads));
        }

        public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            var existingItem = Find(downloadItem.DownloadId);

            if (existingItem != null && existingItem.State != TrackedDownloadState.Downloading)
            {
                LogItemChange(existingItem, existingItem.DownloadItem, downloadItem);

                existingItem.DownloadItem = downloadItem;
                existingItem.IsTrackable = true;

                // fork19: sticky-Failed re-grab zombie. A download marked Failed (e.g. the errAsFailed path on a
                // storm) whose SAME-hash copy was later re-grabbed and is now completed+healthy at the client
                // keeps its terminal Failed verdict forever - Failed is inert in every processing path, so the
                // importable content sits as "Downloaded" and is never imported, removed, or re-evaluated. When
                // the client item is now a healthy COMPLETED download the failure is stale: drop back to
                // Downloading so ProcessClientItem re-runs the completed-import flow (imports if importable, else
                // normal blocked/failed handling). Guarded on Status==Completed so a CURRENTLY-errored Failed
                // item (client still reporting the error) is left alone - that is the normal failed drain.
                if (existingItem.State == TrackedDownloadState.Failed &&
                    downloadItem.Status == DownloadItemStatus.Completed)
                {
                    _logger.Debug("Download '{0}' was Failed but its client item is now completed and healthy; re-evaluating it for import", downloadItem.Title);
                    existingItem.State = TrackedDownloadState.Downloading;
                }

                // fork20: a terminally import-failed item whose client is re-downloading a fresh copy gets a
                // clean slate - clear the strike + terminal flag so the completed-import flow re-evaluates it
                // when the fresh copy finishes.
                if (downloadItem.Status == DownloadItemStatus.Downloading)
                {
                    existingItem.ConsecutiveImportFailures = 0;
                    existingItem.ImportFailedPermanently = false;
                }

                // fork21 (A): a Failed download whose client is STILL/AGAIN reporting failed is permanent litter
                // - it fired its recovery once (or never) and Failed is inert thereafter. Drop it back to
                // Downloading so the failed-download pipeline re-runs the configured recovery (remove + re-search
                // per AutoRedownloadFailed). Rate-limited per item: a client entry that resists removal retries
                // on the interval below rather than every refresh, so this can never become a search flood.
                if (existingItem.State == TrackedDownloadState.Failed &&
                    downloadItem.Status == DownloadItemStatus.Failed &&
                    (existingItem.LastFailedRecoveryAttempt == null ||
                     DateTime.UtcNow - existingItem.LastFailedRecoveryAttempt.Value > TimeSpan.FromMinutes(60)))
                {
                    _logger.Debug("Download '{0}' is Failed and the client still reports it failed; re-running the recovery flow (remove + re-search)", downloadItem.Title);
                    existingItem.LastFailedRecoveryAttempt = DateTime.UtcNow;
                    existingItem.State = TrackedDownloadState.Downloading;
                }

                return existingItem;
            }

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = downloadClient.Id,
                DownloadItem = downloadItem,
                Protocol = downloadClient.Protocol,
                IsTrackable = true,
                HasNotifiedManualInteractionRequired = existingItem?.HasNotifiedManualInteractionRequired ?? false,

                // fork7 #4: carry the probe-timeout strike count across a rebuild (mirrors the sticky flag
                // above); it resets naturally when the download restarts (goes back to Downloading).
                ConsecutiveProbeTimeouts = existingItem?.ConsecutiveProbeTimeouts ?? 0
            };

            try
            {
                var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId)
                    .OrderByDescending(h => h.Date)
                    .ToList();

                var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title);

                if (parsedMovieInfo != null)
                {
                    try
                    {
                        trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo, "", 0, null);
                    }
                    catch (MultipleMoviesFoundException e)
                    {
                        // fork13: an ambiguous title (e.g. Dracula (1931) tt0021814 vs Dracula (1931) tt0021815)
                        // makes the title-based Map throw. Previously it bubbled to the outer catch, left RemoteMovie
                        // null, and the download stuck - re-erroring every poll (~17MB/9h flood across 8 stuck items).
                        // Swallow it here so the grabbed-history movieId fallback below resolves it deterministically.
                        _logger.Debug(e, "Ambiguous title for '{0}', resolving via grabbed-history movieId instead", downloadItem.Title);
                    }
                }

                var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(downloadItem.DownloadId);

                if (downloadHistory != null)
                {
                    var state = GetStateFromHistory(downloadHistory.EventType);
                    trackedDownload.State = state;
                }

                if (historyItems.Any())
                {
                    var firstHistoryItem = historyItems.First();
                    var grabbedEvent = historyItems.FirstOrDefault(v => v.EventType == MovieHistoryEventType.Grabbed);

                    trackedDownload.Indexer = grabbedEvent?.Data?.GetValueOrDefault("indexer");
                    trackedDownload.Added = grabbedEvent?.Date;

                    if (parsedMovieInfo == null ||
                        trackedDownload.RemoteMovie?.Movie == null)
                    {
                        parsedMovieInfo = Parser.Parser.ParseMovieTitle(firstHistoryItem.SourceTitle);

                        if (parsedMovieInfo != null)
                        {
                            trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo,
                                firstHistoryItem.MovieId);
                        }
                    }

                    if (trackedDownload.RemoteMovie != null)
                    {
                        trackedDownload.RemoteMovie.Release ??= new ReleaseInfo();
                        trackedDownload.RemoteMovie.Release.Indexer = trackedDownload.Indexer;
                        trackedDownload.RemoteMovie.Release.Title = trackedDownload.RemoteMovie.ParsedMovieInfo?.ReleaseTitle;

                        if (Enum.TryParse(grabbedEvent?.Data?.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                        {
                            trackedDownload.RemoteMovie.Release.IndexerFlags = flags;
                        }

                        if (downloadHistory != null)
                        {
                            trackedDownload.RemoteMovie.Release.IndexerId = downloadHistory.IndexerId;
                        }
                    }
                }

                if (trackedDownload.RemoteMovie != null)
                {
                    _aggregationService.Augment(trackedDownload.RemoteMovie);

                    // Calculate custom formats
                    trackedDownload.RemoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(trackedDownload.RemoteMovie, downloadItem.TotalSize);
                }

                // Track it so it can be displayed in the queue even though we can't determine which movie it is for
                if (trackedDownload.RemoteMovie == null)
                {
                    _logger.Trace("No Movie found for download '{0}'", trackedDownload.DownloadItem.Title);
                }
            }
            catch (MultipleMoviesFoundException e)
            {
                _logger.Debug(e, "Found multiple movies for " + downloadItem.Title);

                trackedDownload.Warn("Unable to import automatically, found multiple movies: {0}", string.Join(", ", e.Movies));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find movie for " + downloadItem.Title);
                return null;
            }

            // fork13: SECOND silent-eat site (fork12 only fixed CompletedDownloadService.VerifyImport). A re-grab
            // that reuses a downloadId whose latest download-history event is DownloadImported gets State=Imported
            // above from history alone, with no file-state check - and DownloadProcessingService then removes it
            // (DownloadCanBeRemovedEvent -> RemoveItem) WITHOUT importing. If the movie it "imported" no longer has
            // a file (deleted since the original import), that mark is stale, so downgrade to Downloading and let
            // the completed-download pipeline (file-state-aware since fork12) process the fresh grab instead of
            // silently removing it. A genuine already-imported download still has its file and is untouched.
            if (trackedDownload.State == TrackedDownloadState.Imported &&
                trackedDownload.RemoteMovie?.Movie != null &&
                !trackedDownload.RemoteMovie.Movie.HasFile)
            {
                _logger.Debug("Download '{0}' is marked imported in history, but its movie has no file on disk now; treating it as not-imported so the fresh grab is processed instead of removed", downloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Downloading;
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);

            _cache.Set(trackedDownload.DownloadItem.DownloadId, trackedDownload);
            return trackedDownload;
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var untrackable = GetTrackedDownloads().ExceptBy(t => t.DownloadItem.DownloadId, trackedDownloads, t => t.DownloadItem.DownloadId, StringComparer.CurrentCulture).ToList();

            foreach (var trackedDownload in untrackable)
            {
                trackedDownload.IsTrackable = false;
            }
        }

        private void UpdateCachedItem(TrackedDownload trackedDownload)
        {
            var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title);

            trackedDownload.RemoteMovie = parsedMovieInfo == null ? null : _parsingService.Map(parsedMovieInfo, "", 0, null);

            _aggregationService.Augment(trackedDownload.RemoteMovie);
        }

        private static TrackedDownloadState GetStateFromHistory(DownloadHistoryEventType eventType)
        {
            switch (eventType)
            {
                case DownloadHistoryEventType.DownloadImported:
                    return TrackedDownloadState.Imported;
                case DownloadHistoryEventType.DownloadFailed:
                    return TrackedDownloadState.Failed;
                case DownloadHistoryEventType.DownloadIgnored:
                    return TrackedDownloadState.Ignored;
                default:
                    return TrackedDownloadState.Downloading;
            }
        }

        private void LogItemChange(TrackedDownload trackedDownload, DownloadClientItem existingItem, DownloadClientItem downloadItem)
        {
            if (existingItem == null ||
                existingItem.Status != downloadItem.Status ||
                existingItem.CanBeRemoved != downloadItem.CanBeRemoved ||
                 existingItem.CanMoveFiles != downloadItem.CanMoveFiles)
            {
                _logger.Debug("Tracking '{0}:{1}': ClientState={2}{3} RadarrStage={4} Movie='{5}' OutputPath={6}.",
                    downloadItem.DownloadClientInfo.Name,
                    downloadItem.Title,
                    downloadItem.Status,
                    downloadItem.CanBeRemoved ? "" : downloadItem.CanMoveFiles ? " (busy)" : " (readonly)",
                    trackedDownload.State,
                    trackedDownload.RemoteMovie?.ParsedMovieInfo,
                    downloadItem.OutputPath);
            }
        }

        public void Handle(MovieAddedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie == null ||
                    message.Movie?.TmdbId == t.RemoteMovie.Movie.TmdbId)
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MovieEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    (t.RemoteMovie.Movie.Id == message.Movie?.Id || t.RemoteMovie.Movie.TmdbId == message.Movie?.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesBulkEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesDeletedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
