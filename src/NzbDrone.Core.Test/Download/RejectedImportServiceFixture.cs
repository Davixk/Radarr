using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class RejectedImportServiceFixture : CoreTest<RejectedImportService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                .With(d => d.Title = "Drone.2023.2160p.WEBDL.DV.mp4")
                .With(d => d.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                .With(t => t.State = TrackedDownloadState.ImportPending)
                .With(t => t.DownloadItem = downloadItem)
                .With(t => t.RemoteMovie = new RemoteMovie
                {
                    Release = new ReleaseInfo { IndexerId = 1 }
                })
                .Build();
        }

        private ImportResult RejectedResult(ImportRejectionReason reason, string message)
        {
            var decision = new ImportDecision(new LocalMovie(), new ImportRejection(reason, message));

            return new ImportResult(decision, message);
        }

        [Test]
        public void should_fail_dolby_vision_excluded_for_blocklist_and_research()
        {
            // even when indexer settings do not resolve, a DV exclusion must still blocklist + re-search
            Mocker.GetMock<ICachedIndexerSettingsProvider>()
                  .Setup(s => s.GetSettings(It.IsAny<int>()))
                  .Returns((CachedIndexerSettings)null);

            var message = DolbyVisionSpecification.BlocklistToken + " Dolby Vision profile 5 (compatibility id 0) is excluded by configuration; blocklisted for re-search";

            var handled = Subject.Process(_trackedDownload, RejectedResult(ImportRejectionReason.DolbyVisionExcluded, message));

            handled.Should().BeTrue();
            _trackedDownload.State.Should().Be(TrackedDownloadState.FailedPending);
            _trackedDownload.FailureReason.Should().StartWith(DolbyVisionSpecification.BlocklistToken);
        }

        [Test]
        public void should_warn_and_not_fail_for_other_rejections()
        {
            Mocker.GetMock<ICachedIndexerSettingsProvider>()
                  .Setup(s => s.GetSettings(It.IsAny<int>()))
                  .Returns((CachedIndexerSettings)null);

            var handled = Subject.Process(_trackedDownload, RejectedResult(ImportRejectionReason.Unknown, "some other reason"));

            handled.Should().BeTrue();
            _trackedDownload.State.Should().NotBe(TrackedDownloadState.FailedPending);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Warning);
        }
    }
}
