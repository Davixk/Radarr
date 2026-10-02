using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Aggregation
{
    // fork28: Augment is where every import path (automatic, manual, disk scan) probes the file, so it is where the
    // unparseable classification must be captured onto the LocalMovie, right after that same probe.
    [TestFixture]
    public class AggregationServiceFixture : CoreTest
    {
        private const string Reason = "[matroska,webm] EBML header parsing failed; Invalid data found when processing input";

        private Mock<IVideoFileInfoReader> _reader;
        private AggregationService _subject;
        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            _reader = new Mock<IVideoFileInfoReader>();

            _subject = new AggregationService(new List<IAggregateLocalMovie>(),
                                              new Mock<IDiskProvider>().Object,
                                              _reader.Object,
                                              new Mock<IConfigService>().Object,
                                              LogManager.GetLogger("AggregationServiceFixture"));

            _localMovie = new LocalMovie
                          {
                              Path = @"C:\Test\Unsorted\Movie Title\movie.title.2000.mkv".AsOsAgnostic(),
                              FileMovieInfo = new ParsedMovieInfo(),
                              Movie = Builder<Movie>.CreateNew().Build()
                          };
        }

        [Test]
        public void should_capture_the_unparseable_reason_when_the_probe_returns_null()
        {
            _reader.Setup(r => r.GetMediaInfo(_localMovie.Path)).Returns((MediaInfoModel)null);
            _reader.Setup(r => r.TakeUnparseableReason(_localMovie.Path)).Returns(Reason);

            _subject.Augment(_localMovie, null).UnparseableReason.Should().Be(Reason);
        }

        [Test]
        public void should_not_carry_a_reason_when_the_probe_succeeds()
        {
            _reader.Setup(r => r.GetMediaInfo(_localMovie.Path)).Returns(new MediaInfoModel());
            _reader.Setup(r => r.TakeUnparseableReason(_localMovie.Path)).Returns(Reason);

            _subject.Augment(_localMovie, null).UnparseableReason.Should().BeNull();
        }
    }
}
