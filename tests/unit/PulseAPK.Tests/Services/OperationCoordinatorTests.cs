using System.Text.Json;
using PulseAPK.Core.Services;

namespace PulseAPK.Tests.Services;

public class OperationCoordinatorTests
{
    [Fact]
    public void NullJournalEntriesDoNotPreventStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), "senda-null-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "[null]");
            var coordinator = new OperationCoordinator(path);
            Assert.Empty(coordinator.Snapshot());
            Assert.False(coordinator.HasWork);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task RunsOneAtATimeAndCancelsAQueuedOperation()
    {
        var queue = new OperationCoordinator(null);
        using var first = queue.Enqueue("Primera");
        using var second = queue.Enqueue("Segunda");
        using var third = queue.Enqueue("Tercera");
        await first.WaitAsync();
        Assert.False(second.WaitAsync().IsCompleted);
        Assert.False(third.WaitAsync().IsCompleted);
        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync());
        first.Complete();
        first.Dispose();
        await third.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("En curso", queue.Snapshot().Single(x => x.Name == "Tercera").State);
        third.Complete();
    }

    [Fact]
    public async Task CriticalTransferCannotBeCancelledAfterStarting()
    {
        var queue = new OperationCoordinator(null);
        using var transfer = queue.Enqueue("Cuarentena");
        await transfer.WaitAsync();
        Assert.True(transfer.EnterCriticalSection());
        queue.CancelAll();
        Assert.False(transfer.Token.IsCancellationRequested);
        transfer.Complete();
    }

    [Fact]
    public async Task UnconfirmedTerminationPausesTheQueueUntilExplicitlyResumed()
    {
        var queue = new OperationCoordinator(null);
        using var first = queue.Enqueue("Primera");
        using var second = queue.Enqueue("Segunda");
        await first.WaitAsync();
        first.Cancel();
        first.TerminationUnconfirmed();
        first.Dispose();
        Assert.True(queue.IsPaused);
        Assert.False(second.WaitAsync().IsCompleted);
        Assert.Equal("Terminación no confirmada", queue.Snapshot().Single(x => x.Name == "Primera").State);
        queue.ResumeAfterReview();
        await second.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        second.Complete();
    }

    [Fact]
    public async Task RestartMarksUnfinishedTasksInterruptedWithoutReplayingThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "senda-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "operations.json");
        try
        {
            var queue = new OperationCoordinator(path);
            using var task = queue.Enqueue("Análisis");
            await task.WaitAsync();
            var recovered = new OperationCoordinator(path);
            Assert.Equal("Terminación no confirmada", Assert.Single(recovered.Snapshot()).State);
            Assert.True(recovered.IsPaused);
            Assert.False(recovered.HasWork);
            Assert.NotEmpty(JsonSerializer.Deserialize<List<OperationRecord>>(await File.ReadAllTextAsync(path))!);
            recovered.ResumeAfterReview();
            var restarted = new OperationCoordinator(path);
            Assert.False(restarted.IsPaused);
            Assert.Equal("Interrumpida", Assert.Single(restarted.Snapshot()).State);
            task.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task CorruptJournalFallsBackToDurableBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "senda-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "operations.json");
        try
        {
            var queue = new OperationCoordinator(path);
            using var task = queue.Enqueue("Análisis");
            await task.WaitAsync();
            task.Stage("Preparando"); // The previous complete state is retained as a backup.
            File.WriteAllText(path, "{corrupt");
            var recovered = new OperationCoordinator(path);
            Assert.Equal("Terminación no confirmada", Assert.Single(recovered.Snapshot()).State);
            Assert.True(recovered.IsPaused);
            task.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void ReviewingInterruptedOptionsDoesNotSilentlyResumeTheQueue()
    {
        var path = Path.Combine(Path.GetTempPath(), "senda-paused-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new[] {
                new OperationRecord { Name = "Parcheo", State = "Terminación no confirmada" }
            }));
            var queue = new OperationCoordinator(path);
            queue.MarkReviewed(Assert.Single(queue.Snapshot()).Id);
            var restarted = new OperationCoordinator(path);
            Assert.True(restarted.IsPaused);
            restarted.ResumeAfterReview();
            Assert.False(new OperationCoordinator(path).IsPaused);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }
}
