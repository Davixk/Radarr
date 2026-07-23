using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class ImportDecisionMakerParallelFixture : CoreTest<ImportDecisionMaker>
    {
        private Movie _movie;
        private Mock<IImportDecisionEngineSpecification> _pass;

        // Released in TearDown so a probe that is deliberately blocked forever (representing an
        // uninterruptible D-state ffprobe) is unblocked once the test finishes; the worker thread is a
        // background thread so it never keeps the runner alive on its own.
        private ManualResetEventSlim _wedgeLatch;

        [SetUp]
        public void Setup()
        {
            _wedgeLatch = new ManualResetEventSlim(false);

            _pass = new Mock<IImportDecisionEngineSpecification>();
            _pass.Setup(c => c.IsSatisfiedBy(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>()))
                 .Returns(ImportSpecDecision.Accept());

            _movie = Builder<Movie>.CreateNew()
                                   .With(m => m.Path = @"C:\Movies\Test".AsOsAgnostic())
                                   .With(m => m.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                   .Build();

            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>(new[] { _pass.Object });

            Mocker.GetMock<ICustomFormatCalculationService>()
                  .Setup(c => c.ParseCustomFormat(It.IsAny<LocalMovie>()))
                  .Returns(new List<CustomFormat>());
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", null);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", null);

            // Release any probe blocked on the latch so its background worker can unwind and the process
            // does not accumulate wedged threads across tests.
            _wedgeLatch.Set();
        }

        private List<string> GivenVideoFiles(int count)
        {
            var files = Enumerable.Range(0, count)
                                  .Select(i => $@"C:\Downloads\The.Movie.{i}.2019.1080p.BluRay.x264-Radarr\the.movie.{i}.mkv".AsOsAgnostic())
                                  .ToList();

            Mocker.GetMock<IMediaFileService>()
                  .Setup(c => c.FilterExistingFiles(It.IsAny<List<string>>(), It.IsAny<Movie>()))
                  .Returns<List<string>, Movie>((f, m) => f);

            return files;
        }

        private void GivenAugmentBlocksOn(IVideoFileInfoReader reader)
        {
            Mocker.GetMock<IAggregationService>()
                  .Setup(s => s.Augment(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>()))
                  .Callback<LocalMovie, DownloadClientItem>((localMovie, downloadClientItem) =>
                  {
                      localMovie.Movie = _movie;

                      // Represents the ffprobe bound work inside AggregationService.Augment.
                      localMovie.MediaInfo = reader.GetMediaInfo(localMovie.Path);
                  })
                  .Returns<LocalMovie, DownloadClientItem>((localMovie, downloadClientItem) => localMovie);
        }

        [Test]
        public void should_probe_files_concurrently_up_to_configured_degree()
        {
            var files = GivenVideoFiles(4);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            var reader = new ConcurrencyLatchReader(expectedConcurrency: 4, timeout: TimeSpan.FromSeconds(3));
            GivenAugmentBlocksOn(reader);

            var decisions = Subject.GetImportDecisions(files, _movie, null, null, false, false);

            decisions.Should().HaveCount(4);
            reader.PeakConcurrency.Should().Be(4);
        }

        [Test]
        public void should_return_decisions_in_input_order_when_probes_finish_out_of_order()
        {
            var files = GivenVideoFiles(4);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            var reader = new ReverseCompletionReader(files, TimeSpan.FromSeconds(5));
            GivenAugmentBlocksOn(reader);

            var decisions = Subject.GetImportDecisions(files, _movie, null, null, false, false);

            decisions.Select(d => d.LocalMovie.Path).Should().Equal(files);
        }

        [Test]
        public void should_run_serially_and_preserve_order_when_degree_is_one()
        {
            var files = GivenVideoFiles(3);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "1");

            var reader = new ConcurrencyLatchReader(expectedConcurrency: 3, timeout: TimeSpan.FromSeconds(1));
            GivenAugmentBlocksOn(reader);

            var decisions = Subject.GetImportDecisions(files, _movie, null, null, false, false);

            decisions.Should().HaveCount(3);
            decisions.Select(d => d.LocalMovie.Path).Should().Equal(files);
            reader.PeakConcurrency.Should().Be(1);
        }

        [Test]
        public void should_abandon_wedged_probe_and_import_healthy_files_when_timeout_configured()
        {
            var files = GivenVideoFiles(3);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", "1");

            // The middle file's probe never returns (an uninterruptible D-state ffprobe); the others are fast.
            var reader = new WedgeOneReader(files[1], _wedgeLatch);
            GivenAugmentBlocksOn(reader);

            var task = Task.Run(() => Subject.GetImportDecisions(files, _movie, null, null, false, false));

            // The fix: the batch must flow past the wedge rather than hang on it.
            task.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the configured probe timeout must abandon the wedged probe and let the batch complete");

            var decisions = task.Result;

            decisions.Should().HaveCount(3);
            decisions.Select(d => d.LocalMovie.Path).Should().Equal(files, "decisions stay in input order regardless of the abandoned probe");
            decisions[0].Approved.Should().BeTrue();
            decisions[2].Approved.Should().BeTrue();
            decisions[1].Approved.Should().BeFalse("the wedged file is rejected this pass");
            decisions[1].Rejections.Should().ContainSingle(r => r.Reason == ImportRejectionReason.Error && r.Message == "Probe timed out");
        }

        [Test]
        public void should_hang_on_wedged_probe_when_timeout_disabled()
        {
            var files = GivenVideoFiles(3);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", "0");

            var reader = new WedgeOneReader(files[1], _wedgeLatch);
            GivenAugmentBlocksOn(reader);

            var task = Task.Run(() => Subject.GetImportDecisions(files, _movie, null, null, false, false));

            // Documents the default behaviour the timeout knob is off by default: with no probe timeout the
            // never-returning probe joins forever, so the call does not complete within a bounded wait.
            // The blocked worker is a background thread and the latch is released in TearDown, so the
            // runner is not left hanging.
            task.Wait(TimeSpan.FromSeconds(3)).Should().BeFalse("without a probe timeout the wedged probe hangs the whole batch");
        }

        // Releases callers only once expectedConcurrency of them are blocked at the same time,
        // recording the peak observed concurrency. Serial callers never reach the threshold and
        // fall through on the timeout with a peak of 1.
        private sealed class ConcurrencyLatchReader : IVideoFileInfoReader
        {
            private readonly int _expectedConcurrency;
            private readonly TimeSpan _timeout;
            private readonly object _sync = new object();
            private int _current;
            private bool _released;

            public ConcurrencyLatchReader(int expectedConcurrency, TimeSpan timeout)
            {
                _expectedConcurrency = expectedConcurrency;
                _timeout = timeout;
            }

            public int PeakConcurrency { get; private set; }

            public MediaInfoModel GetMediaInfo(string filename)
            {
                lock (_sync)
                {
                    _current++;

                    if (_current > PeakConcurrency)
                    {
                        PeakConcurrency = _current;
                    }

                    if (_current >= _expectedConcurrency)
                    {
                        _released = true;
                        Monitor.PulseAll(_sync);
                    }
                    else
                    {
                        var deadline = DateTime.UtcNow + _timeout;

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

                    _current--;
                }

                return new MediaInfoModel();
            }

            public TimeSpan? GetRunTime(string filename)
            {
                return TimeSpan.FromMinutes(120);
            }
        }

        // Forces the probes to complete in reverse input order so that a naive implementation that
        // returns results in completion order would scramble the decisions. Requires the probes to
        // run concurrently, which is guaranteed by the parallel decision phase.
        private sealed class ReverseCompletionReader : IVideoFileInfoReader
        {
            private readonly IReadOnlyList<string> _orderedPaths;
            private readonly TimeSpan _timeout;
            private readonly object _sync = new object();
            private int _completedCount;

            public ReverseCompletionReader(IReadOnlyList<string> orderedPaths, TimeSpan timeout)
            {
                _orderedPaths = orderedPaths;
                _timeout = timeout;
            }

            public MediaInfoModel GetMediaInfo(string filename)
            {
                var index = -1;

                for (var i = 0; i < _orderedPaths.Count; i++)
                {
                    if (string.Equals(_orderedPaths[i], filename, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                var mustCompleteFirst = index >= 0 ? _orderedPaths.Count - 1 - index : 0;

                lock (_sync)
                {
                    var deadline = DateTime.UtcNow + _timeout;

                    while (_completedCount < mustCompleteFirst)
                    {
                        var remaining = deadline - DateTime.UtcNow;

                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        Monitor.Wait(_sync, remaining);
                    }

                    _completedCount++;
                    Monitor.PulseAll(_sync);
                }

                return new MediaInfoModel();
            }

            public TimeSpan? GetRunTime(string filename)
            {
                return TimeSpan.FromMinutes(120);
            }
        }

        // Blocks one specific file's probe until the latch is released, representing an ffprobe stuck in
        // uninterruptible D-state that never returns and cannot be killed. Every other file probes
        // instantly. The latch is only released in TearDown, so within a test the wedged probe truly
        // never completes.
        private sealed class WedgeOneReader : IVideoFileInfoReader
        {
            private readonly string _wedgePath;
            private readonly ManualResetEventSlim _latch;

            public WedgeOneReader(string wedgePath, ManualResetEventSlim latch)
            {
                _wedgePath = wedgePath;
                _latch = latch;
            }

            public MediaInfoModel GetMediaInfo(string filename)
            {
                if (string.Equals(filename, _wedgePath, StringComparison.Ordinal))
                {
                    _latch.Wait();
                }

                return new MediaInfoModel();
            }

            public TimeSpan? GetRunTime(string filename)
            {
                return TimeSpan.FromMinutes(120);
            }
        }
    }
}
