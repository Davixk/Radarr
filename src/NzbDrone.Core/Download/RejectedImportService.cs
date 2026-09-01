using System.Linq;
using NLog;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;

namespace NzbDrone.Core.Download;

public interface IRejectedImportService
{
    bool Process(TrackedDownload trackedDownload, ImportResult importResult);
}

public class RejectedImportService : IRejectedImportService
{
    private readonly ICachedIndexerSettingsProvider _cachedIndexerSettingsProvider;
    private readonly Logger _logger;

    public RejectedImportService(ICachedIndexerSettingsProvider cachedIndexerSettingsProvider, Logger logger)
    {
        _cachedIndexerSettingsProvider = cachedIndexerSettingsProvider;
        _logger = logger;
    }

    public bool Process(TrackedDownload trackedDownload, ImportResult importResult)
    {
        if (importResult.Result != ImportResultType.Rejected || trackedDownload.RemoteMovie?.Release == null)
        {
            return false;
        }

        var rejectionReason = importResult.ImportDecision.Rejections.FirstOrDefault()?.Reason;

        // fork24: an excluded Dolby Vision profile can only be known after the file is probed at import,
        // so it cannot be refused synchronously at grab and MUST be blocklisted - otherwise the burned
        // search is re-grabbed forever. This is an operator-wide policy, independent of the per-indexer
        // FailDownloads settings that gate the dangerous/executable branches below, so it is handled first
        // and even when indexer settings fail to resolve. Fail(reason) carries the DV reason (with its
        // stable [DV-EXCLUDED] token) into DownloadFailedEvent -> the blocklist Message, and the same
        // event drives the configured re-search (RedownloadFailedDownloadService, gated on AutoRedownloadFailed).
        if (rejectionReason == ImportRejectionReason.DolbyVisionExcluded)
        {
            var reason = importResult.Errors?.FirstOrDefault() ?? DolbyVisionSpecification.BlocklistToken;
            _logger.Debug("Download '{0}' rejected for an excluded Dolby Vision profile; failing for blocklist + re-search", trackedDownload.DownloadItem.Title);
            trackedDownload.Fail(reason);
            return true;
        }

        var indexerSettings = _cachedIndexerSettingsProvider.GetSettings(trackedDownload.RemoteMovie.Release.IndexerId);

        if (indexerSettings == null)
        {
            trackedDownload.Warn(new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, importResult.Errors));
            return true;
        }

        if (rejectionReason == ImportRejectionReason.DangerousFile &&
            indexerSettings.FailDownloads.Contains(FailDownloads.PotentiallyDangerous))
        {
            _logger.Trace("Download '{0}' contains potentially dangerous file, marking as failed", trackedDownload.DownloadItem.Title);
            trackedDownload.Fail();
        }
        else if (rejectionReason == ImportRejectionReason.ExecutableFile &&
            indexerSettings.FailDownloads.Contains(FailDownloads.Executables))
        {
            _logger.Trace("Download '{0}' contains executable file, marking as failed", trackedDownload.DownloadItem.Title);
            trackedDownload.Fail();
        }
        else
        {
            trackedDownload.Warn(new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, importResult.Errors));
        }

        return true;
    }
}
