using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class DiskScanServiceFixture : CoreTest<DiskScanService>
    {
        private const string StorageRoot = @"C:\storage\__all__";

        private Movie _movie;
        private string _previousReap;

        [SetUp]
        public void SetUp()
        {
            _movie = Builder<Movie>.CreateNew()
                                   .With(m => m.Path = (StorageRoot + @"\Movie Title").AsOsAgnostic())
                                   .Build();

            // Snapshot and clear the fork5 reaper knob so each test drives it explicitly and never leaks. The
            // master switch now defaults OFF, so the enable tests must set it true.
            _previousReap = Environment.GetEnvironmentVariable("REAP_DANGLING_SYMLINKS");
            Environment.SetEnvironmentVariable("REAP_DANGLING_SYMLINKS", null);

            // The reaper's walk-up health check must run for real, so inject a real ScanReapGuard backed by
            // the same mocked IDiskProvider the scan uses.
            Mocker.SetConstant<IScanReapGuard>(new ScanReapGuard(Mocker.GetMock<IDiskProvider>().Object, TestLogger));
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("REAP_DANGLING_SYMLINKS", _previousReap);
        }

        private List<MovieFile> GivenMovieFiles(params string[] relativePaths)
        {
            var movieFiles = relativePaths.Select((relativePath, index) => Builder<MovieFile>.CreateNew()
                                                                                             .With(f => f.Id = index + 1)
                                                                                             .With(f => f.RelativePath = relativePath)
                                                                                             .With(f => f.Size = 100)
                                                                                             .Build())
                                          .ToList();

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesByMovie(_movie.Id))
                  .Returns(movieFiles);

            // Let the scan reach and complete the size loop: the movie folder exists and disk enumeration
            // returns nothing (all work under test happens against the existing DB records).
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(_movie.Path))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFiles(It.IsAny<string>(), It.IsAny<bool>()))
                  .Returns(new List<string>());

            return movieFiles;
        }

        private string PathOf(string relativePath)
        {
            return Path.Combine(_movie.Path, relativePath);
        }

        private void GivenReaperEnabled(bool? enabled)
        {
            Environment.SetEnvironmentVariable("REAP_DANGLING_SYMLINKS", enabled?.ToString());
        }

        private void GivenSizeReadThrows(string relativePath, Exception exception)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSizeStrict(PathOf(relativePath)))
                  .Throws(exception);
        }

        private void GivenSizeRead(string relativePath, long size)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSizeStrict(PathOf(relativePath)))
                  .Returns(size);
        }

        // Walk-up ancestor states, driven per-directory through the errno-preserving GetFileSystemEntries the
        // guard uses: an absent dir THROWS (ENOENT), a transport fault THROWS IOException (ENOTCONN/EIO), a
        // populated dir returns a non-empty enumerable, an empty dir returns an empty enumerable.
        private void GivenAncestorAbsent(string dir)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSystemEntries(dir))
                  .Throws(new DirectoryNotFoundException());
        }

        private void GivenAncestorPopulated(string dir)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSystemEntries(dir))
                  .Returns(new[] { Path.Combine(dir, "child") });
        }

        private void GivenAncestorEmpty(string dir)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSystemEntries(dir))
                  .Returns(Enumerable.Empty<string>());
        }

        private void GivenAncestorFaults(string dir)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSystemEntries(dir))
                  .Throws(new IOException("simulated ENOTCONN/EIO"));
        }

        private void VerifyNoReap()
        {
            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFile(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [Test]
        public void Scan_continues_past_a_file_that_reads_ENOENT_and_does_not_throw()
        {
            GivenMovieFiles("dead.mkv", "good.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());
            GivenSizeRead("good.mkv", 999);

            Assert.DoesNotThrow(() => Subject.Scan(_movie));

            // The loop finished (scan completed) and, with the reaper defaulting off, nothing was reaped.
            VerifyEventPublished<MovieScannedEvent>();
            VerifyNoReap();
        }

        [Test]
        public void Reaper_reaps_when_first_existing_ancestor_is_populated()
        {
            GivenReaperEnabled(true);
            var movieFiles = GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            var movieFolder = _movie.Path;
            var storageAll = Path.GetDirectoryName(movieFolder);
            var storage = Path.GetDirectoryName(storageAll);

            // Target parent and grandparent do not exist; the first ancestor that DOES exist is populated, so
            // the backing storage is mounted and this single link's target really went away.
            GivenAncestorAbsent(movieFolder);
            GivenAncestorAbsent(storageAll);
            GivenAncestorPopulated(storage);

            Subject.Scan(_movie);

            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFile(PathOf("dead.mkv")), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(movieFiles[0], DeleteMediaFileReason.MissingFromDisk), Times.Once());
        }

        [Test]
        public void Reaper_aborts_when_first_existing_ancestor_is_empty()
        {
            GivenReaperEnabled(true);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            var movieFolder = _movie.Path;
            var storageAll = Path.GetDirectoryName(movieFolder);
            var storage = Path.GetDirectoryName(storageAll);

            // Intermediate ancestors are gone; the first existing ancestor is EMPTY (a cleanly-unmounted
            // mountpoint), so the backing storage is not mounted and nothing may be reaped.
            GivenAncestorAbsent(movieFolder);
            GivenAncestorAbsent(storageAll);
            GivenAncestorEmpty(storage);

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_reaps_empty_torrent_dir_when_storage_above_is_populated()
        {
            GivenReaperEnabled(true);
            var movieFiles = GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            var movieFolder = _movie.Path;                        // the emptied torrent dir (its file is gone)
            var storageAll = Path.GetDirectoryName(movieFolder);  // still-mounted storage above it

            // fork7 Path B: the target's own directory EXISTS but is EMPTY (the file was removed while the
            // torrent dir and the storage above it stay mounted) and NO ENOENT gap precedes it, so the walk
            // must climb past the empty dir, find the populated storage root, and reap. Before Path B the walk
            // stopped at the first empty ancestor and (wrongly) aborted, leaving these links unreaped.
            GivenAncestorEmpty(movieFolder);
            GivenAncestorPopulated(storageAll);

            Subject.Scan(_movie);

            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFile(PathOf("dead.mkv")), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(movieFiles[0], DeleteMediaFileReason.MissingFromDisk), Times.Once());
        }

        [Test]
        public void Reaper_marks_missing_when_the_symlink_parent_directory_is_gone()
        {
            // fork23 #2: storage is healthy and the link is reapable, but the parent directory was removed so
            // DeleteFile throws DirectoryNotFoundException. The reaper must treat that as already-removed - still
            // mark the record missing and NOT abort the whole rescan.
            GivenReaperEnabled(true);
            var movieFiles = GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            var movieFolder = _movie.Path;
            var storageAll = Path.GetDirectoryName(movieFolder);
            GivenAncestorEmpty(movieFolder);
            GivenAncestorPopulated(storageAll);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.DeleteFile(PathOf("dead.mkv")))
                  .Throws(new DirectoryNotFoundException());

            Assert.DoesNotThrow(() => Subject.Scan(_movie));

            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(movieFiles[0], DeleteMediaFileReason.MissingFromDisk), Times.Once());
        }

        [Test]
        public void Reaper_aborts_when_an_ancestor_faults()
        {
            GivenReaperEnabled(true);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            var movieFolder = _movie.Path;
            var storageAll = Path.GetDirectoryName(movieFolder);

            // An ancestor faults with a plain IOException (ENOTCONN/EIO): the transport is degraded, abort.
            GivenAncestorAbsent(movieFolder);
            GivenAncestorFaults(storageAll);

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_does_NOT_escape_past_a_faulting_ancestor_to_a_populated_grandparent()
        {
            GivenReaperEnabled(true);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            var movieFolder = _movie.Path;
            var mountPoint = Path.GetDirectoryName(movieFolder);      // the dead mount
            var hostFilesystem = Path.GetDirectoryName(mountPoint);   // populated host fs above the mount

            // The immediate ancestor is absent, the mountpoint-level ancestor FAULTS (ENOTCONN/EIO), and the
            // host filesystem ABOVE the mount is populated. A swallowing existence check (Directory.Exists /
            // FolderExists) would treat the faulting mount as "absent" and let the walk escape UP to the
            // populated host filesystem and reap the whole library. The errno-preserving enumerate must stop
            // the walk at the fault.
            GivenAncestorAbsent(movieFolder);
            GivenAncestorFaults(mountPoint);
            GivenAncestorPopulated(hostFilesystem);

            Subject.Scan(_movie);

            // The walk stopped at the fault and never reached the populated grandparent: nothing reaped.
            VerifyNoReap();
        }

        [Test]
        public void Reaper_aborts_pass_on_non_ENOENT_IOException_from_size_read()
        {
            GivenReaperEnabled(true);
            GivenMovieFiles("faulting.mkv");
            GivenSizeReadThrows("faulting.mkv", new IOException("Transport endpoint is not connected"));

            Assert.Throws<IOException>(() => Subject.Scan(_movie));

            // A transport fault on the size read aborts the pass before the reaper is ever consulted: nothing
            // deleted, nothing marked missing.
            VerifyNoReap();
        }

        [Test]
        public void Reaper_reap_deletes_symlink_and_marks_missing_no_blocklist()
        {
            GivenReaperEnabled(true);
            var movieFiles = GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            // Simplest healthy walk-up: the target's own parent directory exists and is populated.
            GivenAncestorPopulated(_movie.Path);

            Subject.Scan(_movie);

            // First ENOENT under healthy storage reaps: the symlink inode is unlinked and the record is marked
            // missing with NO blocklist and NO history (DiskScanService touches neither service).
            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFile(PathOf("dead.mkv")), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(movieFiles[0], DeleteMediaFileReason.MissingFromDisk), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.Is<DeleteMediaFileReason>(r => r != DeleteMediaFileReason.MissingFromDisk)), Times.Never());
        }

        [Test]
        public void Reaper_does_nothing_when_REAP_DANGLING_SYMLINKS_off()
        {
            GivenReaperEnabled(false);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            // Even with a populated (healthy) ancestor, an explicitly-disabled reaper never acts.
            GivenAncestorPopulated(_movie.Path);

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_defaults_off()
        {
            // REAP_DANGLING_SYMLINKS unset (cleared in SetUp): the master switch now defaults OFF.
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            Subject.Scan(_movie);

            VerifyNoReap();
        }
    }
}
