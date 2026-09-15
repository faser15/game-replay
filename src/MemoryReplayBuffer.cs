using System.IO.Pipes;

namespace GameReplay;

/// <summary>Stores immutable, completed MPEG-TS segments in a bounded RAM ring.</summary>
/// <remarks>The integration test must exercise FFmpeg's Windows named-pipe segment output.</remarks>
public sealed class MemoryReplayBuffer : IDisposable
{
    readonly int SegmentLimit;
    readonly long ByteLimit;
    const int ActiveLimit = 8 * 1024 * 1024;
    readonly object gate = new();
    readonly Queue<byte[]> segments = new();
    readonly Dictionary<long, Listener> listeners = new();
    readonly CancellationTokenSource cancel = new();
    readonly string name = "GameReplay-" + Guid.NewGuid().ToString("N");
    readonly Task worker;
    Task? stopTask;
    long bytes, completedSequence;
    string? failure;
    bool disposed;

    sealed class Listener : IDisposable
    {
        public readonly NamedPipeServerStream Pipe;
        public readonly Task Connected;
        public Listener(string name, CancellationToken token)
        {
            Pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
            Connected = Pipe.WaitForConnectionAsync(token);
        }
        public void Dispose() => Pipe.Dispose();
    }

    public string OutputPattern => @"\\.\pipe\" + name + "-%08d.ts";
    public int Count { get { lock (gate) return segments.Count; } }
    public long Bytes { get { lock (gate) return bytes; } }
    public long CompletedSequence { get { lock (gate) return completedSequence; } }
    public string? Failure { get { lock (gate) return failure; } }

    public MemoryReplayBuffer(int seconds = 300, long byteLimit = 160L * 1024 * 1024)
    {
        SegmentLimit = Math.Clamp((seconds + 1) / 2, 1, 300);
        ByteLimit = Math.Max(ActiveLimit, byteLimit);
        try
        {
            // Register and start listening before FFmpeg is launched. Subsequent
            // output files already exist as listening pipes when a segment closes.
            for (long i = 0; i < 3; i++) AddListener(i);
            worker = Task.Run(ReadSegmentsAsync);
        }
        catch
        {
            cancel.Cancel();
            foreach (var listener in listeners.Values) listener.Dispose();
            cancel.Dispose();
            throw;
        }
    }

    void AddListener(long index)
    {
        lock (gate)
        {
            cancel.Token.ThrowIfCancellationRequested();
            listeners.Add(index, new Listener(name + "-" + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".ts", cancel.Token));
        }
    }

    async Task ReadSegmentsAsync()
    {
        byte[] block = new byte[65536];
        try
        {
            for (long index = 0; ; index++)
            {
                Listener listener;
                lock (gate) listener = listeners[index];
                await listener.Connected.ConfigureAwait(false);
                // Keep at least two future pipe names listening at all times.
                AddListener(index + 3);
                using var data = new MemoryStream(1024 * 1024);
                while (true)
                {
                    int read = await listener.Pipe.ReadAsync(block.AsMemory(), cancel.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (data.Length + read > ActiveLimit)
                        throw new IOException("A replay segment exceeded the 8 MB memory limit. Recording must stop.");
                    data.Write(block, 0, read);
                }
                // EOF commits even the final short segment after FFmpeg exits.
                if (data.Length > 0)
                {
                    byte[] finished = data.ToArray();
                    lock (gate)
                    {
                        if (!disposed)
                        {
                            while (segments.Count >= SegmentLimit || bytes + finished.Length > ByteLimit)
                                bytes -= segments.Dequeue().Length;
                            segments.Enqueue(finished);
                            bytes += finished.Length;
                            completedSequence++;
                        }
                    }
                }
                lock (gate) listeners.Remove(index);
                listener.Dispose();
            }
        }
        catch (Exception error)
        {
            if (!cancel.IsCancellationRequested)
                lock (gate) failure = error.Message;
        }
        finally
        {
            Listener[] remaining;
            lock (gate) { remaining = listeners.Values.ToArray(); listeners.Clear(); }
            foreach (var listener in remaining) listener.Dispose();
            // Observe cancellation/failure from ahead-of-time connection tasks.
            foreach (var listener in remaining)
                try { await listener.Connected.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>References must be treated as immutable; no video bytes are copied.</summary>
    public byte[][] Snapshot(int max = 150)
    {
        if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
        lock (gate) return segments.Skip(Math.Max(0, segments.Count - max)).ToArray();
    }

    /// <summary>Call after stopping FFmpeg; allow final EOF/data to drain, then close listeners.</summary>
    public Task StopAsync()
    {
        lock (gate) return stopTask ??= StopCoreAsync();
    }

    async Task StopCoreAsync()
    {
        await Task.WhenAny(worker, Task.Delay(500)).ConfigureAwait(false);
        cancel.Cancel();
        Listener[] open;
        lock (gate) open = listeners.Values.ToArray();
        foreach (var listener in open) listener.Dispose();
        await worker.ConfigureAwait(false);
    }

    public void Clear()
    {
        lock (gate) { segments.Clear(); bytes = 0; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        StopAsync().GetAwaiter().GetResult();
        Clear();
        cancel.Dispose();
    }
}


