using System.Collections.Generic;
using System.IO;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    // Carries the result of the read-only DECIDE phase (DownloadedMovieImportService.DecidePath) to the
    // mutating COMMIT phase (DownloadedMovieImportService.ImportDecidedBatch). Splitting the two lets the
    // expensive probe/decision work fan out concurrently across downloads while the actual import stays
    // serial and in order.
    public class DownloadedMovieImportBatch
    {
        // When set, the decide phase reached an early result (unknown movie, locked file, movie-folder
        // rejection, inaccessible path) and the commit phase simply returns these without importing.
        public List<ImportResult> EarlyResults { get; set; }

        // The decisions to import in the commit phase. Mutable so the commit coordinator can substitute a
        // re-validated list (see IMakeImportDecision.RevalidateApprovedDecisions) before committing.
        public List<ImportDecision> Decisions { get; set; }

        public Movie Movie { get; set; }

        public ImportMode ImportMode { get; set; }

        public DownloadClientItem DownloadClientItem { get; set; }

        // The folder being imported, or null for a single-file import. Only the folder case runs the
        // post-import folder cleanup / empty-result checks.
        public DirectoryInfo DirectoryInfo { get; set; }
    }
}
