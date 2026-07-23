using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadProcessingServiceFixture : CoreTest<DownloadProcessingService>
    {
        private FakeCompletedDownloadService _completedDownloadService;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(true);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", null);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", null);

            _completedDownloadService?.Release();
        }

        private List<TrackedDownload> GivenPendingDownloads(int count)
        {
            var trackedDownloads = Enumerable.Range(0, count)
                .Select(i => new TrackedDownload
                {
                    IsTrackable = true,
                    State = TrackedDownloadState.ImportPending,
                    DownloadItem = new DownloadClientItem { Title = $"download-{i}" }
                })
                .ToList();

            Mocker.GetMock<ITrackedDownloadService>()
                  .Setup(s => s.GetTrackedDownloads())
                  .Returns(trackedDownloads);

            return trackedDownloads;
        }

        private void GivenCompletedDownloadService(FakeCompletedDownloadService fake)
        {
            _completedDownloadService = fake;
            Mocker.SetConstant<ICompletedDownloadService>(fake);
        }

        [Test]
        public void should_probe_pending_downloads_concurrently_across_downloads()
        {
            // The completed-download backlog is dominated by single-file downloads, so before the fix the
            // outer foreach(trackedDownload) imported them one at a time and only one ffprobe was ever in
            // flight (peak concurrency 1). The probe/decide phase must now fan out ACROSS downloads.
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            GivenPendingDownloads(4);
            GivenCompletedDownloadService(new FakeCompletedDownloadService(expectedConcurrency: 4, gateTimeout: TimeSpan.FromSeconds(5)));

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            _completedDownloadService.PeakProbeConcurrency.Should().Be(4);
            _completedDownloadService.Committed.Should().HaveCount(4);
        }

        [Test]
        public void should_run_serially_when_degree_is_one()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "1");

            GivenPendingDownloads(3);
            GivenCompletedDownloadService(new FakeCompletedDownloadService(expectedConcurrency: 3, gateTimeout: TimeSpan.FromSeconds(1)));

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            _completedDownloadService.PeakProbeConcurrency.Should().Be(1);
            _completedDownloadService.Committed.Should().HaveCount(3);
        }

        [Test]
        public void should_commit_in_original_download_order()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            GivenPendingDownloads(4);
            GivenCompletedDownloadService(new FakeCompletedDownloadService(expectedConcurrency: 4, gateTimeout: TimeSpan.FromSeconds(5)));

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            _completedDownloadService.Committed.Should().Equal("download-0", "download-1", "download-2", "download-3");
        }

        [Test]
        public void should_abandon_wedged_probe_and_import_other_downloads()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", "1");

            GivenPendingDownloads(3);
            GivenCompletedDownloadService(new FakeCompletedDownloadService(expectedConcurrency: 1, gateTimeout: TimeSpan.Zero)
            {
                WedgeTitle = "download-1"
            });

            var task = Task.Run(() => Subject.Execute(new ProcessMonitoredDownloadsCommand()));

            task.Wait(TimeSpan.FromSeconds(15))
                .Should().BeTrue("the configured probe timeout must abandon the wedged probe so the batch completes");

            _completedDownloadService.Committed.Should().BeEquivalentTo(new[] { "download-0", "download-2" });
            _completedDownloadService.Committed.Should().NotContain("download-1");
        }

        // Hand-written ICompletedDownloadService test double. ProbeImport (the read-only DECIDE phase that
        // stands in for the ffprobe/media-info work) records peak concurrency via a latch, so a serial
        // coordinator is observed as peak 1 and a fan-out is observed as peak == degree. One download's
        // probe can be wedged (never returns) to model an ffprobe stuck in uninterruptible D-state.
        private sealed class FakeCompletedDownloadService : ICompletedDownloadService
        {
            private readonly int _expectedConcurrency;
            private readonly TimeSpan _gateTimeout;
            private readonly ManualResetEventSlim _wedgeLatch = new ManualResetEventSlim(false);
            private readonly object _sync = new object();
            private int _current;
            private bool _released;

            public FakeCompletedDownloadService(int expectedConcurrency, TimeSpan gateTimeout)
            {
                _expectedConcurrency = expectedConcurrency;
                _gateTimeout = gateTimeout;
            }

            public string WedgeTitle { get; set; }

            public int PeakProbeConcurrency { get; private set; }

            public List<string> Committed { get; } = new List<string>();

            public void Release()
            {
                _wedgeLatch.Set();
            }

            public PendingImport PrepareImport(TrackedDownload trackedDownload)
            {
                return new PendingImport(trackedDownload, PendingImportStatus.ReadyToProbe, "output");
            }

            public void ProbeImport(PendingImport pendingImport)
            {
                var title = pendingImport.TrackedDownload.DownloadItem.Title;

                if (title == WedgeTitle)
                {
                    // Never returns within the test: models an ffprobe stuck in D-state. The dispatcher
                    // must abandon it on IMPORT_PROBE_TIMEOUT rather than join it, so Batch stays null.
                    _wedgeLatch.Wait();
                    return;
                }

                lock (_sync)
                {
                    _current++;

                    if (_current > PeakProbeConcurrency)
                    {
                        PeakProbeConcurrency = _current;
                    }

                    if (_expectedConcurrency > 1)
                    {
                        if (_current >= _expectedConcurrency)
                        {
                            _released = true;
                            Monitor.PulseAll(_sync);
                        }
                        else
                        {
                            var deadline = DateTime.UtcNow + _gateTimeout;

                            while (!_released)
                            {
                                var remaining = deadline - DateTime.UtcNow;

                                if (remaining <= TimeSpan.Zero)
                                {
                                    break;
                                }

                                Monitor.Wait(_sync, remaining);
                            }
                        }
                    }

                    _current--;
                }

                pendingImport.Batch = new DownloadedMovieImportBatch();
            }

            public void CompleteImport(PendingImport pendingImport)
            {
                // A wedged/abandoned probe leaves Batch null; the real service skips it too.
                if (pendingImport.Batch == null)
                {
                    return;
                }

                lock (_sync)
                {
                    Committed.Add(pendingImport.TrackedDownload.DownloadItem.Title);
                }
            }

            public void Check(TrackedDownload trackedDownload)
            {
            }

            public void Import(TrackedDownload trackedDownload)
            {
                var pendingImport = PrepareImport(trackedDownload);
                ProbeImport(pendingImport);
                CompleteImport(pendingImport);
            }

            public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
            {
                return false;
            }
        }
    }
}
