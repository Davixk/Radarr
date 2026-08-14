using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class ImportFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.1998")
                                                    .Build();

            var remoteMovie = BuildRemoteMovie();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteMovie = remoteMovie)
                    .With(c => c.ConsecutiveImportFailures = 0)
                    .With(c => c.ImportFailedPermanently = false)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.MostRecentForDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new MovieHistory());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Drone.1998"))
                  .Returns(remoteMovie.Movie);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);

            // The commit phase re-validates the decisions against the current DB before importing; the
            // default here is a pass-through (nothing was imported concurrently) so behaviour matches the
            // original serial decide-then-import flow.
            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.RevalidateApprovedDecisions(It.IsAny<List<ImportDecision>>(), It.IsAny<DownloadClientItem>()))
                  .Returns<List<ImportDecision>, DownloadClientItem>((decisions, downloadClientItem) => decisions);
        }

        private RemoteMovie BuildRemoteMovie()
        {
            return new RemoteMovie
            {
                Movie = new Movie()
            };
        }

        // Wires the split decide/commit seam so the completed download imports the given results: the
        // decide phase returns a batch carrying the decisions, and the commit phase returns the results.
        private void GivenImportResults(List<ImportResult> importResults)
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.DecidePath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns<string, ImportMode, Movie, DownloadClientItem>((path, mode, movie, downloadClientItem) =>
                      new DownloadedMovieImportBatch
                      {
                          Decisions = importResults.Select(r => r.ImportDecision).ToList(),
                          Movie = movie,
                          ImportMode = mode,
                          DownloadClientItem = downloadClientItem
                      });

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ImportDecidedBatch(It.IsAny<DownloadedMovieImportBatch>()))
                  .Returns(importResults);
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
               .Setup(s => s.MostRecentForDownloadId(It.Is<string>(i => i == "1234")))
               .Returns(new MovieHistory() { SourceTitle = "Droned 1998" });

            Mocker.GetMock<IParsingService>()
               .Setup(s => s.GetMovie(It.IsAny<string>()))
               .Returns((Movie)null);

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.GetMovie("Droned 1998"))
                .Returns(BuildRemoteMovie().Movie);
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteMovie.Movie);
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_rejected()
        {
            GivenImportResults(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1999.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent<DownloadCompletedEvent>(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_no_movies_were_parsed()
        {
            GivenImportResults(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_skipped()
        {
            GivenImportResults(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_terminally_block_after_repeated_import_failures()
        {
            // fork20: a download that fails the import commit on MaxImportFailures (3) consecutive passes must
            // stop retrying forever - it is marked ImportFailedPermanently and left visibly ImportBlocked.
            GivenImportResults(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);
            _trackedDownload.ImportFailedPermanently.Should().BeFalse();

            Subject.Import(_trackedDownload);
            _trackedDownload.ImportFailedPermanently.Should().BeFalse();

            Subject.Import(_trackedDownload);
            _trackedDownload.ImportFailedPermanently.Should().BeTrue();
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [Test]
        public void should_not_revive_a_permanently_import_failed_download()
        {
            // fork20: once terminal, Check must not flip it back to ImportPending (the eternal-retry loop).
            _trackedDownload.State = TrackedDownloadState.ImportBlocked;
            _trackedDownload.ImportFailedPermanently = true;
            _trackedDownload.DownloadItem.Status = DownloadItemStatus.Completed;

            Subject.Check(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [Test]
        public void should_mark_as_imported_if_all_movies_were_imported_but_extra_files_were_not()
        {
            GivenSeriesMatch();

            _trackedDownload.RemoteMovie.Movie = new Movie();

            GivenImportResults(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie })),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
               });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_the_download_can_be_tracked_using_the_source_movieid()
        {
            GivenABadlyNamedDownload();

            GivenImportResults(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie }))
               });

            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovie(It.IsAny<int>()))
                  .Returns(BuildRemoteMovie().Movie);

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_revalidate_decisions_against_current_db_before_committing()
        {
            GivenImportResults(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv", Movie = _trackedDownload.RemoteMovie.Movie }))
               });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IMakeImportDecision>()
                  .Verify(s => s.RevalidateApprovedDecisions(It.IsAny<List<ImportDecision>>(), It.IsAny<DownloadClientItem>()), Times.Once());
        }

        [Test]
        public void should_not_mark_as_imported_from_history_when_the_movie_no_longer_has_a_file()
        {
            _trackedDownload.RemoteMovie.Movie = new Movie { Id = 1 };

            // Nothing imported this pass, matching the live bug: a fresh grab of a release whose previously-imported
            // file was since deleted must NOT be marked imported+removed purely from history.
            GivenImportResults(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure")
                           });

            var history = Builder<MovieHistory>.CreateListOfSize(2).BuildList();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(It.IsAny<TrackedDownload>(), It.IsAny<List<MovieHistory>>()))
                  .Returns(true);

            // The historically-imported movie currently has NO file on disk (deleted since the original import).
            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(It.IsAny<int>()))
                  .Returns(new Movie { Id = 1, MovieFileId = 0 });

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        private void AssertNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private void AssertImported()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Verify(v => v.DecidePath(_trackedDownload.DownloadItem.OutputPath.FullPath, ImportMode.Auto, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem), Times.Once());

            Mocker.GetMock<IDownloadedMovieImportService>()
                .Verify(v => v.ImportDecidedBatch(It.IsAny<DownloadedMovieImportBatch>()), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }
    }
}
