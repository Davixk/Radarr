using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IScanReapGuard
    {
        bool ReaperEnabled { get; }
        bool ShouldReap(string linkPath);
    }

    // fork4: gate for the dangling-symlink reaper (b) that lives in the DiskScanService size loop. This is a
    // DESTRUCTIVE feature (it deletes symlinks and marks the library file missing), so reaping is only ever
    // allowed when the operator has declared where the symlink targets live (REAP_STORAGE_ROOT) AND that
    // storage root reads HEALTHY this pass. The health check is what tells "a single link's target really
    // went away" apart from "the whole backing mount is down"; a down/empty/faulting mount NEVER reaps.
    //
    // Knobs (read once, lazily, cached for the process lifetime):
    //   REAP_DANGLING_SYMLINKS : bool, default true. Master switch for the reaper.
    //   REAP_STORAGE_ROOT      : comma-separated list of storage-root directories the symlink targets live
    //                            under. UNSET by default; while unset the reaper no-ops (safe default even
    //                            though the reaper defaults on): (b) deletes nothing until the operator
    //                            declares where the backing storage root is.
    public class ScanReapGuard : IScanReapGuard, IHandle<ApplicationStartedEvent>
    {
        private const bool DEFAULT_REAP_ENABLED = true;

        // Root health is cached per distinct root for a short window so a 63k-entry directory is probed at
        // most once per root per pass rather than once per file.
        private static readonly TimeSpan RootHealthTtl = TimeSpan.FromSeconds(30);

        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<string, (bool Healthy, DateTime CheckedUtc)> _rootHealth = new ConcurrentDictionary<string, (bool, DateTime)>();

        private readonly object _initLock = new object();
        private bool _initialized;
        private bool _reaperEnabled;
        private string[] _storageRoots;

        public ScanReapGuard(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public bool ReaperEnabled
        {
            get
            {
                EnsureInitialized();
                return _reaperEnabled;
            }
        }

        // Called when a tracked file's size read threw ENOENT (FileNotFoundException /
        // DirectoryNotFoundException). Returns true (caller reaps) ONLY when ALL hold:
        //   - the reaper is enabled AND at least one REAP_STORAGE_ROOT is configured
        //   - the link's target path resolves under one of the configured storage roots
        //   - that storage root is HEALTHY this pass (see IsRootHealthy)
        // Reaps on the FIRST ENOENT under a healthy root: no consecutive-pass wait, no strike counting.
        public bool ShouldReap(string linkPath)
        {
            EnsureInitialized();

            // Reaper off, or the operator has not declared a storage root: never reap (safe default).
            if (!_reaperEnabled || _storageRoots.Length == 0)
            {
                return false;
            }

            var target = ResolveTarget(linkPath);

            if (target == null)
            {
                // Target could not be derived: cannot verify against a storage root, so never reap.
                return false;
            }

            var root = MatchStorageRoot(target);

            if (root == null)
            {
                // Target is not under any configured storage root: cannot verify, so never reap.
                return false;
            }

            // Root absent, empty, or faulting => never reap.
            return IsRootHealthy(root);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            EnsureInitialized();

            // Overlay-loaded proof: this line exists only in the patched core, so the wording is identical
            // across both forks and can be grepped to confirm the overlay loaded.
            _logger.Info("fork4 config: dangling-symlink reaper {0}, storage roots [{1}], cleanup empty-enum bail on, SQLITE_BUSY_TIMEOUT={2}ms",
                _reaperEnabled ? "ENABLED" : "disabled",
                string.Join(",", _storageRoots),
                ConnectionStringFactory.GetBusyTimeout());
        }

        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (_initLock)
            {
                if (_initialized)
                {
                    return;
                }

                _reaperEnabled = GetReaperEnabled();
                _storageRoots = GetStorageRoots();
                _initialized = true;
            }
        }

        // Resolves the absolute path the size read was actually pointing at, so it can be matched against a
        // storage root. LinkTarget is non-throwing and does not follow the link; null means "not a symlink"
        // (a plain file that read ENOENT), in which case the file's own path is what must sit under a root.
        private string ResolveTarget(string linkPath)
        {
            string linkTarget;

            try
            {
                linkTarget = new FileInfo(linkPath).LinkTarget;
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "Unable to read link target for {0}", linkPath);
                return null;
            }

            if (linkTarget == null)
            {
                return linkPath;
            }

            if (!Path.IsPathRooted(linkTarget))
            {
                var linkDir = Path.GetDirectoryName(linkPath);
                linkTarget = linkDir == null ? linkTarget : Path.Combine(linkDir, linkTarget);
            }

            return linkTarget;
        }

        private string MatchStorageRoot(string target)
        {
            foreach (var root in _storageRoots)
            {
                if (target.PathEquals(root) || root.IsParentPath(target))
                {
                    return root;
                }
            }

            return null;
        }

        private bool IsRootHealthy(string root)
        {
            var now = DateTime.UtcNow;

            if (_rootHealth.TryGetValue(root, out var cached) && now - cached.CheckedUtc < RootHealthTtl)
            {
                return cached.Healthy;
            }

            var healthy = ProbeRoot(root);
            _rootHealth[root] = (healthy, now);

            return healthy;
        }

        private bool ProbeRoot(string root)
        {
            try
            {
                // Absent or empty => unhealthy. FolderEmpty enumerates lazily (only the first entry is
                // pulled), so a 63k-entry root is never materialized. Any transport fault (EIO / ENOTCONN)
                // raised while probing throws and is treated as unhealthy below.
                return _diskProvider.FolderExists(root) && !_diskProvider.FolderEmpty(root);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Storage root health check failed for {0}; treating as unhealthy and not reaping this pass", root);
                return false;
            }
        }

        private static bool GetReaperEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("REAP_DANGLING_SYMLINKS");

            if (bool.TryParse(raw, out var enabled))
            {
                return enabled;
            }

            return DEFAULT_REAP_ENABLED;
        }

        private static string[] GetStorageRoots()
        {
            var raw = Environment.GetEnvironmentVariable("REAP_STORAGE_ROOT");

            if (raw.IsNullOrWhiteSpace())
            {
                return Array.Empty<string>();
            }

            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(root => root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                      .Where(root => root.Length > 0)
                      .ToArray();
        }
    }
}
