using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class DownloadMonitoringService : IExecute<RefreshMonitoredDownloadsCommand>,
                                             IExecute<CheckForFinishedDownloadCommand>,
                                             IHandle<MovieGrabbedEvent>,
                                             IHandle<MovieFileImportedEvent>,
                                             IHandle<ManualInteractionRequiredEvent>,
                                             IHandle<DownloadsProcessedEvent>,
                                             IHandle<TrackedDownloadsRemovedEvent>
    {
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _manageCommandQueue;
        private readonly IConfigService _configService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly Logger _logger;
        private readonly Debouncer _refreshDebounce;

        public DownloadMonitoringService(IDownloadClientStatusService downloadClientStatusService,
                                         IDownloadClientFactory downloadClientFactory,
                                         IEventAggregator eventAggregator,
                                         IManageCommandQueue manageCommandQueue,
                                         IConfigService configService,
                                         IFailedDownloadService failedDownloadService,
                                         ICompletedDownloadService completedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         Logger logger)
        {
            _downloadClientStatusService = downloadClientStatusService;
            _downloadClientFactory = downloadClientFactory;
            _eventAggregator = eventAggregator;
            _manageCommandQueue = manageCommandQueue;
            _configService = configService;
            _failedDownloadService = failedDownloadService;
            _completedDownloadService = completedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _logger = logger;

            _refreshDebounce = new Debouncer(QueueRefresh, TimeSpan.FromSeconds(5));
        }

        private void QueueRefresh()
        {
            _manageCommandQueue.Push(new RefreshMonitoredDownloadsCommand(), CommandPriority.High);
        }

        private void Refresh()
        {
            _refreshDebounce.Pause();
            try
            {
                var downloadClients = _downloadClientFactory.DownloadHandlingEnabled();

                var trackedDownloads = new List<TrackedDownload>();

                foreach (var downloadClient in downloadClients)
                {
                    var clientTrackedDownloads = ProcessClientDownloads(downloadClient);

                    trackedDownloads.AddRange(clientTrackedDownloads.Where(DownloadIsTrackable));
                }

                _trackedDownloadService.UpdateTrackable(trackedDownloads);
                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads));
                _manageCommandQueue.Push(new ProcessMonitoredDownloadsCommand(), CommandPriority.High);
            }
            finally
            {
                _refreshDebounce.Resume();
            }
        }

        private List<TrackedDownload> ProcessClientDownloads(IDownloadClient downloadClient)
        {
            var downloadClientItems = new List<DownloadClientItem>();
            var trackedDownloads = new List<TrackedDownload>();

            try
            {
                downloadClientItems = downloadClient.GetItems().ToList();

                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
            }
            catch (Exception ex)
            {
                // TODO: Stop tracking items for the offline client
                _downloadClientStatusService.RecordFailure(downloadClient.Definition.Id);
                _logger.Warn(ex, "Unable to retrieve queue and history items from " + downloadClient.Definition.Name);
            }

            foreach (var downloadItem in downloadClientItems)
            {
                var item = ProcessClientItem(downloadClient, downloadItem);
                trackedDownloads.AddIfNotNull(item);
            }

            return trackedDownloads;
        }

        private TrackedDownload ProcessClientItem(IDownloadClient downloadClient, DownloadClientItem downloadItem)
        {
            TrackedDownload trackedDownload = null;

            try
            {
                trackedDownload =
                    _trackedDownloadService.TrackDownload((DownloadClientDefinition)downloadClient.Definition,
                        downloadItem);

                if (trackedDownload is { State: TrackedDownloadState.Downloading or TrackedDownloadState.ImportBlocked })
                {
                    _failedDownloadService.Check(trackedDownload);
                    _completedDownloadService.Check(trackedDownload);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't process tracked download {0}", downloadItem.Title);
            }

            return trackedDownload;
        }

        private bool DownloadIsTrackable(TrackedDownload trackedDownload)
        {
            // If the download has already been imported or the user ignored it don't track it
            if (trackedDownload.State == TrackedDownloadState.Imported ||
                trackedDownload.State == TrackedDownloadState.Ignored)
            {
                return false;
            }

            // fork17: State==Failed is deliberately NOT excluded here. Stock drops Failed from tracking, which
            // hides it from the queue (QueueService filters on IsTrackable). Stock gets away with it because
            // errored torrents map to Warning (State stays Downloading/ImportBlocked = visible) and a genuine
            // Failed is removed from the client almost instantly, so the invisible window is a blink. With the
            // per-client "Report Errored Torrents as Failed" knob a large pile can sit in State=Failed awaiting a
            // starved removal slot - invisible in the queue the entire time (the regression: ~800 failed rows,
            // ?status=failed -> 0). Keeping Failed trackable while it is STILL served by the client restores stock
            // visibility (failed shows red until processed); it drops from the queue the instant the client stops
            // serving it (UpdateTrackable's ExceptBy). Inert in every processing path: the probe loop skips
            // non-ImportPending, the commit loop only touches ImportPending/FailedPending, RemoveCompletedDownloads
            // only touches Imported.

            // If CDH is disabled and the download status is complete don't track it
            if (!_configService.EnableCompletedDownloadHandling && trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed)
            {
                return false;
            }

            return true;
        }

        public void Execute(RefreshMonitoredDownloadsCommand message)
        {
            Refresh();
        }

        public void Execute(CheckForFinishedDownloadCommand message)
        {
            _logger.Warn("A third party app used the deprecated CheckForFinishedDownload command, it should be updated RefreshMonitoredDownloads instead");
            Refresh();
        }

        public void Handle(MovieGrabbedEvent message)
        {
            _refreshDebounce.Execute();
        }

        public void Handle(ManualInteractionRequiredEvent message)
        {
            _refreshDebounce.Execute();
        }

        public void Handle(MovieFileImportedEvent message)
        {
            _refreshDebounce.Execute();
        }

        public void Handle(DownloadsProcessedEvent message)
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads().Where(t => t.IsTrackable && DownloadIsTrackable(t)).ToList();

            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads));
        }

        public void Handle(TrackedDownloadsRemovedEvent message)
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads().Where(t => t.IsTrackable && DownloadIsTrackable(t)).ToList();
            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(trackedDownloads));
        }
    }
}
