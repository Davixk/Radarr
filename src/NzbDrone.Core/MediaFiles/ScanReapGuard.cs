using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
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

    // fork5: gate for the dangling-symlink reaper (b) that lives in the DiskScanService size loop. This is a
    // DESTRUCTIVE feature (it deletes symlinks and marks the library file missing), so reaping is only ever
    // allowed when the storage backing the symlink target reads HEALTHY this pass. Health is derived per-link
    // by walking UP from the target toward the filesystem root and deciding at the FIRST ancestor that
    // exists: a populated ancestor means the backing storage is mounted and this one link's target really
    // went away (reap); an empty ancestor (a cleanly-unmounted mountpoint) or a faulting ancestor
    // (ENOTCONN/EIO transport fault) means the whole backing mount is down or degraded (never reap). There is
    // no configured storage root; the anchor is discovered by the walk.
    //
    // The ancestor probe MUST use an errno-preserving enumerate (GetFileSystemEntries ->
    // Directory.EnumerateFileSystemEntries), which THROWS DirectoryNotFoundException for an absent dir and
    // IOException for a transport fault. It must NOT use Directory.Exists / FolderExists / FolderEmpty: those
    // swallow every error and return false, so a faulting mount would look "absent" and the walk would escape
    // UP past the dead mountpoint into the populated host filesystem and reap the whole library.
    //
    // Knob (read once, lazily, cached for the process lifetime):
    //   REAP_DANGLING_SYMLINKS : bool, default FALSE. Master switch for the reaper. The operator sets it true
    //                            to enable; while unset the reaper no-ops.
    public class ScanReapGuard : IScanReapGuard, IHandle<ApplicationStartedEvent>
    {
        private const bool DEFAULT_REAP_ENABLED = false;

        // Ancestor state is cached per distinct dir for a short window so a shared ancestor (e.g. a 63k-entry
        // storage root) is probed at most once per dir per pass rather than once per file.
        private static readonly TimeSpan AncestorStateTtl = TimeSpan.FromSeconds(30);

        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<string, (AncestorState State, DateTime CheckedUtc)> _ancestorState = new ConcurrentDictionary<string, (AncestorState, DateTime)>();

        private readonly object _initLock = new object();
        private bool _initialized;
        private bool _reaperEnabled;

        public ScanReapGuard(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
        }

        // Classification of a single ancestor directory as seen through an errno-preserving enumerate.
        private enum AncestorState
        {
            Absent,
            Populated,
            Empty,
            Faulting
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
        // DirectoryNotFoundException). Returns true (caller reaps) ONLY when the reaper is enabled AND the
        // storage backing the link's target reads healthy via the ancestor walk-up. Reaps on the FIRST ENOENT
        // under healthy storage: no consecutive-pass wait, no strike counting.
        public bool ShouldReap(string linkPath)
        {
            EnsureInitialized();

            if (!_reaperEnabled)
            {
                return false;
            }

            var target = ResolveTarget(linkPath);

            return IsStorageHealthyByWalkUp(target);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            EnsureInitialized();

            // Overlay-loaded proof: this line exists only in the patched core, so the wording is identical
            // across both forks and can be grepped to confirm the overlay loaded.
            _logger.Info("fork5 config: dangling-symlink reaper {0} (storage health via target walk-up), cleanup empty-enum bail on, SQLITE_BUSY_TIMEOUT={1}ms",
                _reaperEnabled ? "ENABLED" : "disabled",
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
                _initialized = true;
            }
        }

        // Resolves the absolute path the size read was actually pointing at, so the walk-up starts from the
        // real target. LinkTarget is non-throwing and does not follow the link; null means "not a symlink"
        // (a plain file that read ENOENT), in which case the file's own path is the target.
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

        // Walk UP from the target's parent to the FIRST ancestor that exists, and decide there. Absent
        // ancestors are skipped (keep walking up); the first existing ancestor anchors the decision: populated
        // means healthy storage (reap), empty or faulting means the backing mount is down/degraded (abort).
        // Stops BEFORE the filesystem/drive root and aborts if the walk reaches it without anchoring.
        private bool IsStorageHealthyByWalkUp(string target)
        {
            var dir = Path.GetDirectoryName(target);

            while (dir != null && Path.GetPathRoot(dir) != dir)
            {
                switch (ProbeAncestor(dir))
                {
                    case AncestorState.Absent:
                        dir = Path.GetDirectoryName(dir);
                        continue;
                    case AncestorState.Populated:
                        return true;
                    case AncestorState.Empty:
                        return false;
                    case AncestorState.Faulting:
                        return false;
                }
            }

            return false;
        }

        private AncestorState ProbeAncestor(string dir)
        {
            var now = DateTime.UtcNow;

            if (_ancestorState.TryGetValue(dir, out var cached) && now - cached.CheckedUtc < AncestorStateTtl)
            {
                return cached.State;
            }

            var state = ClassifyAncestor(dir);
            _ancestorState[dir] = (state, now);

            return state;
        }

        // The critical safety point: this uses an errno-preserving enumerate. An absent dir throws
        // DirectoryNotFoundException / FileNotFoundException (ENOENT) and a transport fault throws IOException
        // (ENOTCONN/EIO). A swallowing existence check would report a faulting mount as absent and let the
        // walk escape up past the dead mountpoint into the populated host filesystem.
        private AncestorState ClassifyAncestor(string dir)
        {
            try
            {
                var hasEntry = _diskProvider.GetFileSystemEntries(dir).Any();
                return hasEntry ? AncestorState.Populated : AncestorState.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return AncestorState.Absent;
            }
            catch (FileNotFoundException)
            {
                return AncestorState.Absent;
            }
            catch (IOException)
            {
                return AncestorState.Faulting;
            }
            catch (UnauthorizedAccessException)
            {
                return AncestorState.Faulting;
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
    }
}
