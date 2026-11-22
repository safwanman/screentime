using System.Net;
using System.IO;
using System.Text.Json;

namespace ScreenTimeTracker.Services;

public sealed class EdgeBridge : IDisposable
{
    private const string Prefix = "http://127.0.0.1:43183/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly UsageTracker _tracker;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cancellation;

    public EdgeBridge(UsageTracker tracker)
    {
        _tracker = tracker;
    }

    public event Action<Exception>? ListenerFailed;

    public void Start()
    {
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _cancellation = new CancellationTokenSource();
        _ = AcceptRequestsAsync(_cancellation.Token);
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _listener.Close();
        _cancellation?.Dispose();
    }

    private async Task AcceptRequestsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ListenerFailed?.Invoke(exception);
                }

                break;
            }

            _ = HandleRequestAsync(context, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        try
        {
            if (context.Request.RemoteEndPoint is not { } remoteEndPoint ||
                !IPAddress.IsLoopback(remoteEndPoint.Address))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            var origin = context.Request.Headers["Origin"];
            if (!IsExtensionOrigin(origin))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            response.Headers["Vary"] = "Origin";

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            if (context.Request.HttpMethod != "POST" ||
                context.Request.Url?.AbsolutePath != "/site" ||
                context.Request.ContentLength64 > 2048)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            var heartbeat = await JsonSerializer.DeserializeAsync<SiteHeartbeat>(
                context.Request.InputStream,
                JsonOptions,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (heartbeat is null ||
                string.IsNullOrWhiteSpace(heartbeat.Host) ||
                heartbeat.Host.Length > 253 ||
                Uri.CheckHostName(heartbeat.Host) == UriHostNameType.Unknown ||
                heartbeat.Timestamp < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 20_000 ||
                heartbeat.Timestamp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20_000)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            var host = heartbeat.Host.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
            {
                host = host[4..];
            }

            _tracker.UpdateActiveSite(host, DateTime.UtcNow);
            var blockReason = _tracker.GetSiteBlockReason(host, heartbeat.ShortForm);

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            var result = JsonSerializer.Serialize(new
            {
                blocked = blockReason is not null,
                reason = blockReason
            });
            var responseBody = System.Text.Encoding.UTF8.GetBytes(result);
            response.ContentLength64 = responseBody.Length;
            await response.OutputStream.WriteAsync(
                responseBody,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
        }
        catch (IOException)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static bool IsExtensionOrigin(string? origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("edge-extension", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SiteHeartbeat
    {
        public string Host { get; set; } = "";
        public long Timestamp { get; set; }
        public bool ShortForm { get; set; }
    }
}
