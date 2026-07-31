using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileTableCleanupService
    {
        void Clean(Movie movie, List<string> filesOnDisk);
    }

    public class MediaFileTableCleanupService : IMediaFileTableCleanupService
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IMovieService _movieService;
        private readonly Logger _logger;

        public MediaFileTableCleanupService(IMediaFileService mediaFileService,
                                            IMovieService movieService,
                                            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _movieService = movieService;
            _logger = logger;
        }

        public void Clean(Movie movie, List<string> filesOnDisk)
        {
            var movieFiles = _mediaFileService.GetFilesByMovie(movie.Id);

            var filesOnDiskKeys = new HashSet<string>(filesOnDisk, PathEqualityComparer.Instance);

            // fork4: if disk enumeration returned nothing while the DB still holds file records, a mount or
            // enumeration failure is far likelier than every file having genuinely vanished. Skip the
            // deletions this pass rather than mass-marking the whole library missing. On by default.
            if (filesOnDiskKeys.Count == 0 && movieFiles.Count > 0)
            {
                _logger.Warn("Disk enumeration returned no files for {0} while {1} record(s) exist; skipping cleanup deletions to avoid data loss on a possible mount failure.", movie, movieFiles.Count);
                return;
            }

            // fork4: optional fractional cap (CLEANUP_MAX_DELETE_FRACTION, default 1.0 = off). When set
            // below 1 and the share of records that would be deleted this pass exceeds it, skip the
            // deletions rather than remove a suspiciously large fraction at once.
            var maxDeleteFraction = GetMaxDeleteFraction();

            if (maxDeleteFraction < 1.0 && movieFiles.Count > 0)
            {
                var wouldDelete = movieFiles.Count(movieFile => !filesOnDiskKeys.Contains(Path.Combine(movie.Path, movieFile.RelativePath)));

                if ((double)wouldDelete / movieFiles.Count > maxDeleteFraction)
                {
                    _logger.Warn("Cleanup would delete {0} of {1} record(s) for {2}, exceeding CLEANUP_MAX_DELETE_FRACTION={3}; skipping deletions this pass.", wouldDelete, movieFiles.Count, movie, maxDeleteFraction);
                    return;
                }
            }

            foreach (var movieFile in movieFiles)
            {
                var movieFilePath = Path.Combine(movie.Path, movieFile.RelativePath);

                try
                {
                    if (!filesOnDiskKeys.Contains(movieFilePath))
                    {
                        _logger.Debug("File [{0}] no longer exists on disk, removing from db", movieFilePath);
                        _mediaFileService.Delete(movieFile, DeleteMediaFileReason.MissingFromDisk);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    var errorMessage = string.Format("Unable to cleanup MovieFile in DB: {0}", movieFile.Id);
                    _logger.Error(ex, errorMessage);
                }
            }
        }

        // Reads CLEANUP_MAX_DELETE_FRACTION. Default 1.0 (cap off). Only a value in (0,1] arms the cap;
        // anything else leaves it off so a typo can never start skipping legitimate cleanups.
        private static double GetMaxDeleteFraction()
        {
            var raw = Environment.GetEnvironmentVariable("CLEANUP_MAX_DELETE_FRACTION");

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction > 0.0 && fraction <= 1.0)
            {
                return fraction;
            }

            return 1.0;
        }
    }
}
