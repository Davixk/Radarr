using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    [TestFixture]
    public class CommandExecutorReservedLaneFixture : TestBase<CommandExecutor>
    {
        private Mock<IExecute<MoviesSearchCommand>> _searchHandler;
        private Mock<IExecute<ImportSignalCommand>> _importHandler;

        private ManualResetEventSlim _searchGate;
        private ManualResetEventSlim _twoSearchesStarted;
        private ManualResetEventSlim _importExecuted;
        private int _searchesStarted;

        private string _previousThreadLimit;
        private string _previousReserve;

        private CommandQueueManager _commandQueueManager;

        [SetUp]
        public void Setup()
        {
            _searchesStarted = 0;
            _searchGate = new ManualResetEventSlim(false);
            _twoSearchesStarted = new ManualResetEventSlim(false);
            _importExecuted = new ManualResetEventSlim(false);

            // Pin the pool so the executor starts THREAD_LIMIT + 1 = 3 workers and reserves 1 lane
            // for non-search commands (COMMAND_SEARCH_RESERVE = 1 -> maxConcurrentSearch = 2).
            _previousThreadLimit = Environment.GetEnvironmentVariable("THREAD_LIMIT");
            _previousReserve = Environment.GetEnvironmentVariable("COMMAND_SEARCH_RESERVE");
            Environment.SetEnvironmentVariable("THREAD_LIMIT", "2");
            Environment.SetEnvironmentVariable("COMMAND_SEARCH_RESERVE", "1");

            _searchHandler = new Mock<IExecute<MoviesSearchCommand>>();
            _searchHandler.Setup(h => h.Execute(It.IsAny<MoviesSearchCommand>()))
                          .Callback(() =>
                          {
                              if (Interlocked.Increment(ref _searchesStarted) >= 2)
                              {
                                  _twoSearchesStarted.Set();
                              }

                              // Simulate a long / indexer-throttled search that pins its worker.
                              _searchGate.Wait();
                          });

            _importHandler = new Mock<IExecute<ImportSignalCommand>>();
            _importHandler.Setup(h => h.Execute(It.IsAny<ImportSignalCommand>()))
                          .Callback(() => _importExecuted.Set());

            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.Build(typeof(IExecute<MoviesSearchCommand>)))
                  .Returns(_searchHandler.Object);

            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.Build(typeof(IExecute<ImportSignalCommand>)))
                  .Returns(_importHandler.Object);

            // Use the real queue manager so the executor drives the real CommandQueue and its
            // reservation logic (the reservation cap is configured by CommandExecutor.Handle).
            _commandQueueManager = Mocker.Resolve<CommandQueueManager>();
            Mocker.SetConstant<IManageCommandQueue>(_commandQueueManager);
        }

        [TearDown]
        public void TearDown()
        {
            // Release any blocked search workers so the threads can exit.
            _searchGate.Set();

            Subject.Handle(new ApplicationShutdownRequested());

            Thread.Sleep(50);

            Environment.SetEnvironmentVariable("THREAD_LIMIT", _previousThreadLimit);
            Environment.SetEnvironmentVariable("COMMAND_SEARCH_RESERVE", _previousReserve);

            _searchGate.Dispose();
            _twoSearchesStarted.Dispose();
            _importExecuted.Dispose();
        }

        [Test]
        public void should_run_non_search_command_when_all_workers_would_be_filled_by_searches()
        {
            Subject.Handle(new ApplicationStartedEvent());

            // Fill every worker with blocking searches.
            for (var i = 0; i < 3; i++)
            {
                _commandQueueManager.Push(new MoviesSearchCommand { MovieIds = new List<int> { i + 1 } });
            }

            _twoSearchesStarted.Wait(10000).Should().BeTrue("searches should occupy worker threads");

            // Now enqueue a non-search command. With a reserved lane a worker is still free to run it.
            _commandQueueManager.Push(new ImportSignalCommand());

            _importExecuted.Wait(10000)
                           .Should().BeTrue("a worker lane must remain reserved for non-search commands so imports don't starve");
        }
    }

    public class ImportSignalCommand : Command
    {
    }
}
