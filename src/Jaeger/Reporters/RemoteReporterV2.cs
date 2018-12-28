using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jaeger.Exceptions;
using Jaeger.Metrics;
using Jaeger.Senders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jaeger.Reporters
{
    /// <summary>
    /// <see cref="RemoteReporterV2"/> buffers spans in memory and sends them out of process using <see cref="ISender"/>.
    /// </summary>
    public class RemoteReporterV2 : IReporter
    {
        public const int DefaultMaxQueueSize = 100;
        public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);

        private readonly Channel<ICommand> _commandQueue;
        private readonly Task _queueProcessorTask;
        private readonly TimeSpan _flushInterval;
        private readonly Task _flushTask;
        private readonly ISender _sender;
        private readonly IMetrics _metrics;
        private readonly ILogger _logger;

        internal RemoteReporterV2(ISender sender, TimeSpan flushInterval, int maxQueueSize,
            IMetrics metrics, ILoggerFactory loggerFactory)
        {
            _sender = sender;
            _metrics = metrics;
            _logger = loggerFactory.CreateLogger<RemoteReporterV2>();

            // 1. Guarantee this channel can only have single reader, but could be with multi writer.
            // 2. AllowSynchronousContinuations need to be false, or a slow read operation may block the write operation which awaked it. 
            if (maxQueueSize < 1) // unlimited queue
            {
                _commandQueue = Channel.CreateUnbounded<ICommand>(new UnboundedChannelOptions
                {
                    SingleWriter = false,
                    SingleReader = true,
                    AllowSynchronousContinuations = false
                });
            }
            else
            {
                _commandQueue = Channel.CreateBounded<ICommand>(new BoundedChannelOptions(maxQueueSize)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleWriter = false,
                    SingleReader = true,
                    AllowSynchronousContinuations = false
                });
            }

            // start a thread to append spans
            // The task returned by Task.Factory.StartNew has an invalid complete state, so we need unwrap it to make it awaitable.
            _queueProcessorTask = Task.Factory.StartNew(ProcessQueueLoop, TaskCreationOptions.LongRunning).Unwrap();

            _flushInterval = flushInterval;
            _flushTask = Task.Factory.StartNew(FlushLoop, TaskCreationOptions.LongRunning).Unwrap();
        }

        public void Report(Span span)
        {
            ChannelWriter<ICommand> writer = _commandQueue.Writer;

            // In drop mode, we can not determine this command has beed enqueued or dropped.
            // https://github.com/dotnet/corefx/blob/master/src/System.Threading.Channels/src/System/Threading/Channels/BoundedChannel.cs#L360
            // A workaround: Count all write attempts, then get dropped command count by (ReporterAll - ReporterAppended)

            var cmd = new AppendCommand(this, span);
            // In drop mode, TryWrite should always return ture.
            writer.TryWrite(cmd);
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            // Note: Java creates a CloseCommand but we have CompleteAdding() in C# so we don't need the command.
            // (This also stops the FlushLoop)
            _commandQueue.Writer.TryComplete();

            try
            {
                // Give processor some time to process any queued commands.
                var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    new CancellationTokenSource(10000).Token);
                var cancellationTask = Task.Delay(Timeout.Infinite, cts.Token);

                await Task.WhenAny(_queueProcessorTask, cancellationTask);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogError(ex, "Dispose interrupted");
            }
            finally
            {
                try
                {
                    int n = await _sender.CloseAsync(cancellationToken).ConfigureAwait(false);
                    _metrics.ReporterSuccess.Inc(n);
                }
                catch (SenderException ex)
                {
                    _metrics.ReporterFailure.Inc(ex.DroppedSpanCount);
                }
            }
        }

        internal bool Flush()
        {
            // We can safely drop FlushCommand when the queue is full - sender should take care of flushing
            // in such case
            return _commandQueue.Writer.TryWrite(new FlushCommand(this));
        }

        private async Task FlushLoop()
        {
            // First flush should happen later so we start with the delay
            do
            {
                await Task.Delay(_flushInterval).ConfigureAwait(false);
            }
            while (Flush()); // Flush() will return false if channel has been closed.
        }

        private async Task ProcessQueueLoop()
        {
            ChannelReader<ICommand> reader = _commandQueue.Reader;

            while (true)
            {
                ValueTask<bool> vt = reader.WaitToReadAsync();
                bool res = vt.IsCompletedSuccessfully ? vt.Result : await vt.ConfigureAwait(false);
                if (!res) // No further command will enqueue, then we can exit the loop.
                {
                    break;
                }

                if (reader.TryRead(out ICommand command))
                {
                    try
                    {
                        await command.ExecuteAsync().ConfigureAwait(false);
                    }
                    catch (SenderException ex)
                    {
                        _metrics.ReporterFailure.Inc(ex.DroppedSpanCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "QueueProcessor error");
                        // Do nothing, and try again on next command.
                    }
                }
            };
        }

        public override string ToString()
        {
            return $"{nameof(RemoteReporterV2)}(Sender={_sender})";
        }

        /*
         * The code below implements the command pattern. This pattern is useful for
         * situations where multiple threads would need to synchronize on a resource,
         * but are fine with executing sequentially. The advantage is simplified code where
         * tasks are put onto a blocking queue and processed sequentially by another thread.
         */
        public interface ICommand
        {
            Task ExecuteAsync();
        }

        private class AppendCommand : ICommand
        {
            private readonly RemoteReporterV2 _reporter;
            private readonly Span _span;

            public AppendCommand(RemoteReporterV2 reporter, Span span)
            {
                _reporter = reporter;
                _span = span;
            }

            public Task ExecuteAsync()
            {
                return _reporter._sender.AppendAsync(_span, CancellationToken.None);
            }
        }

        private class FlushCommand : ICommand
        {
            private readonly RemoteReporterV2 _reporter;

            public FlushCommand(RemoteReporterV2 reporter)
            {
                _reporter = reporter;
            }

            public async Task ExecuteAsync()
            {
                int n = await _reporter._sender.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                _reporter._metrics.ReporterSuccess.Inc(n);
            }
        }

        public sealed class Builder
        {
            private ISender _sender;
            private IMetrics _metrics;
            private ILoggerFactory _loggerFactory;
            private TimeSpan _flushInterval = DefaultFlushInterval;
            private int _maxQueueSize = DefaultMaxQueueSize;

            public Builder WithFlushInterval(TimeSpan flushInterval)
            {
                _flushInterval = flushInterval;
                return this;
            }

            public Builder WithMaxQueueSize(int maxQueueSize)
            {
                _maxQueueSize = maxQueueSize;
                return this;
            }

            public Builder WithMetrics(IMetrics metrics)
            {
                _metrics = metrics;
                return this;
            }

            public Builder WithSender(ISender sender)
            {
                _sender = sender;
                return this;
            }

            public Builder WithLoggerFactory(ILoggerFactory loggerFactory)
            {
                _loggerFactory = loggerFactory;
                return this;
            }

            public RemoteReporterV2 Build()
            {
                if (_loggerFactory == null)
                {
                    _loggerFactory = NullLoggerFactory.Instance;
                }
                if (_sender == null)
                {
                    _sender = new UdpSender();
                }
                if (_metrics == null)
                {
                    _metrics = new MetricsImpl(NoopMetricsFactory.Instance);
                }
                return new RemoteReporterV2(_sender, _flushInterval, _maxQueueSize, _metrics, _loggerFactory);
            }
        }
    }
}