using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Specifications
{
    [TestFixture]
    public class ParseableMediaSpecificationFixture : CoreTest<ParseableMediaSpecification>
    {
        private const string Reason = "[matroska,webm] EBML header parsing failed; Invalid data found when processing input";

        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            var movie = Builder<Movie>.CreateNew()
                                      .With(s => s.Path = Path.Combine(@"C:\Test\Movies".AsOsAgnostic(), "Movie Title"))
                                      .Build();

            _localMovie = new LocalMovie
                          {
                              Path = @"C:\Test\Unsorted\Movie Title\movie.title.2000.mkv".AsOsAgnostic(),
                              Movie = movie
                          };
        }

        [Test]
        public void should_reject_with_the_greppable_token_when_the_probe_proved_the_file_is_not_media()
        {
            _localMovie.MediaInfo = null;
            _localMovie.UnparseableReason = Reason;

            var result = Subject.IsSatisfiedBy(_localMovie, null);

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(ImportRejectionReason.UnparseableMedia);
            result.Message.Should().StartWith(ParseableMediaSpecification.RejectionToken);
            result.Message.Should().Contain(Reason);
        }

        // A null MediaInfo with no classification is a probe that merely failed (mount fault, timeout, disk image,
        // media info disabled): stock behaviour, never rejected here.
        [Test]
        public void should_accept_when_media_info_is_null_but_the_file_was_not_classified_unparseable()
        {
            _localMovie.MediaInfo = null;
            _localMovie.UnparseableReason = null;

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_media_info_is_present()
        {
            _localMovie.MediaInfo = Builder<MediaInfoModel>.CreateNew().Build();
            _localMovie.UnparseableReason = Reason;

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }
    }
}
