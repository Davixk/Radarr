using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         Logger logger)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                              .Where(t => t.IsTrackable)
                                              .ToList();

            // The completed-download import is split into a read-only DECIDE phase (the expensive
            // ffprobe/media-info/decision work) and a mutating COMMIT phase (the actual import, state
            // changes and events). The DECIDE phase is run with bounded concurrency ACROSS downloads so a
            // backlog of single-file downloads no longer probes one file at a time behind a serial
            // foreach, and a wedged probe is abandoned on IMPORT_PROBE_TIMEOUT instead of blocking the
            // whole batch. The COMMIT phase stays strictly serial and in the original download order, and
            // re-validates each decision against the current DB so nothing is double-imported.
            var pendingImports = new PendingImport[trackedDownloads.Count];

            if (enableCompletedDownloadHandling)
            {
                // Cheap serial preparation (resolve import item, validate path/movie). Kept serial
                // because it can call the download client, which is not guaranteed concurrency-safe.
                var probeIndexes = new List<int>();

                for (var i = 0; i < trackedDownloads.Count; i++)
                {
                    var trackedDownload = trackedDownloads[i];

                    if (trackedDownload.State != TrackedDownloadState.ImportPending)
                    {
                        continue;
                    }

                    try
                    {
                        pendingImports[i] = _completedDownloadService.PrepareImport(trackedDownload);

                        if (pendingImports[i].Status == PendingImportStatus.ReadyToProbe)
                        {
                            probeIndexes.Add(i);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Debug(e, "Failed to prepare download for import: {0}", trackedDownload.DownloadItem.Title);
                    }
                }

                // Expensive read-only DECIDE phase, fanned out across downloads with bounded concurrency
                // and abandon-on-timeout. A wedged probe leaves its PendingImport.Batch null so the commit
                // phase skips it while the healthy downloads still import.
                ImportProbePool.Run(probeIndexes.Count, j =>
                {
                    var index = probeIndexes[j];

                    try
                    {
                        _completedDownloadService.ProbeImport(pendingImports[index]);
                    }
                    catch (Exception e)
                    {
                        _logger.Debug(e, "Failed to compute import decisions for download: {0}", trackedDownloads[index].DownloadItem.Title);
                    }
                });
            }

            for (var i = 0; i < trackedDownloads.Count; i++)
            {
                var trackedDownload = trackedDownloads[i];

                try
                {
                    // Process completed items followed by failed, this allows failed imports to have
                    // their state changed and be processed immediately instead of the next execution.

                    if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending && pendingImports[i] != null)
                    {
                        _completedDownloadService.CompleteImport(pendingImports[i]);
                    }

                    if (trackedDownload.State == TrackedDownloadState.FailedPending)
                    {
                        _failedDownloadService.ProcessFailed(trackedDownload);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
