using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TrophyShots.Mpv;

/// <summary>
/// mpv JSON IPC 客户端（Windows 命名管道，逐行 JSON）。
/// 协议见 https://mpv.io/manual/master/#json-ipc。
/// </summary>
public sealed class MpvIpc : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _readLoop;
    private long _nextId;

    /// <summary>收到 <c>property-change</c> 事件：属性名与当前值。</summary>
    public event Action<string, JsonElement>? PropertyChanged;

    /// <summary>收到任意非回复事件（含 property-change）。</summary>
    public event Action<JsonElement>? EventReceived;

    /// <summary>读循环异常结束（mpv 退出、管道断开）。</summary>
    public event Action<string>? Faulted;

    private MpvIpc(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public static async Task<MpvIpc> ConnectAsync(string pipePath, TimeSpan timeout)
    {
        const string prefix = @"\\.\pipe\";
        string name = pipePath.StartsWith(prefix, StringComparison.Ordinal) ? pipePath[prefix.Length..] : pipePath;

        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                await pipe.ConnectAsync(200).ConfigureAwait(false);
                return new MpvIpc(pipe);
            }
            catch (TimeoutException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    pipe.Dispose();
                    throw new TimeoutException($"连接 mpv IPC 超时（{timeout.TotalSeconds:0.#} 秒）：{pipePath}");
                }
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    pipe.Dispose();
                    throw;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    public async Task<JsonElement> CommandAsync(params object?[] command)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = tcs;
        }

        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["request_id"] = id,
        });

        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public async Task<string?> GetStringAsync(string property)
    {
        JsonElement response = await CommandAsync("get_property", property).ConfigureAwait(false);
        return TryGetData(response, out JsonElement data) && data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : null;
    }

    public async Task<double?> GetDoubleAsync(string property)
    {
        JsonElement response = await CommandAsync("get_property", property).ConfigureAwait(false);
        return TryGetData(response, out JsonElement data) && data.ValueKind == JsonValueKind.Number
            ? data.GetDouble()
            : null;
    }

    public Task ObserveAsync(string property) => CommandAsync("observe_property", 1 + Interlocked.Increment(ref _nextId), property);

    public static bool TryGetData(JsonElement response, out JsonElement data)
    {
        if (response.TryGetProperty("data", out data))
        {
            return true;
        }

        data = default;
        return false;
    }

    public static string DescribeResponse(JsonElement response)
    {
        string error = response.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "?" : "?";
        if (error != "success")
        {
            return $"error={error}";
        }

        if (!response.TryGetProperty("data", out JsonElement data))
        {
            return "success";
        }

        return data.ValueKind switch
        {
            JsonValueKind.String => data.GetString() ?? "",
            JsonValueKind.Number => data.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => data.GetRawText(),
            _ => data.GetRawText(),
        };
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
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
                TaskCompletionSource<JsonElement>? tcs;
                lock (_pending)
                {
                    if (_pending.Remove(id, out tcs))
                    {
                        tcs.TrySetResult(root.Clone());
                    }
                }

                return;
            }

            if (!root.TryGetProperty("event", out JsonElement eventName))
            {
                return;
            }

            JsonElement snapshot = root.Clone();
            EventReceived?.Invoke(snapshot);

            if (eventName.GetString() == "property-change"
                && root.TryGetProperty("name", out JsonElement name)
                && name.GetString() is string propertyName)
            {
                JsonElement value = root.TryGetProperty("data", out JsonElement data) ? data.Clone() : default;
                PropertyChanged?.Invoke(propertyName, value);
            }
        }
    }

    private void FailPending(string reason)
    {
        List<TaskCompletionSource<JsonElement>> pending;
        lock (_pending)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<JsonElement> tcs in pending)
        {
            tcs.TrySetException(new IOException(reason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception)
        {
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
