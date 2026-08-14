using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;

namespace NzbDrone.Core.Messaging.Commands
{
    public class CommandExecutor : IHandle<ApplicationStartedEvent>,
                                   IHandle<ApplicationShutdownRequested>
    {
        private const int THREAD_UPPER_BOUND = 10;
        private const int THREAD_LOWER_BOUND = 2;
        private const int THREAD_LIMIT = 2;
        private const int DEFAULT_SEARCH_RESERVE = 1;

        private readonly Logger _logger;
        private readonly IServiceFactory _serviceFactory;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigService _configService;

        private static CancellationTokenSource _cancellationTokenSource;

        public CommandExecutor(IServiceFactory serviceFactory,
                               IManageCommandQueue commandQueueManager,
                               IEventAggregator eventAggregator,
                               IConfigService configService,
                               Logger logger)
        {
            _logger = logger;
            _serviceFactory = serviceFactory;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _configService = configService;
        }

        private void ExecuteCommands()
        {
            try
            {
                foreach (var command in _commandQueueManager.Queue(_cancellationTokenSource.Token))
                {
                    try
                    {
                        ExecuteCommand((dynamic)command.Body, command);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error occurred while executing task {0}", command.Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Trace("Stopped one command execution pipeline");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unknown error in thread");
            }
        }

        private void ExecuteCommand<TCommand>(TCommand command, CommandModel commandModel)
            where TCommand : Command
        {
            IExecute<TCommand> handler = null;

            try
            {
                handler = (IExecute<TCommand>)_serviceFactory.Build(typeof(IExecute<TCommand>));

                _logger.Trace("{0} -> {1}", command.GetType().Name, handler.GetType().Name);

                _commandQueueManager.Start(commandModel);
                BroadcastCommandUpdate(commandModel);

                if (ProgressMessageContext.CommandModel == null)
                {
                    ProgressMessageContext.CommandModel = commandModel;
                }

                ExecuteWithTimeout(handler, command, commandModel);

                _commandQueueManager.Complete(commandModel, command.CompletionMessage ?? commandModel.Message);
            }
            catch (CommandFailedException ex)
            {
                _commandQueueManager.SetMessage(commandModel, "Failed");
                _commandQueueManager.Fail(commandModel, ex.Message, ex);
                throw;
            }
            catch (Exception ex)
            {
                _commandQueueManager.SetMessage(commandModel, "Failed");
                _commandQueueManager.Fail(commandModel, "Failed", ex);
                throw;
            }
            finally
            {
                BroadcastCommandUpdate(commandModel);

                _eventAggregator.PublishEvent(new CommandExecutedEvent(commandModel));

                if (ProgressMessageContext.CommandModel == commandModel)
                {
                    ProgressMessageContext.CommandModel = null;
                }

                if (handler != null)
                {
                    _logger.Trace("{0} <- {1} [{2}]", command.GetType().Name, handler.GetType().Name, commandModel.Duration.ToString());
                }
            }
        }

        private void ExecuteWithTimeout<TCommand>(IExecute<TCommand> handler, TCommand command, CommandModel commandModel)
            where TCommand : Command
        {
            // fork18: optional command-execution reaper. Stock runs the handler synchronously on the worker
            // thread with no timeout, so a hung command (a search whose indexer/decypharr call never returns -
            // the observed 2-day zombie) pins that worker forever, starving the imports and failed-download
            // processing that share the command queue. When CommandTimeout (minutes, UI-settable) is > 0 the
            // handler runs on a task and the worker abandons it if it overruns: the command is failed and the
            // worker is freed. The abandoned handler leaks its thread (a managed thread blocked in native IO
            // cannot be force-aborted) but the queue recovers. Gated on > 0 so the default is the exact stock
            // synchronous call. Progress context flows across the task via ProgressMessageContext's AsyncLocal.
            var timeout = GetCommandTimeout();

            if (timeout == null)
            {
                handler.Execute(command);
                return;
            }

            var task = Task.Run(() => handler.Execute(command));

            bool completed;

            try
            {
                completed = task.Wait(timeout.Value);
            }
            catch (AggregateException ex)
            {
                // The handler threw within the timeout - surface its own exception exactly as the synchronous
                // path would have, so the existing catch blocks fail the command normally.
                ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                return;
            }

            if (!completed)
            {
                // Observe the eventual fault so an abandoned handler cannot raise an UnobservedTaskException.
                task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

                throw new CommandFailedException($"Command '{commandModel.Name}' exceeded its {timeout.Value.TotalMinutes:0} minute execution timeout and was abandoned to free the worker");
            }
        }

        // Seam so tests can drive a sub-second timeout; production reads CommandTimeout (minutes, 0 = disabled).
        protected virtual TimeSpan? GetCommandTimeout()
        {
            var minutes = _configService.CommandTimeout;

            return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        }

        private void BroadcastCommandUpdate(CommandModel command)
        {
            if (command.Body.SendUpdatesToClient)
            {
                _eventAggregator.PublishEvent(new CommandUpdatedEvent(command));
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _cancellationTokenSource = new CancellationTokenSource();

            var envLimit = Environment.GetEnvironmentVariable("THREAD_LIMIT") ?? $"{THREAD_LIMIT}";
            var threadLimit = THREAD_LIMIT;
            if (int.TryParse(envLimit, out var parsedLimit))
            {
                threadLimit = parsedLimit;
            }

            threadLimit = Math.Max(THREAD_LOWER_BOUND, threadLimit);
            threadLimit = Math.Min(THREAD_UPPER_BOUND, threadLimit);

            var workerCount = threadLimit + 1;

            var envReserve = Environment.GetEnvironmentVariable("COMMAND_SEARCH_RESERVE") ?? $"{DEFAULT_SEARCH_RESERVE}";
            var reserve = DEFAULT_SEARCH_RESERVE;
            if (int.TryParse(envReserve, out var parsedReserve))
            {
                reserve = parsedReserve;
            }

            reserve = Math.Max(0, Math.Min(workerCount - 1, reserve));

            var maxConcurrentSearch = Math.Max(1, workerCount - reserve);
            _commandQueueManager.SetMaxConcurrentSearch(maxConcurrentSearch);

            _logger.Info("Starting {} threads for tasks.", threadLimit);
            _logger.Info("Reserving {0} worker(s) for non-search commands, max concurrent searches: {1}.", reserve, maxConcurrentSearch);

            for (var i = 0; i < workerCount; i++)
            {
                var thread = new Thread(ExecuteCommands);
                thread.Start();
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _logger.Info("Shutting down task execution");
            _cancellationTokenSource.Cancel(true);
        }
    }
}
