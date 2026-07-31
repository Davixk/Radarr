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
        private string _root;
        private string _previousReap;
        private string _previousRoot;

        [SetUp]
        public void SetUp()
        {
            _root = StorageRoot.AsOsAgnostic();

            _movie = Builder<Movie>.CreateNew()
                                   .With(m => m.Path = (StorageRoot + @"\Movie Title").AsOsAgnostic())
                                   .Build();

            // Snapshot and clear the fork4 reaper knobs so each test drives them explicitly and never leaks.
            _previousReap = Environment.GetEnvironmentVariable("REAP_DANGLING_SYMLINKS");
            _previousRoot = Environment.GetEnvironmentVariable("REAP_STORAGE_ROOT");
            Environment.SetEnvironmentVariable("REAP_DANGLING_SYMLINKS", null);
            Environment.SetEnvironmentVariable("REAP_STORAGE_ROOT", null);

            // The reaper's cross-file state and root-health checks must run for real, so inject a real
            // ScanReapGuard backed by the same mocked IDiskProvider the scan uses.
            Mocker.SetConstant<IScanReapGuard>(new ScanReapGuard(Mocker.GetMock<IDiskProvider>().Object, TestLogger));
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("REAP_DANGLING_SYMLINKS", _previousReap);
            Environment.SetEnvironmentVariable("REAP_STORAGE_ROOT", _previousRoot);
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

        private void GivenReaperConfig(bool? enabled, string storageRoot)
        {
            Environment.SetEnvironmentVariable("REAP_DANGLING_SYMLINKS", enabled?.ToString());
            Environment.SetEnvironmentVariable("REAP_STORAGE_ROOT", storageRoot);
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

        private void GivenRootHealthy()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(_root)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderEmpty(_root)).Returns(false);
        }

        private void GivenRootAbsent()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(_root)).Returns(false);
        }

        private void GivenRootEmpty()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(_root)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderEmpty(_root)).Returns(true);
        }

        private void GivenRootEnumerationThrows()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(_root)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderEmpty(_root)).Throws(new IOException("simulated ENOTCONN/EIO"));
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

            // The loop finished (scan completed) and, with no storage root configured, nothing was reaped.
            VerifyEventPublished<MovieScannedEvent>();
            VerifyNoReap();
        }

        [Test]
        public void Reaper_does_NOT_reap_when_storage_root_absent()
        {
            GivenReaperConfig(true, _root);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());
            GivenRootAbsent();

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_does_NOT_reap_when_storage_root_present_but_empty()
        {
            GivenReaperConfig(true, _root);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());
            GivenRootEmpty();

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_does_NOT_reap_when_root_enumeration_throws_IOException()
        {
            GivenReaperConfig(true, _root);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());
            GivenRootEnumerationThrows();

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_aborts_pass_on_non_ENOENT_IOException_from_size_read()
        {
            GivenReaperConfig(true, _root);
            GivenMovieFiles("faulting.mkv");
            GivenSizeReadThrows("faulting.mkv", new IOException("Transport endpoint is not connected"));
            GivenRootHealthy();

            Assert.Throws<IOException>(() => Subject.Scan(_movie));

            // A transport fault aborts before acting: nothing deleted, nothing marked missing.
            VerifyNoReap();
        }

        [Test]
        public void Reaper_reap_deletes_symlink_and_marks_missing_no_blocklist()
        {
            GivenReaperConfig(true, _root);
            var movieFiles = GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());
            GivenRootHealthy();

            Subject.Scan(_movie);

            // First ENOENT under a healthy root reaps: the symlink inode is unlinked and the record is
            // marked missing with NO blocklist and NO history (DiskScanService touches neither service).
            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFile(PathOf("dead.mkv")), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(movieFiles[0], DeleteMediaFileReason.MissingFromDisk), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.Is<DeleteMediaFileReason>(r => r != DeleteMediaFileReason.MissingFromDisk)), Times.Never());
        }

        [Test]
        public void Reaper_does_nothing_when_REAP_DANGLING_SYMLINKS_off()
        {
            GivenReaperConfig(false, _root);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());
            GivenRootHealthy();

            Subject.Scan(_movie);

            VerifyNoReap();
        }

        [Test]
        public void Reaper_does_nothing_when_REAP_STORAGE_ROOT_unset()
        {
            GivenReaperConfig(true, null);
            GivenMovieFiles("dead.mkv");
            GivenSizeReadThrows("dead.mkv", new FileNotFoundException());

            Assert.DoesNotThrow(() => Subject.Scan(_movie));

            VerifyNoReap();
        }
    }
}
