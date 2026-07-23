using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class ImportDecisionMakerRevalidateFixture : CoreTest<ImportDecisionMaker>
    {
        private const int MovieId = 7;

        private DownloadClientItem _downloadClientItem;
        private Mock<IImportDecisionEngineSpecification> _rejectIfMovieHasFile;

        [SetUp]
        public void Setup()
        {
            _downloadClientItem = Builder<DownloadClientItem>.CreateNew().Build();

            // Stands in for the database-state specifications (already-imported / upgrade): a movie that
            // already has a file rejects a same-quality second import, exactly the case the concurrent
            // decide phase cannot see against its pre-commit snapshot.
            _rejectIfMovieHasFile = new Mock<IImportDecisionEngineSpecification>();
            _rejectIfMovieHasFile.Setup(c => c.IsSatisfiedBy(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>()))
                                 .Returns<LocalMovie, DownloadClientItem>((localMovie, downloadClientItem) =>
                                     localMovie.Movie.MovieFileId > 0
                                         ? ImportSpecDecision.Reject(ImportRejectionReason.NotQualityUpgrade, "Not an upgrade for existing movie file")
                                         : ImportSpecDecision.Accept());

            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>(new[] { _rejectIfMovieHasFile.Object });
        }

        private Movie GivenMovie(int movieFileId)
        {
            return Builder<Movie>.CreateNew()
                                 .With(m => m.Id = MovieId)
                                 .With(m => m.MovieFileId = movieFileId)
                                 .Build();
        }

        private ImportDecision GivenApprovedDecision()
        {
            var localMovie = new LocalMovie
            {
                Movie = GivenMovie(0),
                Path = @"C:\Test\The.Movie.2019.1080p.mkv"
            };

            var decision = new ImportDecision(localMovie);
            decision.Approved.Should().BeTrue();

            return decision;
        }

        private void GivenCurrentMovieState(int movieFileId)
        {
            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(MovieId))
                  .Returns(GivenMovie(movieFileId));
        }

        [Test]
        public void should_keep_approved_when_movie_state_is_unchanged()
        {
            var decision = GivenApprovedDecision();
            GivenCurrentMovieState(0);

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { decision }, _downloadClientItem);

            result.Single().Approved.Should().BeTrue();
        }

        [Test]
        public void should_reject_when_movie_already_imported_by_an_earlier_commit()
        {
            var decision = GivenApprovedDecision();

            // Another download for the same movie was committed first, so the movie now has a file.
            GivenCurrentMovieState(99);

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { decision }, _downloadClientItem);

            result.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_import_movie_only_once_when_two_downloads_race()
        {
            var first = GivenApprovedDecision();
            var second = GivenApprovedDecision();

            // First download commits while the movie still has no file: it is approved and imports.
            GivenCurrentMovieState(0);
            var firstResult = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { first }, _downloadClientItem);
            firstResult.Single().Approved.Should().BeTrue();

            // The first commit imported the movie, so the second download re-validates against a movie
            // that now has a file and is rejected: the movie is imported exactly once.
            GivenCurrentMovieState(99);
            var secondResult = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { second }, _downloadClientItem);
            secondResult.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_not_re_evaluate_already_rejected_decisions()
        {
            var localMovie = new LocalMovie { Movie = GivenMovie(0), Path = @"C:\Test\rejected.mkv" };
            var rejected = new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.Sample, "Sample"));

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { rejected }, _downloadClientItem);

            result.Single().Approved.Should().BeFalse();

            // A rejected decision is passed through untouched; its movie state is never refreshed.
            Mocker.GetMock<IMovieService>()
                  .Verify(s => s.GetMovie(It.IsAny<int>()), Times.Never());
        }
    }
}
