using System;
using System.Collections.Generic;
using System.Reflection;
using FFMpegCore;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class DolbyVisionEnforcementServiceFixture : CoreTest<DolbyVisionEnforcementService>
    {
        private Movie _movie;
        private MovieFile _movieFile;

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", null);

            _movie = Builder<Movie>.CreateNew().Build();
            _movieFile = Builder<MovieFile>.CreateNew()
                .With(f => f.MediaInfo = GivenDovi(5, 0))
                .Build();
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", null);
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", null);
        }

        private MediaInfoModel GivenDovi(int profile, int compatId)
        {
            var dovi = (DoviConfigurationRecordSideData)Assembly.GetAssembly(typeof(FFProbe)).CreateInstance("FFMpegCore.DoviConfigurationRecordSideData");
            dovi.DvProfile = profile;
            dovi.DvBlSignalCompatibilityId = compatId;

            return new MediaInfoModel { DoviConfigurationRecord = dovi };
        }

        private void GivenGrabHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.GetByMovieId(_movie.Id, MovieHistoryEventType.Grabbed))
                .Returns(Builder<MovieHistory>.CreateListOfSize(1).BuildList());
        }

        [Test]
        public void should_delete_blocklist_and_research_an_excluded_file()
        {
            GivenGrabHistory();

            Subject.EnforceOnLibraryFile(_movie, _movieFile).Should().BeTrue();

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteMovieFile(_movie, _movieFile), Times.Once());
            Mocker.GetMock<IFailedDownloadService>()
                .Verify(v => v.MarkAsFailed(It.IsAny<int>(), It.Is<string>(m => m.StartsWith(DolbyVisionSpecification.BlocklistToken)), false), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_do_nothing_for_a_non_excluded_file()
        {
            _movieFile.MediaInfo = GivenDovi(8, 1);

            Subject.EnforceOnLibraryFile(_movie, _movieFile).Should().BeFalse();

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never());
            Mocker.GetMock<IFailedDownloadService>().Verify(v => v.MarkAsFailed(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_delete_but_not_blocklist_when_no_grab_history()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.GetByMovieId(_movie.Id, MovieHistoryEventType.Grabbed))
                .Returns(new List<MovieHistory>());

            Subject.EnforceOnLibraryFile(_movie, _movieFile).Should().BeTrue();

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteMovieFile(_movie, _movieFile), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(v => v.MarkAsFailed(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
