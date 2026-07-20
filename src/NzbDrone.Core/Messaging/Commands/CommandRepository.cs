using System;
using System.Collections.Generic;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Messaging.Commands
{
    public interface ICommandRepository : IBasicRepository<CommandModel>
    {
        void Trim();
        void OrphanStarted();
        List<CommandModel> Queued();
        void Start(CommandModel command);
        void End(CommandModel command);
        void Cancel(int id);
        void CancelQueued(string name = null);
    }

    public class CommandRepository : BasicRepository<CommandModel>, ICommandRepository
    {
        public CommandRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public void Trim()
        {
            var date = DateTime.UtcNow.AddDays(-1);

            Delete(c => c.EndedAt < date);
        }

        public void OrphanStarted()
        {
            var sql = @"UPDATE ""Commands"" SET ""Status"" = @Orphaned, ""EndedAt"" = @Ended WHERE ""Status"" = @Started";
            var args = new
                {
                    Orphaned = (int)CommandStatus.Orphaned,
                    Started = (int)CommandStatus.Started,
                    Ended = DateTime.UtcNow
                };

            using (var conn = _database.OpenConnection())
            {
                conn.Execute(sql, args);
            }
        }

        public List<CommandModel> Queued()
        {
            return Query(x => x.Status == CommandStatus.Queued);
        }

        public void Start(CommandModel command)
        {
            SetFields(command, c => c.StartedAt, c => c.Status);
        }

        public void End(CommandModel command)
        {
            SetFields(command, c => c.EndedAt, c => c.Status, c => c.Duration, c => c.Exception);
        }

        public void Cancel(int id)
        {
            var sql = @"UPDATE ""Commands"" SET ""Status"" = @Cancelled, ""EndedAt"" = @Ended WHERE ""Id"" = @Id AND ""Status"" = @Queued";
            var args = new
                {
                    Cancelled = (int)CommandStatus.Cancelled,
                    Queued = (int)CommandStatus.Queued,
                    Ended = DateTime.UtcNow,
                    Id = id
                };

            using (var conn = _database.OpenConnection())
            {
                conn.Execute(sql, args);
            }
        }

        public void CancelQueued(string name = null)
        {
            var sql = @"UPDATE ""Commands"" SET ""Status"" = @Cancelled, ""EndedAt"" = @Ended WHERE ""Status"" = @Queued";

            if (name != null)
            {
                sql += @" AND ""Name"" = @Name";
            }

            var args = new
                {
                    Cancelled = (int)CommandStatus.Cancelled,
                    Queued = (int)CommandStatus.Queued,
                    Ended = DateTime.UtcNow,
                    Name = name
                };

            using (var conn = _database.OpenConnection())
            {
                conn.Execute(sql, args);
            }
        }
    }
}
