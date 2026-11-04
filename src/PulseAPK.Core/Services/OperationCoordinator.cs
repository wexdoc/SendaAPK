using System.Text.Json;

namespace PulseAPK.Core.Services;

/// <summary>Serializes user operations and keeps interrupted work visible without restarting it.</summary>
public sealed class OperationCoordinator
{
    private readonly object sync = new();
    private readonly LinkedList<OperationTicket> waiting = new();
    private readonly List<OperationRecord> records = new();
    private readonly string? journalPath;
    private OperationTicket? active;
    private bool pausedForUnconfirmedTermination;
    public string? PersistenceWarning { get; private set; }
    public event EventHandler? Changed;

    public OperationCoordinator() : this(Path.Combine(LocalAnalysisService.DataRoot, "operations.json")) { }

    public OperationCoordinator(string? journalPath)
    {
        this.journalPath = journalPath;
        try
        {
            if (journalPath is null || (!File.Exists(journalPath) && !File.Exists(journalPath + ".bak"))) return;
            List<OperationRecord?> previous;
            try
            {
                previous = ReadJournal(journalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                previous = ReadJournal(journalPath + ".bak");
            }
            foreach (var item in previous.TakeLast(100))
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.State)) continue;
                if (item.State is "En curso" or "Cancelando")
                {
                    item.State = "Terminación no confirmada";
                    item.Stage = "La aplicación se cerró durante la tarea. Comprueba los procesos y archivos antes de reanudar.";
                    item.Finished = DateTimeOffset.Now;
                }
                else if (item.State == "En espera")
                {
                    item.State = "Interrumpida";
                    item.Stage = "La aplicación se cerró antes de completar la tarea. Puedes volver a iniciarla.";
                    item.Finished = DateTimeOffset.Now;
                }
                if (item.State is "Terminación no confirmada" or "Preparada, terminación no confirmada")
                    pausedForUnconfirmedTermination = true;
                records.Add(item);
            }
            SaveLocked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PersistenceWarning = "No se pudo recuperar el historial de tareas.";
            records.Add(new OperationRecord { Name = "Historial", State = "No disponible", Stage = "No se pudo leer el historial de tareas." });
        }
    }

    private static List<OperationRecord?> ReadJournal(string path) =>
        JsonSerializer.Deserialize<List<OperationRecord?>>(File.ReadAllText(path))
        ?? throw new JsonException("El historial está vacío.");

    public IReadOnlyList<OperationRecord> Snapshot()
    {
        lock (sync) return records.Select(x => x.Copy()).ToArray();
    }

    public bool HasWork { get { lock (sync) return active is not null || waiting.Count > 0; } }
    public bool IsPaused { get { lock (sync) return pausedForUnconfirmedTermination; } }

    public void ResumeAfterReview()
    {
        lock (sync)
        {
            pausedForUnconfirmedTermination = false;
            foreach (var record in records.Where(x => x.State == "Terminación no confirmada"))
                record.State = "Interrumpida";
            foreach (var record in records.Where(x => x.State == "Preparada, terminación no confirmada"))
                record.State = "Preparada para revisar";
            var started = StartNextLocked();
            SaveLocked();
            started?.Start.TrySetResult();
        }
        Notify();
    }

    public OperationTicket Enqueue(string name, bool cancellable = true, OperationRequestSnapshot? request = null)
    {
        var ticket = new OperationTicket(this, name, cancellable, request);
        lock (sync)
        {
            records.Add(ticket.Record);
            while (records.Count > 100 && records.Any(x => x.Finished is not null))
                records.Remove(records.First(x => x.Finished is not null));
            waiting.AddLast(ticket);
            var started = StartNextLocked();
            SaveLocked();
            started?.Start.TrySetResult();
        }
        Notify();
        return ticket;
    }

    public void MarkReviewed(string id)
    {
        lock (sync)
        {
            var record = records.FirstOrDefault(x => x.Id == id && (x.State is "Interrumpida" or "Terminación no confirmada"));
            if (record is null) return;
            record.State = record.State == "Terminación no confirmada" && pausedForUnconfirmedTermination
                ? "Preparada, terminación no confirmada" : "Preparada para revisar";
            SaveLocked();
        }
        Notify();
    }

    public void CancelAll()
    {
        OperationTicket[] tickets;
        lock (sync) tickets = waiting.Concat(active is null ? [] : new[] { active }).ToArray();
        foreach (var ticket in tickets) ticket.Cancel();
    }

    private OperationTicket? StartNextLocked()
    {
        if (pausedForUnconfirmedTermination || active is not null || waiting.First is null) return null;
        active = waiting.First.Value;
        waiting.RemoveFirst();
        active.Record.State = "En curso";
        active.Record.Started = DateTimeOffset.Now;
        return active;
    }

    internal void UpdateStage(OperationTicket ticket, string stage)
    {
        lock (sync) { ticket.Record.Stage = stage; SaveLocked(); }
        Notify();
    }

    internal void RequestCancel(OperationTicket ticket)
    {
        bool queued;
        CancellationToken token;
        lock (sync)
        {
            if (ticket.Done || !ticket.Cancellable || ticket.CancelRequested) return;
            ticket.CancelRequested = true;
            token = ticket.Token;
            var node = waiting.Find(ticket);
            queued = node is not null;
            if (node is not null)
            {
                waiting.Remove(node);
                ticket.Done = true;
                ticket.Record.State = "Cancelada";
                ticket.Record.Finished = DateTimeOffset.Now;
            }
            else if (ReferenceEquals(active, ticket)) ticket.Record.State = "Cancelando";
            SaveLocked();
        }
        try { ticket.CancelSource(); } catch (ObjectDisposedException) { }
        if (queued) ticket.Start.TrySetCanceled(token);
        Notify();
    }

    internal bool EnterCriticalSection(OperationTicket ticket)
    {
        lock (sync)
        {
            if (ticket.Done || ticket.CancelRequested || !ReferenceEquals(active, ticket)) return false;
            ticket.Cancellable = false;
            return true;
        }
    }

    internal void Finish(OperationTicket ticket, string state)
    {
        lock (sync)
        {
            if (ticket.Done) return;
            ticket.Done = true;
            ticket.Record.State = state;
            ticket.Record.Finished = DateTimeOffset.Now;
            if (state == "Terminación no confirmada") pausedForUnconfirmedTermination = true;
            if (ReferenceEquals(active, ticket)) active = null;
            else waiting.Remove(ticket);
            var started = StartNextLocked();
            SaveLocked();
            started?.Start.TrySetResult();
        }
        Notify();
    }

    private void SaveLocked()
    {
        if (journalPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
            var temp = journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, records);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(journalPath)) File.Replace(temp, journalPath, journalPath + ".bak");
                else File.Move(temp, journalPath);
                PersistenceWarning = null;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PersistenceWarning = "No se pudo guardar el historial; los trabajos interrumpidos podrían no aparecer al reiniciar.";
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class OperationTicket : IDisposable
{
    private readonly OperationCoordinator owner;
    private readonly CancellationTokenSource cancellation = new();
    internal readonly TaskCompletionSource Start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly OperationRecord Record;
    internal bool Done;
    internal bool CancelRequested;
    internal bool Cancellable { get; set; }
    private string outcome = "Fallida";

    internal OperationTicket(OperationCoordinator owner, string name, bool cancellable, OperationRequestSnapshot? request)
    {
        this.owner = owner;
        Cancellable = cancellable;
        Record = new OperationRecord { Name = name, State = "En espera", Stage = "Esperando turno", Request = request };
    }
    public CancellationToken Token => cancellation.Token;
    public bool CanCancel => Cancellable && !Done && !CancelRequested;
    public Task WaitAsync() => Start.Task;
    public void Stage(string stage) => owner.UpdateStage(this, stage);
    public void Complete() => outcome = "Completada";
    public void TerminationUnconfirmed()
    {
        outcome = "Terminación no confirmada";
        Stage("La cola está pausada. Comprueba los procesos y archivos temporales antes de reanudar.");
    }
    public bool EnterCriticalSection() => owner.EnterCriticalSection(this);
    internal void CancelSource() => cancellation.Cancel();
    public void Cancel() => owner.RequestCancel(this);
    public void Dispose()
    {
        owner.Finish(this, outcome == "Terminación no confirmada" ? outcome : CancelRequested ? "Cancelada" : outcome);
        cancellation.Dispose();
    }
}

public sealed class OperationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string Stage { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public OperationRequestSnapshot? Request { get; set; }
    internal OperationRecord Copy() => (OperationRecord)MemberwiseClone();
}

public sealed record OperationRequestSnapshot
{
    public string Kind { get; init; } = "";
    public string InputPath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public bool IncludeLibraries { get; init; }
    public bool DecodeResources { get; init; }
    public bool DecodeSources { get; init; }
    public bool KeepOriginalManifest { get; init; }
    public bool ExtractToApkFolder { get; init; }
    public bool SignApk { get; init; }
    public bool UseAapt2 { get; init; }
    public bool InjectForAllArchitectures { get; init; }
    public bool SkipDexValidation { get; init; }
    public int ScriptProfile { get; init; }
    public int DexMode { get; init; }
}
