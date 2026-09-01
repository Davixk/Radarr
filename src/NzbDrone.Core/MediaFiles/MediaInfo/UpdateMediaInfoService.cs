using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles.MediaInfo
{
    public interface IUpdateMediaInfo
    {
        bool Update(MovieFile movieFile, Movie movie);
        bool UpdateMediaInfo(MovieFile movieFile, Movie movie);
    }

    public class UpdateMediaInfoService : IUpdateMediaInfo, IHandle<MovieScannedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IVideoFileInfoReader _videoFileInfoReader;
        private readonly IDolbyVisionEnforcementService _dvEnforcementService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public UpdateMediaInfoService(IDiskProvider diskProvider,
                                IMediaFileService mediaFileService,
                                IVideoFileInfoReader videoFileInfoReader,
                                IDolbyVisionEnforcementService dvEnforcementService,
                                IConfigService configService,
                                Logger logger)
        {
            _diskProvider = diskProvider;
            _mediaFileService = mediaFileService;
            _videoFileInfoReader = videoFileInfoReader;
            _dvEnforcementService = dvEnforcementService;
            _configService = configService;
            _logger = logger;
        }

        public void Handle(MovieScannedEvent message)
        {
            if (!_configService.EnableMediaInfo)
            {
                _logger.Debug("MediaInfo is disabled");
                return;
            }

            var allMediaFiles = _mediaFileService.GetFilesByMovie(message.Movie.Id);
            var filteredMediaFiles = allMediaFiles.Where(c =>
                c.MediaInfo == null ||
                c.MediaInfo.SchemaRevision < VideoFileInfoReader.MINIMUM_MEDIA_INFO_SCHEMA_REVISION).ToList();

            foreach (var mediaFile in filteredMediaFiles)
            {
                UpdateMediaInfo(mediaFile, message.Movie);
            }

            // fork24 backstop: enforce the DV exclusion on EVERY file after the scan re-probe, independent of
            // the schema filter above (which permanently skips files already at the current MediaInfo schema -
            // including one that leaked into the library before this fix). Runs on the freshly-probed local
            // MediaInfo, the only fully reliable DV read. No-op unless the operator has configured an exclusion.
            if (DolbyVisionSpecification.IsExclusionActive())
            {
                foreach (var mediaFile in _mediaFileService.GetFilesByMovie(message.Movie.Id))
                {
                    _dvEnforcementService.EnforceOnLibraryFile(message.Movie, mediaFile);
                }
            }
        }

        public bool Update(MovieFile movieFile, Movie movie)
        {
            if (!_configService.EnableMediaInfo)
            {
                _logger.Debug("MediaInfo is disabled");
                return false;
            }

            return UpdateMediaInfo(movieFile, movie);
        }

        public bool UpdateMediaInfo(MovieFile movieFile, Movie movie)
        {
            var path = movieFile.Path.IsNotNullOrWhiteSpace() ? movieFile.Path : Path.Combine(movie.Path, movieFile.RelativePath);

            if (!_diskProvider.FileExists(path))
            {
                _logger.Debug("Can't update MediaInfo because '{0}' does not exist", path);
                return false;
            }

            var updatedMediaInfo = _videoFileInfoReader.GetMediaInfo(path);

            if (updatedMediaInfo == null)
            {
                return false;
            }

            movieFile.MediaInfo = updatedMediaInfo;
            _mediaFileService.Update(movieFile);
            _logger.Debug("Updated MediaInfo for '{0}'", path);

            return true;
        }
    }
}
