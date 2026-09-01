using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.MediaInfo;
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
        // fork24: stable, machine-greppable prefix token that begins every DV-exclusion rejection (and
        // therefore every DV blocklist Message). Lets the operator find the whole DV-excluded class with a
        // plain string match and bulk-un-blocklist it later (e.g. if a compat value turns out fine on a new
        // client) WITHOUT a schema/audit column. Do NOT change this string once shipped - it is a contract.
        public const string BlocklistToken = "[DV-EXCLUDED]";

        private const string RejectMessageFormat = BlocklistToken + " Dolby Vision profile {0} (compatibility id {1}) is excluded by configuration; blocklisted for re-search";

        private readonly Logger _logger;

        public DolbyVisionSpecification(Logger logger)
        {
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var message = GetExclusionMessage(localMovie.MediaInfo);

            if (message == null)
            {
                return ImportSpecDecision.Accept();
            }

            _logger.Debug("Rejecting import: {0}", message);

            return ImportSpecDecision.Reject(ImportRejectionReason.DolbyVisionExcluded, message);
        }

        // fork24: true when the operator has configured any DV exclusion (either env list non-empty). When
        // false the whole feature is a pure no-op, so the import-time reliable re-probe (ImportApprovedMovie)
        // and the scan backstop (UpdateMediaInfoService) skip their extra work entirely and behave as stock.
        public static bool IsExclusionActive()
        {
            return ParseRejectList("DV_REJECT_PROFILES").Count > 0 || ParseRejectList("DV_REJECT_COMPAT_IDS").Count > 0;
        }

        // fork24: THE single source of truth for "is this file's Dolby Vision excluded". Returns the stable,
        // token-prefixed, retraceable reason string if the profile OR compat id is on an exclusion list;
        // null for null MediaInfo, a non-DV file, or a permitted profile. Shared by the import spec, the
        // import-time reliable re-probe, and the scan backstop so all three enforce and message identically.
        public static string GetExclusionMessage(MediaInfoModel mediaInfo)
        {
            var dovi = mediaInfo?.DoviConfigurationRecord;

            if (dovi == null)
            {
                return null;
            }

            if (ParseRejectList("DV_REJECT_PROFILES").Contains(dovi.DvProfile) ||
                ParseRejectList("DV_REJECT_COMPAT_IDS").Contains(dovi.DvBlSignalCompatibilityId))
            {
                return string.Format(RejectMessageFormat, dovi.DvProfile, dovi.DvBlSignalCompatibilityId);
            }

            return null;
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
