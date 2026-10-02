using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    // fork28 (operator-commissioned): reject a file whose media-info probe ran to completion and ffprobe named the
    // content itself as not media ("Invalid data found when processing input"). Stock reads every failed probe as
    // "unknown": GetMediaInfo swallows the error and returns null, and the null-MediaInfo specs accept. Only
    // NotSampleSpecification rejects on it, and only as "Unable to determine if file is a sample", which does not
    // say the file is not video and is skipped for existing files. A transient probe failure (missing file, I/O
    // error, timeout kill) is NOT classified as unparseable, so this never rejects on a mount fault.
    public class ParseableMediaSpecification : IImportDecisionEngineSpecification
    {
        // Stable, machine-greppable prefix token that begins every unparseable-media rejection. Do NOT change
        // this string once shipped - it is a contract with the operator's resolver.
        public const string RejectionToken = "[UNPARSEABLE]";

        private readonly Logger _logger;

        public ParseableMediaSpecification(Logger logger)
        {
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var message = GetRejectionMessage(localMovie);

            if (message == null)
            {
                return ImportSpecDecision.Accept();
            }

            _logger.Debug("Rejecting {0}: {1}", localMovie.Path, message);

            return ImportSpecDecision.Reject(ImportRejectionReason.UnparseableMedia, message);
        }

        public static string GetRejectionMessage(LocalMovie localMovie)
        {
            if (localMovie?.MediaInfo != null || string.IsNullOrWhiteSpace(localMovie?.UnparseableReason))
            {
                return null;
            }

            return $"{RejectionToken} Not a valid media file, ffprobe could not parse it ({localMovie.UnparseableReason}); refusing import";
        }
    }
}
