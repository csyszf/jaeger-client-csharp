using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Jaeger;
using Jaeger.Metrics;
using Jaeger.Reporters;
using Jaeger.Senders;
using Microsoft.Extensions.Logging;

namespace Jaeger.Benchmarks
{
    public class RemoteReporterBenchmark
    {
        private const int QueueCount = 1_000_000;
        private const int MaxQueueSize = 0;
        // private MockSender sender;

        private TestUdpSender sender;
        private IReporter reporter;
        public async Task RunReport()
        {
            System.Console.WriteLine("\n\n***********Cold Run*************\n");

            sender = new TestUdpSender("10.0.1.95", 6831, 0);
            reporter = new RemoteReporterV2.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

            sender = new TestUdpSender("10.0.1.95", 6831, 0);
            reporter = new RemoteReporter.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

            System.Console.WriteLine("\n\n***********Warm Run*************\n");

            System.Console.WriteLine("***********V1*************\n");

            sender = new TestUdpSender("10.0.1.95", 6831, 0);
            // sender = new MockSender();
            reporter = new RemoteReporter.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

            sender = new TestUdpSender("10.0.1.95", 6831, 0);
            // sender = new MockSender();
            reporter = new RemoteReporter.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

                        sender = new TestUdpSender("10.0.1.95", 6831, 0);
            // sender = new MockSender();
            reporter = new RemoteReporter.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

            System.Console.WriteLine("***********V2*************\n");

            // sender = new MockSender();
            sender = new TestUdpSender("10.0.1.95", 6831, 0);
            reporter = new RemoteReporterV2.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

            sender = new TestUdpSender("10.0.1.95", 6831, 0);
            reporter = new RemoteReporterV2.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();

                        sender = new TestUdpSender("10.0.1.95", 6831, 0);
            reporter = new RemoteReporterV2.Builder()
                .WithMaxQueueSize(MaxQueueSize)
                .WithSender(sender)
                .Build();
            await RunReportInternal();



        }

        private async Task RunReportInternal()
        {
            Console.WriteLine($"reporter: {reporter.GetType().Name}");

            var sw = Stopwatch.StartNew();

            // for (int i = 0; i < QueueCount; ++i)
            // {
            //     reporter.Report(new MockSpan());
            // }


            var finished = new CountdownEvent(1 + (int)QueueCount);
            for (int i = 0; i < QueueCount; ++i)
            {
                ThreadPool.QueueUserWorkItem((state) =>
                {
                    try
                    {
                        reporter.Report(new MockSpan());
                    }
                    finally
                    {
                        finished.Signal();
                    }
                }, null);
            }

            finished.Signal(); // Signal that queueing is complete.
            finished.Wait();

            Console.WriteLine($"report {QueueCount} spans in {sw.ElapsedMilliseconds} ms");

            await reporter.CloseAsync(default).ConfigureAwait(false);
            sw.Stop();

            Console.WriteLine($"try report {QueueCount} spans");
            Console.WriteLine($"send {sender.Count} spans in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"throughput: {sender.Count / sw.ElapsedMilliseconds}k/s");
        }

    }

    public class MockSender : ISender
    {
        private long cnt = 0;
        public long Count => cnt;
        public Task<int> AppendAsync(Span span, CancellationToken cancellationToken)
        {
            cnt++;
            // Interlocked.Increment(ref cnt);
            return Task.FromResult(0);
        }

        public Task<int> CloseAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }

        public Task<int> FlushAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    public class MockSpan : Jaeger.Span
    {
        private static Tracer tracer = new Tracer.Builder("bencharmkForReporter").Build();
        private static SpanContext context = new SpanContext(new TraceId(), new SpanId(), new SpanId(), SpanContextFlags.None);
        internal MockSpan()
            : base(tracer, "bencharmkForReporter", context, default, new Dictionary<string, object>(), default)
        {
        }
    }

}
