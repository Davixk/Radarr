using System;
using System.Threading;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    // fork18: proves the command-execution reaper. A handler that overruns the timeout must be abandoned
    // (command Failed, worker freed) rather than pinning its worker forever; a handler that finishes within
    // the timeout must still Complete normally through the offloaded path.
    [TestFixture]
    public class CommandExecutorTimeoutFixture : TestBase<TestableCommandExecutor>
    {
        private CommandQueue _commandQueue;
        private Mock<IExecute<CommandA>> _executorA;

        [SetUp]
        public void Setup()
        {
            _executorA = new Mock<IExecute<CommandA>>();

            Mocker.GetMock<IServiceFactory>()
                  .Setup(c => c.Build(typeof(IExecute<CommandA>)))
                  .Returns(_executorA.Object);

            _commandQueue = new CommandQueue();

            Mocker.GetMock<IManageCommandQueue>()
                  .Setup(s => s.Queue(It.IsAny<CancellationToken>()))
                  .Returns(_commandQueue.GetConsumingEnumerable);
        }

        [TearDown]
        public void TearDown()
        {
            Subject.Handle(new ApplicationShutdownRequested());
            Thread.Sleep(10);
        }

        private void QueueAndWait(CommandModel commandModel, ManualResetEventSlim done)
        {
            _commandQueue.Add(commandModel);

            if (!done.Wait(15000))
            {
                Assert.Fail("Command did not Complete/Fail within 15 sec");
            }
        }

        [Test]
        public void should_fail_and_free_the_worker_when_a_command_overruns_the_timeout()
        {
            var done = new ManualResetEventSlim();

            Mocker.GetMock<IManageCommandQueue>()
                  .Setup(s => s.Fail(It.IsAny<CommandModel>(), It.IsAny<string>(), It.IsAny<Exception>()))
                  .Callback(() => done.Set());

            // Handler blocks far longer than the 1s test timeout -> must be reaped (Failed), not Completed.
            _executorA.Setup(s => s.Execute(It.IsAny<CommandA>()))
                      .Callback(() => Thread.Sleep(10000));

            var commandModel = new CommandModel { Body = new CommandA() };

            Subject.Handle(new ApplicationStartedEvent());
            QueueAndWait(commandModel, done);

            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Fail(It.Is<CommandModel>(c => c == commandModel), It.IsAny<string>(), It.IsAny<Exception>()), Times.Once());

            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Complete(It.IsAny<CommandModel>(), It.IsAny<string>()), Times.Never());

            ExceptionVerification.WaitForErrors(1, 1000);
        }

        [Test]
        public void should_complete_normally_when_a_command_finishes_within_the_timeout()
        {
            var done = new ManualResetEventSlim();

            Mocker.GetMock<IManageCommandQueue>()
                  .Setup(s => s.Complete(It.IsAny<CommandModel>(), It.IsAny<string>()))
                  .Callback(() => done.Set());

            // No delay -> completes well within the 1s timeout through the offloaded path.
            var commandModel = new CommandModel { Body = new CommandA() };

            Subject.Handle(new ApplicationStartedEvent());
            QueueAndWait(commandModel, done);

            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Complete(It.Is<CommandModel>(c => c == commandModel), It.IsAny<string>()), Times.Once());

            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Fail(It.IsAny<CommandModel>(), It.IsAny<string>(), It.IsAny<Exception>()), Times.Never());
        }
    }

    // Drives a sub-second timeout so the reaper can be exercised without a real-minute wait.
    public class TestableCommandExecutor : CommandExecutor
    {
        public TestableCommandExecutor(IServiceFactory serviceFactory,
                                       IManageCommandQueue commandQueueManager,
                                       IEventAggregator eventAggregator,
                                       IConfigService configService,
                                       Logger logger)
            : base(serviceFactory, commandQueueManager, eventAggregator, configService, logger)
        {
        }

        protected override TimeSpan? GetCommandTimeout()
        {
            return TimeSpan.FromMilliseconds(1000);
        }
    }
}
