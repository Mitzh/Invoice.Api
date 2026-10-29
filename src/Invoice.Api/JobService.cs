using System.Collections.Concurrent;
using System.Threading.Channels;
using Mitzh.Invoice.Core;

namespace Mitzh.Invoice.Api;

public record WorkItem(Guid Id, string Path, string Extension);
public sealed class JobService : BackgroundService
{
    private readonly InvoiceRecognitionOptions limits;
    private readonly InvoiceRecognizer parser;
    private readonly ILogger<JobService> log;
    private readonly Channel<WorkItem> queue;
    private readonly ConcurrentDictionary<Guid, JobView> jobs = new();
    private readonly Dictionary<string, Guid> invoiceKeys = [];
    private readonly object admission = new();
    public JobService(InvoiceRecognitionOptions limits, InvoiceRecognizer parser, ILogger<JobService> log)
    {
        this.limits = limits;
        this.parser = parser;
        this.log = log;
        queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(limits.QueueCapacity) { FullMode = BoundedChannelFullMode.Wait });
    }

    public JobView Get(Guid id) => jobs.GetValueOrDefault(id);
    public Guid? Submit(string path, string ext)
    {
        lock (admission)
        {
            if (jobs.Count >= limits.MaxJobs)
            {
                return null;
            }

            var id = Guid.NewGuid();
            jobs[id] = new(id, "Queued", DateTimeOffset.UtcNow);
            if (queue.Writer.TryWrite(new(id, path, ext)))
            {
                return id;
            }

            jobs.TryRemove(id, out _);
            return null;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Enumerable.Range(0, limits.Workers).Select(_ => Consume(stoppingToken)));
    private async Task Consume(CancellationToken stop)
    {
        await foreach (WorkItem item in queue.Reader.ReadAllAsync(stop))
        {
            JobView original = jobs[item.Id];
            jobs[item.Id] = original with
            {
                State = "Parsing"
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
            try
            {
                ParseResult result = await parser.RecognizeDetailedAsync(item.Path, timeout.Token);
                InvoiceModel i = result.Invoice;
                string key = i.Number?.Length == 20 ? "digital:" + i.Number : i.Number?.Length == 8 && i.Code != null ? $"traditional:{i.Code}:{i.Number}" : null;
                // Only identifies duplicate extraction results. No reimbursement lock without durable storage.
                if (key != null)
                {
                    lock (admission)
                    {
                        if (invoiceKeys.TryGetValue(key, out Guid prior))
                        {
                            result.Issues.Add("进程内疑似重复发票，关联任务: " + prior);
                        }
                        else
                        {
                            invoiceKeys[key] = item.Id;
                        }
                    }
                }

                jobs[item.Id] = original with
                {
                    State = "NeedsReview",
                    Result = result
                };
            }
            catch (OperationCanceledException ex)
            {
                jobs[item.Id] = original with
                {
                    State = "Failed",
                    Error = ex.Message
                };
            }
            catch (Exception ex)
            {
                // Log exception type only; API returns the exception message requested by the caller.
                log.LogWarning("Job {Id} failed ({Type})", item.Id, ex.GetType().Name);
                string detail = ex.Message;
                jobs[item.Id] = original with
                {
                    State = "Failed",
                    Error = detail
                };
            }
            finally
            {
                Cleanup(item.Path);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        while (queue.Reader.TryRead(out WorkItem item))
        {
            Cleanup(item.Path);
        }
    }

    private void Cleanup(string path)
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(path), true);
        }
        catch (IOException)
        {
            log.LogWarning("Temporary file cleanup failed; operator cleanup required.");
        }
        catch (UnauthorizedAccessException)
        {
            log.LogWarning("Temporary file cleanup denied; operator cleanup required.");
        }
    }
}
