using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Download
{
    public enum PendingImportStatus
    {
        // Ready to run the (expensive, read-only) probe/decide phase, then commit.
        ReadyToProbe,

        // ValidatePath failed during preparation (already warned); the commit phase skips it.
        InvalidPath,

        // The download could not be parsed to a movie; the commit phase warns and blocks it.
        RemoteMovieMissing
    }

    // Carries one pending completed download through the concurrent DECIDE / serial COMMIT pipeline
    // coordinated by DownloadProcessingService. Preparation (cheap, serial) sets the Status; the probe
    // phase (expensive, concurrent across downloads) fills Batch; the commit phase (serial, in order)
    // consumes it.
    public class PendingImport
    {
        public PendingImport(TrackedDownload trackedDownload, PendingImportStatus status, string outputPath = null)
        {
            TrackedDownload = trackedDownload;
            Status = status;
            OutputPath = outputPath;
        }

        public TrackedDownload TrackedDownload { get; }

        public PendingImportStatus Status { get; }

        public string OutputPath { get; }

        // Filled by the probe phase. Stays null when the download is not ReadyToProbe or when its probe
        // was abandoned on timeout, in which case the commit phase leaves it pending for a future pass.
        public DownloadedMovieImportBatch Batch { get; set; }
    }
}
