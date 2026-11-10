using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TrophyShots.Player;

/// <summary>
/// mpv JSON IPC 客户端（Windows 命名管道，逐行 JSON）。
/// 协议见 https://mpv.io/manual/master/#json-ipc。
/// </summary>
public sealed class MpvIpc : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _readLoop;
    private long _nextRequestId;

    private MpvIpc(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>收到 <c>property-change</c> 事件：属性名与当前值。</summary>
    public event Action<string, JsonElement>? PropertyChanged;

    /// <summary>mpv 自己的日志消息（需要先 request_log_messages），格式为「前缀 + 正文」。</summary>
    public event Action<string>? LogMessage;

    /// <summary>读循环异常结束（mpv 退出、管道断开）。</summary>
    public event Action<string>? Faulted;

    public static async Task<MpvIpc> ConnectAsync(string pipePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        const string prefix = @"\\.\pipe\";
        string name = pipePath.StartsWith(prefix, StringComparison.Ordinal) ? pipePath[prefix.Length..] : pipePath;

        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await pipe.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
                return new MpvIpc(pipe);
            }
            catch (TimeoutException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw new TimeoutException($"连接 mpv IPC 超时（{timeout.TotalSeconds:0.#} 秒）：{pipePath}");
                }
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<JsonElement> CommandAsync(CancellationToken cancellationToken, params object?[] command)
    {
        long id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = completion;
        }

        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["request_id"] = id,
        });

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读属性：失败或属性不可用时返回 <see langword="null"/>。</summary>
    public async Task<JsonElement?> TryGetPropertyAsync(string property, CancellationToken cancellationToken)
    {
        JsonElement response = await CommandAsync(cancellationToken, "get_property", property).ConfigureAwait(false);
        if (!response.TryGetProperty("data", out JsonElement data))
        {
            return null;
        }

        return data.ValueKind == JsonValueKind.Null ? null : data.Clone();
    }

    public Task ObservePropertyAsync(string property, CancellationToken cancellationToken) =>
        CommandAsync(cancellationToken, "observe_property", Interlocked.Increment(ref _nextRequestId), property);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                Dispatch(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex.Message);
        }
        finally
        {
            FailPending("mpv IPC 连接已断开");

            // 正常 EOF（mpv 退出或管道被关）也要当作断开，否则上层收不到任何信号。
            if (!_cancellation.IsCancellationRequested)
            {
                Faulted?.Invoke("mpv 关闭了 IPC 连接");
            }
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("request_id", out JsonElement requestId)
                && requestId.ValueKind == JsonValueKind.Number)
            {
                long id = requestId.GetInt64();
                TaskCompletionSource<JsonElement>? completion;
                lock (_pending)
                {
                    _pending.Remove(id, out completion);
                }

                completion?.TrySetResult(root.Clone());
                return;
            }

            if (root.TryGetProperty("event", out JsonElement eventName)
                && eventName.GetString() == "log-message"
                && root.TryGetProperty("text", out JsonElement text))
            {
                string prefix = root.TryGetProperty("prefix", out JsonElement prefixElement)
                    ? prefixElement.GetString() ?? ""
                    : "";
                LogMessage?.Invoke($"{prefix}{text.GetString()}".Trim());
                return;
            }

            if (root.TryGetProperty("event", out eventName)
                && eventName.GetString() == "property-change"
                && root.TryGetProperty("name", out JsonElement name)
                && name.GetString() is { } propertyName)
            {
                JsonElement value = root.TryGetProperty("data", out JsonElement data) ? data.Clone() : default;
                PropertyChanged?.Invoke(propertyName, value);
            }
        }
    }

    private void FailPending(string reason)
    {
        TaskCompletionSource<JsonElement>[] pending;
        lock (_pending)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<JsonElement> completion in pending)
        {
            completion.TrySetException(new IOException(reason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读循环已经自己结束或超时，这里不再关心。
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _cancellation.Dispose();
    }
}
