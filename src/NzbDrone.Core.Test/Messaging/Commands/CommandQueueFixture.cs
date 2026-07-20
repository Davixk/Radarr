using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    [TestFixture]
    public class CommandQueueFixture : CoreTest<CommandQueue>
    {
        private void GivenStartedDiskCommand()
        {
            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ProcessMonitoredDownloads")
                .With(c => c.Body = new ProcessMonitoredDownloadsCommand())
                .With(c => c.Status = CommandStatus.Started)
                .Build();

            Subject.Add(commandModel);
        }

        private void GivenStartedTypeExclusiveCommand()
        {
            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ImportListSync")
                .With(c => c.Body = new ImportListSyncCommand())
                .With(c => c.Status = CommandStatus.Started)
                .Build();

            Subject.Add(commandModel);
        }

        private void GivenStartedExclusiveCommand()
        {
            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ApplicationUpdate")
                .With(c => c.Body = new ApplicationUpdateCommand())
                .With(c => c.Status = CommandStatus.Started)
                .Build();

            Subject.Add(commandModel);
        }

        [Test]
        public void should_not_return_disk_access_command_if_another_running()
        {
            GivenStartedDiskCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ProcessMonitoredDownloads")
                .With(c => c.Body = new ProcessMonitoredDownloadsCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_not_return_type_exclusive_command_if_another_and_disk_access_command_running()
        {
            GivenStartedTypeExclusiveCommand();
            GivenStartedDiskCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ImportListSync")
                .With(c => c.Body = new ImportListSyncCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_not_return_type_exclusive_command_if_another_running()
        {
            GivenStartedTypeExclusiveCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ImportListSync")
                .With(c => c.Body = new ImportListSyncCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_return_type_exclusive_command_if_another_not_running()
        {
            GivenStartedDiskCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ImportListSync")
                .With(c => c.Body = new ImportListSyncCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().NotBeNull();
            command.Status.Should().Be(CommandStatus.Started);
        }

        [Test]
        public void should_return_regular_command_if_type_exclusive_command_running()
        {
            GivenStartedTypeExclusiveCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "RefreshMovie")
                .With(c => c.Body = new RefreshMovieCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().NotBeNull();
            command.Status.Should().Be(CommandStatus.Started);
        }

        [Test]
        public void should_not_return_exclusive_command_if_any_running()
        {
            GivenStartedDiskCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "ApplicationUpdate")
                .With(c => c.Body = new ApplicationUpdateCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_not_return_any_command_if_exclusive_running()
        {
            GivenStartedExclusiveCommand();

            var newCommandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "RefreshMovie")
                .With(c => c.Body = new RefreshMovieCommand())
                .Build();

            Subject.Add(newCommandModel);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_return_null_if_nothing_queued()
        {
            GivenStartedDiskCommand();

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        private void GivenQueuedSearchCommand(int movieId)
        {
            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "MoviesSearch")
                .With(c => c.Body = new MoviesSearchCommand { MovieIds = new List<int> { movieId } })
                .With(c => c.Status = CommandStatus.Queued)
                .Build();

            Subject.Add(commandModel);
        }

        [Test]
        public void should_not_hand_out_search_command_once_concurrent_search_cap_is_reached()
        {
            Subject.SetMaxConcurrentSearch(2);

            GivenQueuedSearchCommand(1);
            GivenQueuedSearchCommand(2);
            GivenQueuedSearchCommand(3);

            Subject.TryGet(out var first).Should().BeTrue();
            first.Body.Should().BeOfType<MoviesSearchCommand>();

            Subject.TryGet(out var second).Should().BeTrue();
            second.Body.Should().BeOfType<MoviesSearchCommand>();

            // The cap is reached; the reserved lane idles rather than starting a third search.
            Subject.TryGet(out var third).Should().BeFalse();
            third.Should().BeNull();
        }

        [Test]
        public void should_hand_out_non_search_command_while_search_cap_is_reached()
        {
            Subject.SetMaxConcurrentSearch(2);

            GivenQueuedSearchCommand(1);
            GivenQueuedSearchCommand(2);

            Subject.TryGet(out _);
            Subject.TryGet(out _);

            var nonSearchCommand = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "RefreshMovie")
                .With(c => c.Body = new RefreshMovieCommand())
                .With(c => c.Status = CommandStatus.Queued)
                .Build();

            Subject.Add(nonSearchCommand);

            Subject.TryGet(out var command).Should().BeTrue();
            command.Should().NotBeNull();
            command.Body.Should().BeOfType<RefreshMovieCommand>();
        }
    }
}
