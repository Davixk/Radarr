using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    // fork22 (Area-1, operator ruling: option B): gate the file's probed AUDIO languages against what the
    // release was GRABBED as (parsed from the grabbed release title). Catches a release grabbed/parsed as one
    // language whose content is actually another - it would otherwise import silently (the motivating fraud:
    // parsed Italian / CF 1200, file English-only). Rejection is a visible importBlocked, never a silent wrong
    // import (operator doctrine). Conservative: only a CLEAR mismatch rejects (the file has NONE of the
    // concretely-grabbed languages); anything unprobed/unknown accepts. null MediaInfo -> Accept, same
    // convention as HasAudioTrackSpecification (cannot gate what we could not probe).
    public class AudioLanguageSpecification : IImportDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public AudioLanguageSpecification(Logger logger)
        {
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            if (localMovie.ExistingFile)
            {
                return ImportSpecDecision.Accept();
            }

            if (localMovie.MediaInfo == null)
            {
                return ImportSpecDecision.Accept();
            }

            var releaseTitle = localMovie.Release?.Title;

            if (releaseTitle.IsNullOrWhiteSpace())
            {
                return ImportSpecDecision.Accept();
            }

            var grabbedLanguages = LanguageParser.ParseLanguages(releaseTitle)
                                                 .Where(l => l.Id != Language.Unknown.Id && l.Id != Language.Original.Id)
                                                 .ToList();

            if (grabbedLanguages.Empty())
            {
                return ImportSpecDecision.Accept();
            }

            var audioLanguages = localMovie.MediaInfo.AudioLanguages
                                           .Select(a => IsoLanguages.Find(a)?.Language)
                                           .Where(l => l != null && l.Id != Language.Unknown.Id)
                                           .ToList();

            if (audioLanguages.Empty())
            {
                return ImportSpecDecision.Accept();
            }

            if (grabbedLanguages.Any(g => audioLanguages.Any(a => a.Id == g.Id)))
            {
                return ImportSpecDecision.Accept();
            }

            var grabbed = string.Join(", ", grabbedLanguages.Select(l => l.Name));
            var actual = string.Join(", ", audioLanguages.Select(l => l.Name));

            _logger.Debug("Release grabbed as [{0}] but the file's audio languages are [{1}]", grabbed, actual);

            return ImportSpecDecision.Reject(ImportRejectionReason.AudioLanguageMismatch, "Release was grabbed as [{0}] but the file contains no matching audio ([{1}]); manual import required", grabbed, actual);
        }
    }
}
