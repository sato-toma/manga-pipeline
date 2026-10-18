using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MangaPipeline.Core;

namespace MangaPipeline.Api;

public sealed class GenerationJobWorker(
    SqliteExecutionIndex index,
    GraphExecutionService execution,
    ILogger<GenerationJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await index.TryClaimNextJobAsync(stoppingToken);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
                continue;
            }

            try
            {
                var report = await execution.ExecuteAsync(job, stoppingToken);
                await index.CompleteJobAsync(job.Id, report, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Generation job {JobId} failed", job.Id);
                await index.FailJobAsync(job.Id, exception.Message, DateTimeOffset.UtcNow, stoppingToken);
            }
        }
    }
}
