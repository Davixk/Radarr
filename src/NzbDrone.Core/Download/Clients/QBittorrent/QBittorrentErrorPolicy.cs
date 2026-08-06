using System;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    // fork9: qBittorrent maps a torrent 'error' state to a Warning by design, specifically so that failed
    // download handling is NOT triggered (a real qBittorrent error can be transient or recoverable, and
    // auto-blocklisting it would be wrong). The decypharr qBittorrent shim instead uses 'error' as a
    // terminal "parked-failed" marker, where the desired behaviour is the opposite: let the arr treat it
    // as a failed download and self-heal (blocklist + re-search, and remove the dead row per the client's
    // own RemoveFailedDownloads setting).
    //
    // QBIT_ERROR_AS_FAILED opts into that mapping. Default off preserves upstream behaviour exactly; set
    // it to 1/true/yes/on to enable. The switch is scoped to the 'error' state only, so every other
    // torrent state keeps its stock mapping.
    public static class QBittorrentErrorPolicy
    {
        public static bool ErrorStateAsFailed()
        {
            var envValue = Environment.GetEnvironmentVariable("QBIT_ERROR_AS_FAILED");

            if (envValue.IsNullOrWhiteSpace())
            {
                return false;
            }

            var value = envValue.Trim();

            return value == "1" ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
