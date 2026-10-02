using System.IO;
using System.Linq;
using System.Reflection;
using FFMpegCore;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    [TestFixture]
    [DiskAccessTest]
    public class VideoFileInfoReaderFixture : CoreTest<VideoFileInfoReader>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.OpenReadStream(It.IsAny<string>()))
                  .Returns<string>(s => new FileStream(s, FileMode.Open, FileAccess.Read));
        }

        [Test]
        public void get_runtime()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "H264_sample.mp4");

            Subject.GetRunTime(path).Value.Seconds.Should().Be(10);
        }

        [Test]
        public void get_info()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "H264_sample.mp4");

            var info = Subject.GetMediaInfo(path);

            info.VideoFormat.Should().Be("h264");
            info.VideoCodecID.Should().Be("avc1");
            info.VideoProfile.Should().Be("Constrained Baseline");
            info.AudioFormat.Should().Be("aac");
            info.AudioCodecID.Should().Be("mp4a");
            info.AudioProfile.Should().Be("LC");
            info.AudioBitrate.Should().Be(125509);
            info.AudioChannels.Should().Be(2);
            info.AudioChannelPositions.Should().Be("stereo");
            info.AudioLanguages.Should().BeEquivalentTo("eng");
            info.Height.Should().Be(320);
            info.RunTime.Seconds.Should().Be(10);
            info.ScanType.Should().Be("Progressive");
            info.Subtitles.Should().BeEmpty();
            info.VideoBitrate.Should().Be(193694);
            info.VideoFps.Should().Be(24);
            info.Width.Should().Be(480);
            info.VideoBitDepth.Should().Be(8);
            info.VideoColourPrimaries.Should().Be("smpte170m");
            info.VideoTransferCharacteristics.Should().Be("bt709");
            info.Title.Should().Be("Sample Title");
        }

        [Test]
        public void get_info_unicode()
        {
            var srcPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "H264_sample.mp4");

            var tempPath = GetTempFilePath();
            Directory.CreateDirectory(tempPath);

            var path = Path.Combine(tempPath, "H264_Pok\u00E9mon.mkv");

            File.Copy(srcPath, path);

            var info = Subject.GetMediaInfo(path);

            info.VideoFormat.Should().Be("h264");
            info.VideoCodecID.Should().Be("avc1");
            info.VideoProfile.Should().Be("Constrained Baseline");
            info.AudioFormat.Should().Be("aac");
            info.AudioCodecID.Should().Be("mp4a");
            info.AudioProfile.Should().Be("LC");
            info.AudioBitrate.Should().Be(125509);
            info.AudioChannels.Should().Be(2);
            info.AudioChannelPositions.Should().Be("stereo");
            info.AudioLanguages.Should().BeEquivalentTo("eng");
            info.Height.Should().Be(320);
            info.RunTime.Seconds.Should().Be(10);
            info.ScanType.Should().Be("Progressive");
            info.Subtitles.Should().BeEmpty();
            info.VideoBitrate.Should().Be(193694);
            info.VideoFps.Should().Be(24);
            info.Width.Should().Be(480);
            info.VideoColourPrimaries.Should().Be("smpte170m");
            info.VideoTransferCharacteristics.Should().Be("bt709");
            info.Title.Should().Be("Sample Title");
        }

        [TestCase(8, "", "", "", null, HdrFormat.None)]
        [TestCase(10, "", "", "", null, HdrFormat.None)]
        [TestCase(10, "bt709", "bt709", "", null, HdrFormat.None)]
        [TestCase(8, "bt2020", "smpte2084", "", null, HdrFormat.None)]
        [TestCase(10, "bt2020", "bt2020-10", "", null, HdrFormat.None)]
        [TestCase(10, "bt2020", "arib-std-b67", "", null, HdrFormat.Hlg10)]
        [TestCase(10, "bt2020", "smpte2084", "", null, HdrFormat.Pq10)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.SideData", null, HdrFormat.Pq10)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.MasteringDisplayMetadata", null, HdrFormat.Hdr10)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.ContentLightLevelMetadata", null, HdrFormat.Hdr10)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.HdrDynamicMetadataSpmte2094", null, HdrFormat.Hdr10Plus)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.DoviConfigurationRecordSideData", null, HdrFormat.DolbyVision)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.DoviConfigurationRecordSideData", 1, HdrFormat.DolbyVisionHdr10)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.DoviConfigurationRecordSideData,FFMpegCore.HdrDynamicMetadataSpmte2094", 1, HdrFormat.DolbyVisionHdr10Plus)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.DoviConfigurationRecordSideData,FFMpegCore.HdrDynamicMetadataSpmte2094", 6, HdrFormat.DolbyVisionHdr10Plus)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.DoviConfigurationRecordSideData", 2, HdrFormat.DolbyVisionSdr)]
        [TestCase(10, "bt2020", "smpte2084", "FFMpegCore.DoviConfigurationRecordSideData", 4, HdrFormat.DolbyVisionHlg)]
        public void should_detect_hdr_correctly(int bitDepth, string colourPrimaries, string transferFunction, string sideDataTypes, int? doviConfigId, HdrFormat expected)
        {
            var assembly = Assembly.GetAssembly(typeof(FFProbe));
            var types = sideDataTypes.Split(",").Select(x => x.Trim()).ToList();
            var sideData = types.Where(x => x.IsNotNullOrWhiteSpace()).Select(x => assembly.CreateInstance(x)).Cast<SideData>().ToList();

            if (doviConfigId.HasValue)
            {
                sideData.ForEach(x =>
                {
                    if (x.GetType().Name == "DoviConfigurationRecordSideData")
                    {
                        ((DoviConfigurationRecordSideData)x).DvBlSignalCompatibilityId = doviConfigId.Value;
                    }
                });
            }

            var result = VideoFileInfoReader.GetHdrFormat(bitDepth, colourPrimaries, transferFunction, sideData);

            result.Should().Be(expected);
        }

        // fork28: real bundled ffprobe against bytes that are not media (the Fairly OddParents case: a .mkv whose
        // header is random bytes). The probe must be classified UNPARSEABLE with ffprobe's own reason, not just
        // collapse to an anonymous null.
        [Test]
        public void should_classify_random_bytes_as_unparseable()
        {
            var path = GetTempFilePath() + ".mkv";
            var bytes = new byte[4 * 1024 * 1024];
            new System.Random(4396).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);

            Subject.GetMediaInfo(path).Should().BeNull();

            var reason = Subject.TakeUnparseableReason(path);
            reason.Should().Contain("Invalid data found when processing input");
            reason.Should().Contain("EBML header parsing failed");
            reason.Should().NotContain(path, "the reason is a stable string, not a per-file one");

            Subject.TakeUnparseableReason(path).Should().BeNull("the reason is taken once");

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_classify_a_valid_file_as_unparseable()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "H264_sample.mp4");

            Subject.GetMediaInfo(path).Should().NotBeNull();
            Subject.TakeUnparseableReason(path).Should().BeNull();
        }

        // Negative control: a file that cannot be READ (here: absent, past the mocked FileExists) also yields a null
        // MediaInfo, but it is a transport failure, not proof the content is not media, so it must NOT be classified.
        [Test]
        public void should_not_classify_an_unreadable_file_as_unparseable()
        {
            var path = GetTempFilePath() + "-missing.mkv";

            Subject.GetMediaInfo(path).Should().BeNull();
            Subject.TakeUnparseableReason(path).Should().BeNull();

            ExceptionVerification.ExpectedErrors(1);
        }

        // Exact stderr captured from a bundled ffprobe in a home-operations arr base image.
        [TestCase(1, false, "[matroska,webm @ 0x16a0d180] EBML header parsing failed\n/m/garbage.mkv: Invalid data found when processing input\n", "[matroska,webm] EBML header parsing failed; Invalid data found when processing input")]
        [TestCase(1, false, "[mov,mp4,m4a,3gp,3g2,mj2 @ 0x27339180] moov atom not found\n/m/garbage.mp4: Invalid data found when processing input\n", "[mov,mp4,m4a,3gp,3g2,mj2] moov atom not found; Invalid data found when processing input")]
        [TestCase(1, false, "/m/missing.mkv: No such file or directory\n", null)]
        [TestCase(1, false, "/m/stale.mkv: Input/output error\n", null)]
        [TestCase(1, false, "/m/stale.mkv: Transport endpoint is not connected\n", null)]
        [TestCase(-9, true, "/m/garbage.mkv: Invalid data found when processing input\n", null)]
        [TestCase(0, false, "/m/odd.mkv: Invalid data found when processing input\n", null)]
        [TestCase(1, false, "", null)]
        public void should_classify_probe_outcome(int exitCode, bool timedOut, string stderr, string expected)
        {
            var probe = new NzbDrone.Core.MediaFiles.TimeBoundedProcessResult("{\n\n}", stderr, exitCode, timedOut);

            VideoFileInfoReader.GetUnparseableReason(probe).Should().Be(expected);
        }
    }
}
