using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IMakeImportDecision
    {
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, bool filterExistingFiles);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource, bool filterExistingFiles);
        ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem);
        List<ImportDecision> RevalidateApprovedDecisions(List<ImportDecision> decisions, DownloadClientItem downloadClientItem);
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        private readonly IEnumerable<IImportDecisionEngineSpecification> _specifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAggregationService _aggregationService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDetectSample _detectSample;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IMovieService _movieService;
        private readonly Logger _logger;

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification> specifications,
                                   IMediaFileService mediaFileService,
                                   IAggregationService aggregationService,
                                   IDiskProvider diskProvider,
                                   IDetectSample detectSample,
                                   ITrackedDownloadService trackedDownloadService,
                                   ICustomFormatCalculationService formatCalculator,
                                   IMovieService movieService,
                                   Logger logger)
        {
            _specifications = specifications;
            _mediaFileService = mediaFileService;
            _aggregationService = aggregationService;
            _diskProvider = diskProvider;
            _detectSample = detectSample;
            _trackedDownloadService = trackedDownloadService;
            _formatCalculator = formatCalculator;
            _movieService = movieService;
            _logger = logger;
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie)
        {
            return GetImportDecisions(videoFiles, movie, null, null, false);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, bool filterExistingFiles)
        {
            return GetImportDecisions(videoFiles, movie, null, null, false, filterExistingFiles);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource)
        {
            return GetImportDecisions(videoFiles, movie, downloadClientItem, folderInfo, sceneSource, true);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource, bool filterExistingFiles)
        {
            var newFiles = filterExistingFiles ? _mediaFileService.FilterExistingFiles(videoFiles.ToList(), movie) : videoFiles.ToList();

            _logger.Debug("Analyzing {0}/{1} files.", newFiles.Count, videoFiles.Count);

            ParsedMovieInfo downloadClientItemInfo = null;

            if (downloadClientItem != null)
            {
                downloadClientItemInfo = Parser.Parser.ParseMovieTitle(downloadClientItem.Title);
            }

            // Force the lazy-loaded metadata once up front so the parallel passes below only read it.
            MovieMetadata movieMetadata = movie.MovieMetadata;

            // Phase 1 (bounded parallel): sample detection. This folds the previously serial
            // GetNonSampleVideoFileCount pre-pass into the parallel probe region so a slow probe here
            // does not serialize the whole batch. The result is reused by the sample specification so
            // the file is not probed for its runtime a second time. A probe abandoned on timeout defaults
            // to NotSample here; the same wedged file times out again in Phase 2 and is rejected there.
            var sampleResults = new DetectSampleResult[newFiles.Count];

            Action<int> detectSampleBody = i =>
            {
                sampleResults[i] = _detectSample.IsSample(movieMetadata, newFiles[i]);
            };

            var sampleTimedOut = ImportProbePool.Run(newFiles.Count, detectSampleBody);

            for (var i = 0; i < newFiles.Count; i++)
            {
                if (sampleTimedOut[i])
                {
                    sampleResults[i] = DetectSampleResult.NotSample;
                }
            }

            var nonSampleVideoFileCount = sampleResults.Count(r => r != DetectSampleResult.Sample);
            var otherVideoFiles = nonSampleVideoFileCount > 1;

            // Phase 2 (bounded parallel): the probe/aggregate heavy per-file work (parse, media info,
            // custom formats). Results are collected by input index so ordering stays deterministic
            // regardless of the order the probes complete in.
            var prepared = new PreparedDecision[newFiles.Count];

            Func<int, LocalMovie> buildLocalMovie = i =>
            {
                var file = newFiles[i];

                return new LocalMovie
                {
                    Movie = movie,
                    DownloadClientMovieInfo = downloadClientItemInfo,
                    DownloadItem = downloadClientItem,
                    FolderMovieInfo = folderInfo,
                    Path = file,
                    SceneSource = sceneSource,
                    ExistingFile = movie.Path.IsParentPath(file),
                    OtherVideoFiles = otherVideoFiles,
                    SampleResult = sampleResults[i]
                };
            };

            Action<int> prepareBody = i =>
            {
                prepared[i] = Prepare(buildLocalMovie(i), downloadClientItem);
            };

            var prepareTimedOut = ImportProbePool.Run(newFiles.Count, prepareBody);

            for (var i = 0; i < newFiles.Count; i++)
            {
                if (prepareTimedOut[i])
                {
                    // The probe for this file was abandoned. Reject it this pass (reusing the generic
                    // Error reason) so the batch completes and the healthy files still import; the file
                    // is logged in serial Phase 3 and stays pending for a future pass rather than
                    // hanging the whole import.
                    var localMovie = buildLocalMovie(i);

                    prepared[i] = new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.Error, "Probe timed out")), null);
                }
            }

            // Phase 3 (serial, input order): evaluate specifications, assemble decisions and log.
            // Specification evaluation, history/DB lookups and logging all stay single threaded and
            // ordered to keep behaviour, logs and tests deterministic.
            var decisions = new List<ImportDecision>(prepared.Length);

            foreach (var item in prepared)
            {
                if (item.Error != null)
                {
                    _logger.Error(item.Error, "Couldn't import file. {0}", item.LocalMovie.Path);
                }

                var decision = item.Decision ?? GetDecision(item.LocalMovie, downloadClientItem);

                LogDecision(decision, item.LocalMovie);

                decisions.AddIfNotNull(decision);
            }

            return decisions;
        }

        public ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var reasons = _specifications.Select(c => EvaluateSpec(c, localMovie, downloadClientItem))
                                         .Where(c => c != null);

            return new ImportDecision(localMovie, reasons.ToArray());
        }

        // Re-runs the cheap specifications against the CURRENT database state for each already-approved
        // decision, right before it is committed. The probe/decision phase runs concurrently across
        // downloads against a pre-commit snapshot, so two downloads for the same movie can both be
        // approved. Refreshing the movie's file state here (exactly as AggregateMovie does during the
        // decide phase) and re-evaluating lets the already-imported / upgrade specifications reject a
        // second download once an earlier one in the same serial commit pass has imported the movie,
        // reproducing what the original serial "decide immediately before importing" flow would have done.
        // The expensive probe results already carried on each LocalMovie are kept and reused.
        public List<ImportDecision> RevalidateApprovedDecisions(List<ImportDecision> decisions, DownloadClientItem downloadClientItem)
        {
            if (decisions == null)
            {
                return null;
            }

            var revalidated = new List<ImportDecision>(decisions.Count);

            foreach (var decision in decisions)
            {
                if (!decision.Approved)
                {
                    revalidated.Add(decision);
                    continue;
                }

                var localMovie = decision.LocalMovie;

                if (localMovie?.Movie != null)
                {
                    var refreshed = _movieService.GetMovie(localMovie.Movie.Id);

                    if (refreshed != null)
                    {
                        localMovie.Movie = refreshed;
                    }
                }

                var recheck = GetDecision(localMovie, downloadClientItem);

                if (!recheck.Approved)
                {
                    _logger.Debug("Import for {0} rejected on commit re-validation: {1}", localMovie?.Path, string.Join(", ", recheck.Rejections));
                }

                revalidated.Add(recheck);
            }

            return revalidated;
        }

        private PreparedDecision Prepare(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            // Runs inside the bounded parallel region: only the probe/aggregate heavy IO happens here.
            // Any early rejection is captured and returned so the serial phase can log and assemble it
            // in deterministic input order. Exceptions are captured (not logged here) so all logging
            // stays serial.
            try
            {
                var fileMovieInfo = Parser.Parser.ParseMoviePath(localMovie.Path);

                localMovie.FileMovieInfo = fileMovieInfo;
                localMovie.Size = _diskProvider.GetFileSize(localMovie.Path);

                _aggregationService.Augment(localMovie, downloadClientItem);

                if (localMovie.Movie == null)
                {
                    return new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.InvalidMovie, "Invalid movie")), null);
                }

                if (downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
                {
                    var trackedDownload = _trackedDownloadService.Find(downloadClientItem.DownloadId);

                    if (trackedDownload?.RemoteMovie?.Release?.IndexerFlags != null)
                    {
                        localMovie.IndexerFlags = trackedDownload.RemoteMovie.Release.IndexerFlags;
                    }
                }

                localMovie.CustomFormats = _formatCalculator.ParseCustomFormat(localMovie);
                localMovie.CustomFormatScore = localMovie.Movie.QualityProfile?.CalculateCustomFormatScore(localMovie.CustomFormats) ?? 0;

                return new PreparedDecision(localMovie, null, null);
            }
            catch (AugmentingFailedException)
            {
                return new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.UnableToParse, "Unable to parse file")), null);
            }
            catch (Exception ex)
            {
                return new PreparedDecision(localMovie, new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.Error, "Unexpected error processing file")), ex);
            }
        }

        private void LogDecision(ImportDecision decision, LocalMovie localMovie)
        {
            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localMovie.Path);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("File rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("File accepted");
            }
        }

        private ImportRejection EvaluateSpec(IImportDecisionEngineSpecification spec, LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            try
            {
                var result = spec.IsSatisfiedBy(localMovie, downloadClientItem);

                if (!result.Accepted)
                {
                    return new ImportRejection(result.Reason, result.Message);
                }
            }
            catch (NotImplementedException e)
            {
                _logger.Warn(e, "Spec " + spec.ToString() + " currently does not implement evaluation for movies.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't evaluate decision on {0}", localMovie.Path);
                return new ImportRejection(ImportRejectionReason.DecisionError, $"{spec.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        private sealed class PreparedDecision
        {
            public PreparedDecision(LocalMovie localMovie, ImportDecision decision, Exception error)
            {
                LocalMovie = localMovie;
                Decision = decision;
                Error = error;
            }

            public LocalMovie LocalMovie { get; }

            public ImportDecision Decision { get; }

            public Exception Error { get; }
        }
    }
}
