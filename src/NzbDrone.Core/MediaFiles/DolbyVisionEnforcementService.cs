using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDolbyVisionEnforcementService
    {
        bool EnforceOnLibraryFile(Movie movie, MovieFile movieFile);
    }

    // fork24: the guaranteed backstop for the DV exclusion. The import-time gate + reliable re-probe close
    // the live window, but a file can still be in the library from before this fix (or an import path the
    // gate did not cover). This runs on every movie scan (AFTER UpdateMediaInfoService's re-probe via
    // EventHandleOrder.Last) so it sees the reliable local MediaInfo, and enforces the exclusion on every
    // file INDEPENDENT of the media-info schema filter (which permanently skips already-current-schema
    // files - the very case that leaked in before this fix): remove the file, blocklist the release it came
    // from (with the retraceable [DV-EXCLUDED] reason from its grabbed history), and re-search. It is a
    // scan-event handler rather than a dependency of UpdateMediaInfoService on purpose: injecting it into
    // UpdateMediaInfoService closes a DI cycle (UpdateMediaInfoService -> this -> IDeleteMediaFiles ->
    // ISeriesService -> ... -> FileNameBuilder -> IUpdateMediaInfo). All DV enforcement points share
    // DolbyVisionSpecification's exclusion logic + message, so they act identically.
    public class DolbyVisionEnforcementService : IDolbyVisionEnforcementService, IHandle<MovieScannedEvent>
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IDeleteMediaFiles _deleteMediaFiles;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly Logger _logger;

        public DolbyVisionEnforcementService(IMediaFileService mediaFileService,
                                             IDeleteMediaFiles deleteMediaFiles,
                                             IFailedDownloadService failedDownloadService,
                                             IHistoryService historyService,
                                             Logger logger)
        {
            _mediaFileService = mediaFileService;
            _deleteMediaFiles = deleteMediaFiles;
            _failedDownloadService = failedDownloadService;
            _historyService = historyService;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(MovieScannedEvent message)
        {
            if (!DolbyVisionSpecification.IsExclusionActive())
            {
                return;
            }

            foreach (var movieFile in _mediaFileService.GetFilesByMovie(message.Movie.Id))
            {
                EnforceOnLibraryFile(message.Movie, movieFile);
            }
        }

        public bool EnforceOnLibraryFile(Movie movie, MovieFile movieFile)
        {
            var message = DolbyVisionSpecification.GetExclusionMessage(movieFile?.MediaInfo);

            if (message == null)
            {
                return false;
            }

            _logger.Warn("Library file for {0} reads as excluded Dolby Vision ({1}); removing, blocklisting and re-searching", movie, message);

            var grab = _historyService.GetByMovieId(movie.Id, MovieHistoryEventType.Grabbed)
                .OrderByDescending(h => h.Date)
                .FirstOrDefault();

            _deleteMediaFiles.DeleteMovieFile(movie, movieFile);

            if (grab != null)
            {
                _failedDownloadService.MarkAsFailed(grab.Id, message);
            }
            else
            {
                _logger.Debug("No grabbed history for {0}; removed the excluded file but there is no source release to blocklist", movie);
            }

            return true;
        }
    }
}
