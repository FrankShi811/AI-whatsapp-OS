using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Core.Services;

public sealed record WhatsAppBridgeEvent(string Name, string AccountId, JsonElement Data);

public sealed class WhatsAppBridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class WhatsAppBridgeClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private TaskCompletionSource _ready = NewSignal();
    private CancellationTokenSource? _lifetime;

    public event EventHandler<WhatsAppBridgeEvent>? EventReceived;
    public bool IsRunning => _process is { HasExited: false };
    public bool IsConnected => ConnectionState == "connected";
    public string ConnectionState { get; private set; } = "disconnected";
    public string CurrentAccountId { get; private set; } = "primary";
    public string LastBridgeError { get; private set; } = "";

    public async Task StartAsync(string accountId = "primary", CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        CurrentAccountId = string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId;
        var launch = BridgeLaunch.Resolve();
        _ready = NewSignal();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var start = new ProcessStartInfo
        {
            FileName = launch.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = launch.WorkingDirectory
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new WhatsAppBridgeException("bridge_start_failed", "无法启动 WhatsApp 桥接进程。");
        _input = _process.StandardInput;
        _ = ReadOutputAsync(_process.StandardOutput, _lifetime.Token);
        _ = ReadErrorsAsync(_process.StandardError, _lifetime.Token);
        _ = ObserveExitAsync(_process, _lifetime.Token);

        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        var sessionSecrets = new WindowsCredentialStore($"WAFlow/WhatsAppSessionKey/{CurrentAccountId}");
        var encryptionKey = sessionSecrets.Read();
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            sessionSecrets.Save(encryptionKey);
        }
        await SendCommandAsync("initialize", new { accountId = CurrentAccountId, encryptionKey }, cancellationToken);
    }

    public Task<JsonElement> PingAsync(CancellationToken cancellationToken = default) => SendCommandAsync("ping", null, cancellationToken);
    public async Task<JsonElement> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectionState = "connecting";
        return await SendCommandAsync("connect", null, cancellationToken);
    }
    public async Task<JsonElement> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendCommandAsync("disconnect", null, cancellationToken);
        ConnectionState = "disconnected";
        return result;
    }
    public async Task<JsonElement> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendCommandAsync("logout", null, cancellationToken);
        ConnectionState = "logged_out";
        new WindowsCredentialStore($"WAFlow/WhatsAppSessionKey/{CurrentAccountId}").Delete();
        return result;
    }
    public Task<JsonElement> SendTextAsync(string phone, string text, CancellationToken cancellationToken = default) =>
        SendCommandAsync("send_text", new { phone, text }, cancellationToken);
    public Task<JsonElement> SendMediaAsync(string phone, string path, string caption, CancellationToken cancellationToken = default) =>
        SendCommandAsync("send_media", new { phone, path, caption }, cancellationToken);
    public Task<JsonElement> SetChatPinnedAsync(string phone, bool pinned, CancellationToken cancellationToken = default) =>
        SendCommandAsync("set_chat_pin", new { phone, pinned }, cancellationToken);
    public Task<JsonElement> SyncNowAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("sync_now", null, cancellationToken);

    private async Task<JsonElement> SendCommandAsync(string command, object? payload, CancellationToken cancellationToken)
    {
        if (!IsRunning || _input is null) throw new WhatsAppBridgeException("bridge_not_running", "WhatsApp 桥接进程尚未启动。");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("无法创建桥接请求。");
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { command, requestId, payload }));
            var message = new Dictionary<string, object?>
            {
                ["command"] = command,
                ["requestId"] = requestId
            };
            if (payload is not null)
                foreach (var property in document.RootElement.GetProperty("payload").EnumerateObject()) message[property.Name] = property.Value.Clone();
            var line = JsonSerializer.Serialize(message);
            await _writeLock.WaitAsync(cancellationToken);
            try { await _input.WriteLineAsync(line.AsMemory(), cancellationToken); await _input.FlushAsync(); }
            finally { _writeLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task ReadOutputAsync(StreamReader output, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await output.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException)
                {
                    LastBridgeError = "桥接进程产生了一行非协议输出，已安全忽略。";
                    continue;
                }
                using (document)
                {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement)) continue;
                var type = typeElement.GetString();
                if (type == "response")
                {
                    var requestId = root.GetProperty("requestId").GetString() ?? "";
                    if (!_pending.TryGetValue(requestId, out var completion)) continue;
                    if (root.GetProperty("ok").GetBoolean()) completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                    else
                    {
                        var error = root.TryGetProperty("error", out var errorElement) ? errorElement : default;
                        completion.TrySetException(new WhatsAppBridgeException(
                            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) ? code.GetString() ?? "bridge_error" : "bridge_error",
                            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.GetString() ?? "WhatsApp 桥接调用失败。" : "WhatsApp 桥接调用失败。"));
                    }
                    continue;
                }
                if (type != "event") continue;
                var eventName = root.TryGetProperty("event", out var eventElement) ? eventElement.GetString() ?? "unknown" : "unknown";
                if (eventName == "ready") _ready.TrySetResult();
                var accountId = root.TryGetProperty("accountId", out var account) ? account.GetString() ?? "" : "";
                var data = root.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : default;
                if (eventName == "connection" && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("state", out var state))
                    ConnectionState = state.GetString() ?? "disconnected";
                EventReceived?.Invoke(this, new WhatsAppBridgeEvent(eventName, accountId, data));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            LastBridgeError = error.Message;
            FailPending(error);
        }
    }

    private async Task ReadErrorsAsync(StreamReader errors, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await errors.ReadLineAsync(cancellationToken) is { } line)
                if (!string.IsNullOrWhiteSpace(line)) LastBridgeError = line.Length > 1000 ? line[..1000] : line;
        }
        catch (OperationCanceledException) { }
    }

    private async Task ObserveExitAsync(Process process, CancellationToken cancellationToken)
    {
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        ConnectionState = "disconnected";
        FailPending(new WhatsAppBridgeException("bridge_exited", $"WhatsApp 桥接进程已退出，代码 {process.ExitCode}。"));
    }

    private void FailPending(Exception error)
    {
        foreach (var pair in _pending) pair.Value.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (IsRunning) await DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _lifetime?.Cancel();
        if (_process is { HasExited: false })
        {
            try { _input?.Close(); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { try { _process.Kill(entireProcessTree: true); } catch { } }
        }
        _process?.Dispose();
        _writeLock.Dispose();
        _lifetime?.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record BridgeLaunch(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments)
    {
        public static BridgeLaunch Resolve()
        {
            var explicitExe = Environment.GetEnvironmentVariable("WAFLOW_BRIDGE_EXE");
            if (!string.IsNullOrWhiteSpace(explicitExe) && File.Exists(explicitExe))
                return new(explicitExe, Path.GetDirectoryName(explicitExe)!, []);

            var processDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var packaged = Path.Combine(processDirectory, "WAFlow.WhatsApp.Bridge.exe");
            if (File.Exists(packaged)) return new(packaged, processDirectory, []);

            var embedded = ExtractEmbeddedBridge();
            if (!string.IsNullOrWhiteSpace(embedded) && File.Exists(embedded))
                return new(embedded, Path.GetDirectoryName(embedded)!, []);

            var script = Environment.GetEnvironmentVariable("WAFLOW_BRIDGE_SCRIPT");
            if (string.IsNullOrWhiteSpace(script) || !File.Exists(script)) script = FindDevelopmentScript(AppContext.BaseDirectory);
            var node = Environment.GetEnvironmentVariable("WAFLOW_NODE_PATH");
            if (string.IsNullOrWhiteSpace(node) || !File.Exists(node)) node = FindNode();
            if (!string.IsNullOrWhiteSpace(script) && File.Exists(script) && !string.IsNullOrWhiteSpace(node) && File.Exists(node))
                return new(node, Path.GetDirectoryName(Path.GetDirectoryName(script))!, [script]);

            throw new WhatsAppBridgeException("bridge_runtime_missing", "未找到 WAFlow.WhatsApp.Bridge.exe。开发环境可设置 WAFLOW_NODE_PATH 和 WAFLOW_BRIDGE_SCRIPT。");
        }

        private static string? ExtractEmbeddedBridge()
        {
            var assembly = typeof(WhatsAppBridgeClient).Assembly;
            using var resource = assembly.GetManifestResourceStream("WAFlow.WhatsApp.Bridge.exe");
            if (resource is null) return null;
            var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFlow", "runtime", version);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, "WAFlow.WhatsApp.Bridge.exe");
            if (File.Exists(destination) && new FileInfo(destination).Length == resource.Length) return destination;
            var temporary = destination + $".{Environment.ProcessId}.tmp";
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(output);
            try { File.Move(temporary, destination, true); }
            catch when (File.Exists(destination) && new FileInfo(destination).Length == resource.Length) { File.Delete(temporary); }
            return destination;
        }

        private static string? FindDevelopmentScript(string start)
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "bridge", "src", "index.mjs");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string? FindNode()
        {
            foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(segment.Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            var codexRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe");
            return File.Exists(codexRuntime) ? codexRuntime : null;
        }
    }
}
