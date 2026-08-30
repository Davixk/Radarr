using System;
using System.Reflection;
using FFMpegCore;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Specifications
{
    // fork23 #1: the configurable Dolby Vision profile/compat exclusion gate. Both lists empty -> no-op.
    [TestFixture]
    public class DolbyVisionSpecificationFixture : CoreTest<DolbyVisionSpecification>
    {
        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", null);
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", null);

            _localMovie = new LocalMovie { MediaInfo = GivenDovi(5, 0) };
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

        [Test]
        public void should_accept_by_default_when_both_lists_are_empty()
        {
            // No env set -> pure no-op, even for Profile 5 (his explicit ruling: opt-in, no default bad values).
            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_a_profile_on_the_profile_reject_list()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");
            _localMovie.MediaInfo = GivenDovi(5, 0);

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_a_profile_not_on_the_profile_reject_list()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");
            _localMovie.MediaInfo = GivenDovi(8, 1);

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_a_compat_id_on_the_compat_reject_list()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", "0");
            _localMovie.MediaInfo = GivenDovi(8, 0);

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_when_media_info_has_no_dolby_vision_record()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");
            _localMovie.MediaInfo = new MediaInfoModel { DoviConfigurationRecord = null };

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_media_info_is_null()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");
            _localMovie.MediaInfo = null;

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }
    }
}
