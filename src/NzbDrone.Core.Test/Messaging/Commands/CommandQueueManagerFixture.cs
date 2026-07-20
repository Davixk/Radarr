using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    [TestFixture]
    public class CommandQueueManagerFixture : CoreTest<CommandQueueManager>
    {
        [SetUp]
        public void Setup()
        {
            var id = 0;
            var commands = new List<CommandModel>();

            Mocker.GetMock<ICommandRepository>()
                  .Setup(s => s.Insert(It.IsAny<CommandModel>()))
                  .Returns<CommandModel>(c =>
                  {
                      c.Id = id + 1;
                      commands.Add(c);
                      id++;

                      return c;
                  });

            Mocker.GetMock<ICommandRepository>()
                  .Setup(s => s.Get(It.IsAny<int>()))
                  .Returns<int>(c =>
                  {
                      return commands.SingleOrDefault(e => e.Id == c);
                  });
        }

        [Test]
        public void should_not_remove_commands_for_five_minutes_after_they_end()
        {
            var command = Subject.Push<RefreshMonitoredDownloadsCommand>(new RefreshMonitoredDownloadsCommand());

            // Start the command to mimic CommandQueue's behaviour
            command.StartedAt = DateTime.Now;
            command.Status = CommandStatus.Started;

            Subject.Start(command);
            Subject.Complete(command, "All done");
            Subject.CleanCommands();

            Subject.Get(command.Id).Should().NotBeNull();

            Mocker.GetMock<ICommandRepository>()
                  .Verify(v => v.Get(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void cancel_should_persist_cancellation_so_command_is_not_resurrected_on_restart()
        {
            var db = new List<CommandModel>();
            var repo = Mocker.GetMock<ICommandRepository>();

            repo.Setup(s => s.Insert(It.IsAny<CommandModel>()))
                .Returns<CommandModel>(c =>
                {
                    c.Id = db.Count + 1;
                    db.Add(c);
                    return c;
                });

            repo.Setup(s => s.Queued())
                .Returns(() => db.Where(c => c.Status == CommandStatus.Queued).ToList());

            repo.Setup(s => s.Cancel(It.IsAny<int>()))
                .Callback<int>(id =>
                {
                    var model = db.FirstOrDefault(c => c.Id == id);

                    if (model != null)
                    {
                        model.Status = CommandStatus.Cancelled;
                    }
                });

            var command = Subject.Push<RefreshMonitoredDownloadsCommand>(new RefreshMonitoredDownloadsCommand());

            Subject.Cancel(command.Id);

            repo.Verify(v => v.Cancel(command.Id), Times.Once());

            // Simulate a restart: OrphanStarted + Requeue from the persisted rows.
            Subject.Handle(new ApplicationStartedEvent());

            Subject.All().Should().NotContain(c => c.Id == command.Id);
        }

        [Test]
        public void cancel_many_should_cancel_all_queued_commands()
        {
            var repo = Mocker.GetMock<ICommandRepository>();

            var first = Subject.Push<RefreshMonitoredDownloadsCommand>(new RefreshMonitoredDownloadsCommand());
            var second = Subject.Push<MessagingCleanupCommand>(new MessagingCleanupCommand());

            var cancelled = Subject.CancelMany();

            cancelled.Should().Contain(first.Id);
            cancelled.Should().Contain(second.Id);

            repo.Verify(v => v.CancelQueued(null), Times.Once());

            Subject.All().Where(c => c.Status == CommandStatus.Queued).Should().BeEmpty();
        }
    }
}
