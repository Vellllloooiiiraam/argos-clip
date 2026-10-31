using System.Collections.Concurrent;

namespace ArgosClip;

/// <summary>Estado de um job (dicionário thread-safe, igual ao JOBS_STATE do original).</summary>
public sealed class JobState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _data = new();

    public void Set(string key, object? value)
    {
        lock (_gate) _data[key] = value;
    }

    public void Set(params (string Key, object? Value)[] pairs)
    {
        lock (_gate)
        {
            foreach (var (k, v) in pairs) _data[k] = v;
        }
    }

    public object? Get(string key)
    {
        lock (_gate) return _data.TryGetValue(key, out var v) ? v : null;
    }

    public Dictionary<string, object?> Snapshot()
    {
        lock (_gate) return new Dictionary<string, object?>(_data);
    }
}

public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new();

    public void Add(string id, Dictionary<string, object?> init)
    {
        var st = new JobState();
        foreach (var (k, v) in init) st.Set(k, v);
        _jobs[id] = st;
    }

    public JobState? Get(string id) => _jobs.TryGetValue(id, out var s) ? s : null;

    public void Remove(string id) => _jobs.TryRemove(id, out _);

    public IReadOnlyCollection<KeyValuePair<string, JobState>> All() => _jobs.ToArray();
}

/// <summary>
/// Fila com limite de jobs processando ao mesmo tempo (evita estouro de memória
/// com vários Whisper em paralelo). Os demais ficam "na fila". Porta do worker
/// loop do original.
/// </summary>
public sealed class JobQueue
{
    private readonly ConcurrentQueue<(string Id, Action Run)> _queue = new();
    private readonly object _sync = new();
    private int _active;
    private readonly int _max;

    public JobQueue(int max)
    {
        _max = Math.Max(1, max);
        var t = new Thread(Loop) { IsBackground = true, Name = "argos-worker" };
        t.Start();
    }

    public void Enqueue(string id, Action fn) => _queue.Enqueue((id, fn));

    private void Loop()
    {
        while (true)
        {
            (string Id, Action Run)? job = null;
            lock (_sync)
            {
                if (_queue.TryPeek(out var peek) && _active < _max)
                {
                    if (_queue.TryDequeue(out var got)) job = got;
                    if (job is not null) _active++;
                }
            }
            if (job is null)
            {
                Thread.Sleep(300);
                continue;
            }
            try
            {
                job.Value.Run();
            }
            catch
            {
                // o próprio processamento já registra o erro no estado do job
            }
            finally
            {
                lock (_sync) _active--;
            }
        }
    }
}
