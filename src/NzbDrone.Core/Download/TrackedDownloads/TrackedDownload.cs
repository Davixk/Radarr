using System;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class TrackedDownload
    {
        public int DownloadClient { get; set; }
        public DownloadClientItem DownloadItem { get; set; }
        public DownloadClientItem ImportItem { get; set; }
        public TrackedDownloadState State { get; set; }
        public TrackedDownloadStatus Status { get; private set; }
        public RemoteMovie RemoteMovie { get; set; }
        public TrackedDownloadStatusMessage[] StatusMessages { get; private set; }
        public DownloadProtocol Protocol { get; set; }
        public string Indexer { get; set; }
        public DateTime? Added { get; set; }
        public bool IsTrackable { get; set; }
        public bool HasNotifiedManualInteractionRequired { get; set; }

        // fork7 #4: consecutive ProcessMonitoredDownloads passes on which this download's import probe timed
        // out. When it reaches IMPORT_PROBE_TIMEOUT_STRIKES the download is failed (blocklist + re-search)
        // instead of retried into the same unreadable file forever. Reset on any non-timeout probe outcome.
        public int ConsecutiveProbeTimeouts { get; set; }

        // fork20: consecutive passes on which this download reached the import COMMIT and failed to import
        // (a deterministic throw during the move e.g. PathTooLong, the source folder gone, or nothing
        // eligible). At MaxImportFailures the download is marked ImportFailedPermanently and left visibly
        // ImportBlocked for manual action instead of being revived into the same failing import forever.
        // Kept as ImportBlocked (not Failed) so it never collides with the fork19 Failed+completed
        // re-evaluation. Both reset when the client re-downloads a fresh copy (Status back to Downloading).
        public int ConsecutiveImportFailures { get; set; }
        public bool ImportFailedPermanently { get; set; }

        // fork21 (A): last time a stuck Failed download was re-routed through the recovery flow (remove +
        // re-search). A tracked download marked Failed is inert in every processing path, so a client entry
        // that fails AGAIN (or a decypharr-side failure that lands after the arr already wrote Failed) has no
        // exit and sits as permanent litter. On re-observation of Failed + client-still-failed it is dropped
        // back to Downloading to re-run the configured recovery, rate-limited by this timestamp so an entry
        // that resists removal retries periodically instead of re-firing every refresh (search flood).
        public DateTime? LastFailedRecoveryAttempt { get; set; }

        // fork24: an explicit, retraceable reason set when a download is failed for a policy violation
        // detected at import (an excluded Dolby Vision profile) rather than a client-side failure.
        // ProcessFailed prefers this over the generic "Failed download detected" so the reason - which
        // carries a stable, greppable token - reaches the blocklist row's Message for later auditing /
        // bulk un-blocklisting. Null for ordinary client failures, which keep the generic message.
        public string FailureReason { get; private set; }

        public TrackedDownload()
        {
            StatusMessages = Array.Empty<TrackedDownloadStatusMessage>();
        }

        public void Warn(string message, params object[] args)
        {
            var statusMessage = string.Format(message, args);
            Warn(new TrackedDownloadStatusMessage(DownloadItem.Title, statusMessage));
        }

        public void Warn(params TrackedDownloadStatusMessage[] statusMessages)
        {
            Status = TrackedDownloadStatus.Warning;
            StatusMessages = statusMessages;
        }

        public void Fail()
        {
            Status = TrackedDownloadStatus.Error;
            State = TrackedDownloadState.FailedPending;

            // Set CanBeRemoved to allow the failed item to be removed from the client
            DownloadItem.CanBeRemoved = true;
        }

        // fork24: fail with an explicit, retraceable reason (carried into the blocklist Message via
        // ProcessFailed -> DownloadFailedEvent). Used for the DV-exclusion policy failure.
        public void Fail(string reason)
        {
            FailureReason = reason;
            Fail();
        }
    }

    public enum TrackedDownloadState
    {
        Downloading,
        ImportBlocked,
        ImportPending,
        Importing,
        Imported,
        FailedPending,
        Failed,
        Ignored
    }

    public enum TrackedDownloadStatus
    {
        Ok,
        Warning,
        Error
    }
}
