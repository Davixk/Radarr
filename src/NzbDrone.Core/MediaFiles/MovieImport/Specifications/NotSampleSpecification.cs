using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    public class NotSampleSpecification : IImportDecisionEngineSpecification
    {
        private readonly IDetectSample _detectSample;
        private readonly Logger _logger;

        public NotSampleSpecification(IDetectSample detectSample,
                                      Logger logger)
        {
            _detectSample = detectSample;
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            if (localMovie.ExistingFile)
            {
                _logger.Debug("Existing file, skipping sample check");
                return ImportSpecDecision.Accept();
            }

            // Reuse the result computed during the parallel probe phase when available so the file is
            // not probed for its runtime a second time; fall back to probing when it was not precomputed.
            var sample = localMovie.SampleResult ?? _detectSample.IsSample(localMovie.Movie.MovieMetadata, localMovie.Path);

            if (sample == DetectSampleResult.Sample)
            {
                return ImportSpecDecision.Reject(ImportRejectionReason.Sample, "Sample");
            }
            else if (sample == DetectSampleResult.Indeterminate)
            {
                return ImportSpecDecision.Reject(ImportRejectionReason.SampleIndeterminate, "Unable to determine if file is a sample");
            }

            return ImportSpecDecision.Accept();
        }
    }
}
