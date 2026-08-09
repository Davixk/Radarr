using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    // fork15: DownloadEventHub sends deleteData to RemoveItem. Stock hardcoded true; fork15 makes COMPLETED-download
    // removals honour the qBittorrent per-client "Delete data when removing completed downloads" flag (surfaced onto
    // DownloadClientItem.DeleteDataOnCompletedRemoval), while FAILED removals always keep true.
    [TestFixture]
    public class DownloadEventHubFixture : CoreTest<DownloadEventHub>
    {
        private TrackedDownload _trackedDownload;
        private Mock<IDownloadClient> _downloadClient;

        [SetUp]
        public void Setup()
        {
            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                .With(t => t.DownloadClient = 1)
                .With(t => t.DownloadItem = new DownloadClientItem
                {
                    DownloadId = "abc",
                    Title = "Test.Download",
                    Status = DownloadItemStatus.Completed,
                    CanBeRemoved = true,
                    Removed = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qbit" }
                })
                .Build();

            _downloadClient = Mocker.GetMock<IDownloadClient>();
            _downloadClient.SetupGet(c => c.Definition)
                .Returns(new DownloadClientDefinition { RemoveCompletedDownloads = true, RemoveFailedDownloads = true });

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(p => p.Get(It.IsAny<int>()))
                .Returns(_downloadClient.Object);
        }

        [Test]
        public void should_delete_data_on_completed_removal_by_default()
        {
            Subject.Handle(new DownloadCompletedEvent(_trackedDownload, 0));

            _downloadClient.Verify(c => c.RemoveItem(_trackedDownload.DownloadItem, true), Times.Once());
        }

        [Test]
        public void should_send_delete_data_false_on_completed_removal_when_the_client_flag_is_off()
        {
            _trackedDownload.DownloadItem.DeleteDataOnCompletedRemoval = false;

            Subject.Handle(new DownloadCompletedEvent(_trackedDownload, 0));

            _downloadClient.Verify(c => c.RemoveItem(_trackedDownload.DownloadItem, false), Times.Once());
        }

        [Test]
        public void should_always_delete_data_on_failed_removal_even_when_the_completed_flag_is_off()
        {
            _trackedDownload.DownloadItem.DeleteDataOnCompletedRemoval = false;

            Subject.Handle(new DownloadFailedEvent { TrackedDownload = _trackedDownload });

            _downloadClient.Verify(c => c.RemoveItem(_trackedDownload.DownloadItem, true), Times.Once());
        }
    }
}
