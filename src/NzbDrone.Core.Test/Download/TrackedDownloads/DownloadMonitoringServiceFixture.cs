using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class DownloadMonitoringServiceFixture : CoreTest<DownloadMonitoringService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(true);
        }

        private void GivenTracked(params TrackedDownloadState[] states)
        {
            var tracked = states
                .Select((state, i) => new TrackedDownload
                {
                    IsTrackable = true,
                    State = state,
                    DownloadItem = new DownloadClientItem { Title = $"download-{i}", Status = DownloadItemStatus.Failed }
                })
                .ToList();

            Mocker.GetMock<ITrackedDownloadService>()
                  .Setup(s => s.GetTrackedDownloads())
                  .Returns(tracked);
        }

        private List<TrackedDownload> WhenRefreshed()
        {
            List<TrackedDownload> published = null;

            Mocker.GetMock<IEventAggregator>()
                  .Setup(s => s.PublishEvent(It.IsAny<TrackedDownloadRefreshedEvent>()))
                  .Callback<TrackedDownloadRefreshedEvent>(e => published = e.TrackedDownloads);

            Subject.Handle(new DownloadsProcessedEvent());

            return published;
        }

        [Test]
        public void should_keep_failed_downloads_visible_while_still_in_client()
        {
            // fork17 regression fix: a tracked-Failed item still served by the client must stay trackable so it
            // remains visible (red) in the queue until it is actually removed. Stock dropped Failed here, which
            // hid the whole tracked-Failed pile from the queue (the reported regression: ~800 rows, status=failed -> 0).
            GivenTracked(TrackedDownloadState.Failed);

            WhenRefreshed().Should().HaveCount(1);
        }

        [Test]
        public void should_still_drop_imported_and_ignored_downloads()
        {
            GivenTracked(TrackedDownloadState.Imported, TrackedDownloadState.Ignored);

            WhenRefreshed().Should().BeEmpty();
        }
    }
}
