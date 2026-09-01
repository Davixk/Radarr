using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDolbyVisionEnforcementService
    {
        bool EnforceOnLibraryFile(Movie movie, MovieFile movieFile);
    }

    // fork24: the guaranteed backstop for the DV exclusion. The import-time gate + reliable re-probe close
    // the live window, but a file can still be in the library from before this fix (or an import path the
    // gate did not cover). The library scan re-probes every file locally - the only fully reliable read -
    // so this enforces the exclusion on that reliable MediaInfo: remove the file, blocklist the release it
    // came from (with the retraceable [DV-EXCLUDED] reason from its grabbed history), and re-search. All DV
    // enforcement points share DolbyVisionSpecification's exclusion logic + message, so they act identically.
    public class DolbyVisionEnforcementService : IDolbyVisionEnforcementService
    {
        private readonly IDeleteMediaFiles _deleteMediaFiles;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly Logger _logger;

        public DolbyVisionEnforcementService(IDeleteMediaFiles deleteMediaFiles,
                                             IFailedDownloadService failedDownloadService,
                                             IHistoryService historyService,
                                             Logger logger)
        {
            _deleteMediaFiles = deleteMediaFiles;
            _failedDownloadService = failedDownloadService;
            _historyService = historyService;
            _logger = logger;
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
