using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    // fork23 #1 (operator-commissioned): reject files whose Dolby Vision profile OR BL-signal-compatibility-id
    // is on an operator-set exclusion list. Motivating case: DV Profile 5 renders with magenta/green tints on
    // his client and is unwatchable; other profile/compat combos are untested on his other devices, so BOTH
    // are independently configurable rather than a hardcoded constant.
    //
    // Two env lists, comma-separated ints, BOTH EMPTY BY DEFAULT -> with neither set this spec is a pure no-op
    // and the arr behaves exactly as stock (his explicit ruling: no default bad values, this is opt-in). Reject
    // if DvProfile is in DV_REJECT_PROFILES OR DvBlSignalCompatibilityId is in DV_REJECT_COMPAT_IDS. null
    // MediaInfo or null DoviConfigurationRecord (= not Dolby Vision) -> ACCEPT, same convention as the other
    // MediaInfo-based specs (cannot gate what we could not probe / what is not DV).
    public class DolbyVisionSpecification : IImportDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public DolbyVisionSpecification(Logger logger)
        {
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var dovi = localMovie.MediaInfo?.DoviConfigurationRecord;

            if (dovi == null)
            {
                return ImportSpecDecision.Accept();
            }

            if (ParseRejectList("DV_REJECT_PROFILES").Contains(dovi.DvProfile))
            {
                _logger.Debug("Dolby Vision profile {0} is on the DV_REJECT_PROFILES exclusion list", dovi.DvProfile);

                return ImportSpecDecision.Reject(ImportRejectionReason.DolbyVisionExcluded, "Dolby Vision profile {0} (compatibility id {1}) is excluded by configuration; manual import required", dovi.DvProfile, dovi.DvBlSignalCompatibilityId);
            }

            if (ParseRejectList("DV_REJECT_COMPAT_IDS").Contains(dovi.DvBlSignalCompatibilityId))
            {
                _logger.Debug("Dolby Vision compatibility id {0} is on the DV_REJECT_COMPAT_IDS exclusion list", dovi.DvBlSignalCompatibilityId);

                return ImportSpecDecision.Reject(ImportRejectionReason.DolbyVisionExcluded, "Dolby Vision profile {0} (compatibility id {1}) is excluded by configuration; manual import required", dovi.DvProfile, dovi.DvBlSignalCompatibilityId);
            }

            return ImportSpecDecision.Accept();
        }

        private static HashSet<int> ParseRejectList(string envName)
        {
            var values = new HashSet<int>();
            var raw = Environment.GetEnvironmentVariable(envName);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return values;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var value))
                {
                    values.Add(value);
                }
            }

            return values;
        }
    }
}
