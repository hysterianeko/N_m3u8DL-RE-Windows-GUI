using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace M3u8DownloaderGui
{
    // Bridges downloader requests through Windows curl. This is useful for CDNs
    // that accept Schannel/curl but reject the .NET HTTP stack before inspecting
    // otherwise-correct Referer and User-Agent headers.
    internal sealed class CurlMediaProxy : IDisposable
    {
        private const int MaximumRequestLineLength = 8192;
        private const int MaximumRequestHeaderBytes = 65536;
        private const int MaximumRequestHeaderCount = 100;
        private const int MaximumForwardedHeaderValueLength = 16384;
        private const int CurlConnectTimeoutSeconds = 30;
        private const int CurlTimeoutSeconds = 300;
        private const int CurlLowSpeedTimeSeconds = 60;
        private const int CurlLowSpeedBytesPerSecond = 512;
        private const int MaximumConcurrentClients = 24;
        private const int MaximumUpstreamRedirects = 5;

        private static readonly HashSet<string> BlockedForwardedHeaderNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Accept-Encoding",
                "Connection",
                "Content-Length",
                "Cookie2",
                "Date",
                "Expect",
                "Host",
                "Keep-Alive",
                "Proxy-Authenticate",
                "Proxy-Authorization",
                "Proxy-Connection",
                "Range",
                "Set-Cookie",
                "Set-Cookie2",
                "TE",
                "Trailer",
                "Transfer-Encoding",
                "Upgrade",
                "Via",
                "WWW-Authenticate"
            };

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation;
        private readonly ConcurrentDictionary<string, RegisteredTarget> _targets;
        private readonly ConcurrentDictionary<int, Task> _workers;
        private readonly ConcurrentDictionary<int, TcpClient> _activeClients;
        private readonly SemaphoreSlim _clientSlots;
        private readonly string _pathToken;
        private readonly string _workDirectory;
        private readonly string _curlPath;
        private readonly bool _supportsSslRevokeBestEffort;
        private readonly MediaRequestHeaders _headers;
        private readonly Action<string> _log;
        private readonly int _port;
        private readonly Task _acceptLoop;

        private int _nextWorkerId;
        private int _disposed;
        private long _successfulRequestCount;
        private long _transientRetryCount;
        private long _upstreamFailureCount;
        private long _clientDisconnectCount;

        private CurlMediaProxy(
            TcpListener listener,
            string pathToken,
            string workDirectory,
            string curlPath,
            bool supportsSslRevokeBestEffort,
            MediaRequestHeaders headers,
            Action<string> log)
        {
            _listener = listener;
            _pathToken = pathToken;
            _workDirectory = workDirectory;
            _curlPath = curlPath;
            _supportsSslRevokeBestEffort = supportsSslRevokeBestEffort;
            _headers = headers ?? new MediaRequestHeaders();
            _log = log;
            _cancellation = new CancellationTokenSource();
            _targets = new ConcurrentDictionary<string, RegisteredTarget>(
                StringComparer.OrdinalIgnoreCase);
            _workers = new ConcurrentDictionary<int, Task>();
            _activeClients = new ConcurrentDictionary<int, TcpClient>();
            _clientSlots = new SemaphoreSlim(MaximumConcurrentClients, MaximumConcurrentClients);
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        }

        public static CurlMediaProxy Start(
            string tempDirectory,
            MediaRequestHeaders headers,
            Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(tempDirectory))
            {
                throw new ArgumentException("A proxy temporary directory is required.", "tempDirectory");
            }

            string curlPath;
            string curlError;
            bool supportsSslRevokeBestEffort;
            if (!IsCurlAvailable(
                    out curlPath,
                    out curlError,
                    out supportsSslRevokeBestEffort))
            {
                throw new InvalidOperationException(curlError);
            }

            string fullTempDirectory = Path.GetFullPath(tempDirectory);
            Directory.CreateDirectory(fullTempDirectory);

            string token = CreateRandomToken();
            string workDirectory = Path.Combine(fullTempDirectory, "curl_proxy_" + token);
            Directory.CreateDirectory(workDirectory);

            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new CurlMediaProxy(
                    listener,
                    token,
                    workDirectory,
                    curlPath,
                    supportsSslRevokeBestEffort,
                    headers == null ? new MediaRequestHeaders() : headers.Clone(),
                    log);
            }
            catch
            {
                if (listener != null)
                {
                    try
                    {
                        listener.Stop();
                    }
                    catch
                    {
                    }
                }

                TryDeleteOwnedWorkDirectory(workDirectory);
                throw;
            }
        }

        public static bool IsCurlAvailable()
        {
            string path;
            string error;
            bool ignored;
            return IsCurlAvailable(out path, out error, out ignored);
        }

        public static bool IsCurlAvailable(out string errorMessage)
        {
            string path;
            bool ignored;
            return IsCurlAvailable(out path, out errorMessage, out ignored);
        }

        public string Register(string url)
        {
            ThrowIfDisposed();

            Uri uri;
            if (string.IsNullOrWhiteSpace(url) ||
                url.Length > 16384 ||
                ContainsControlCharacter(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Only absolute HTTP(S) media URLs can be registered.", "url");
            }

            string id;
            RegisteredTarget target;
            do
            {
                id = Guid.NewGuid().ToString("N");
                string extension = GetSafeExtension(uri.AbsolutePath);
                string localPath = "/" + _pathToken + "/" + id + extension;
                target = new RegisteredTarget(id, uri.AbsoluteUri, localPath);
            }
            while (!_targets.TryAdd(id, target));

            return "http://127.0.0.1:" +
                   _port.ToString(CultureInfo.InvariantCulture) +
                   target.LocalPath;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            try
            {
                _listener.Stop();
            }
            catch
            {
            }

            CloseActiveClients();

            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            // An accept may have completed just as Stop was called.
            CloseActiveClients();

            Task[] workers = _workers.Values.ToArray();
            bool workersCompleted = workers.Length == 0;
            if (workers.Length > 0)
            {
                try
                {
                    workersCompleted = Task.WaitAll(workers, TimeSpan.FromSeconds(10));
                }
                catch
                {
                }
            }

            _targets.Clear();
            if (workersCompleted)
            {
                _clientSlots.Dispose();
                _cancellation.Dispose();
                TryDeleteOwnedWorkDirectory(_workDirectory);
            }
            else
            {
                Log("curl proxy shutdown timed out; remaining workers will exit after their sockets close.");
            }

            long retryCount = Interlocked.Read(ref _transientRetryCount);
            long failureCount = Interlocked.Read(ref _upstreamFailureCount);
            long disconnectCount = Interlocked.Read(ref _clientDisconnectCount);
            if (retryCount > 0 || failureCount > 0 || disconnectCount > 0)
            {
                Log(
                    "network fluctuation summary: automatic retries " +
                    retryCount.ToString(CultureInfo.InvariantCulture) +
                    ", requests returned to downloader for another attempt " +
                    failureCount.ToString(CultureInfo.InvariantCulture) +
                    ", local early disconnects " +
                    disconnectCount.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Log("curl proxy stopped accepting requests: " + exception.Message);
                    }

                    break;
                }
                catch (Exception exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Log("curl proxy accept failed: " + exception.Message);
                    }

                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    TryCloseClient(client);
                    break;
                }

                if (!_clientSlots.Wait(0))
                {
                    RejectOverloadedClient(client);
                    continue;
                }

                int workerId = Interlocked.Increment(ref _nextWorkerId);
                _activeClients[workerId] = client;
                Task worker = new Task(
                    () =>
                    {
                        try
                        {
                            HandleClientSafely(client, cancellationToken);
                        }
                        finally
                        {
                            TcpClient ignoredClient;
                            _activeClients.TryRemove(workerId, out ignoredClient);
                            _clientSlots.Release();
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach);
                _workers[workerId] = worker;
                Task cleanup = worker.ContinueWith(
                    completed =>
                    {
                        Task ignored;
                        _workers.TryRemove(workerId, out ignored);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                if (cancellationToken.IsCancellationRequested)
                {
                    TryCloseClient(client);
                }

                try
                {
                    worker.Start(TaskScheduler.Default);
                }
                catch
                {
                    Task ignoredTask;
                    TcpClient ignoredClient;
                    _workers.TryRemove(workerId, out ignoredTask);
                    _activeClients.TryRemove(workerId, out ignoredClient);
                    _clientSlots.Release();
                    TryCloseClient(client);
                    throw;
                }
            }
        }

        private void CloseActiveClients()
        {
            foreach (TcpClient client in _activeClients.Values)
            {
                TryCloseClient(client);
            }
        }

        private static void RejectOverloadedClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.SendTimeout = 1000;
                    SendError(
                        client.GetStream(),
                        503,
                        "Service Unavailable",
                        "Media proxy concurrency limit reached.");
                }
                catch
                {
                }
            }
        }

        private void HandleClientSafely(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 15000;
                    // Dispose closes active sockets to interrupt stalled writes.
                    client.SendTimeout = 0;
                    HandleClient(client, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        long count = Interlocked.Increment(ref _clientDisconnectCount);
                        if (ShouldLogRepeatedEvent(count, 50))
                        {
                            Log(
                                "downloader closed a local request early; it can retry the segment " +
                                "(count " + count.ToString(CultureInfo.InvariantCulture) + "). " +
                                SanitizeLogText(exception.Message));
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Log("curl proxy request failed: " + exception.Message);
                        TrySendError(client, 502, "Bad Gateway", "Media proxy request failed.");
                    }
                }
            }
        }

        private void HandleClient(TcpClient client, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NetworkStream stream = client.GetStream();
            ProxyRequest request;
            string requestError;
            int requestErrorStatus;
            if (!TryReadRequest(
                    stream,
                    out request,
                    out requestErrorStatus,
                    out requestError))
            {
                SendError(stream, requestErrorStatus, GetReasonPhrase(requestErrorStatus), requestError);
                return;
            }

            RegisteredTarget target;
            if (!TryResolveTarget(request.RawPath, out target))
            {
                SendError(stream, 404, "Not Found", "Unknown media proxy resource.");
                return;
            }

            string requestId = Guid.NewGuid().ToString("N");
            string bodyPath = Path.Combine(_workDirectory, requestId + ".body");
            string headerPath = Path.Combine(_workDirectory, requestId + ".headers");

            try
            {
                CurlResult result = ExecuteCurl(
                    target,
                    request,
                    bodyPath,
                    headerPath,
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    long failureCount = Interlocked.Increment(ref _upstreamFailureCount);
                    if (ShouldLogRepeatedEvent(failureCount, 20))
                    {
                        string detail = string.IsNullOrWhiteSpace(result.SanitizedStandardError)
                            ? string.Empty
                            : " " + result.SanitizedStandardError;
                        Log(
                            "an upstream request still failed after the proxy retry and was returned " +
                            "to the downloader for normal segment retry (count " +
                            failureCount.ToString(CultureInfo.InvariantCulture) +
                            ", latest curl exit " +
                            result.ExitCode.ToString(CultureInfo.InvariantCulture) + ", " +
                            DescribeTarget(target) + ")." + detail);
                    }
                    SendError(stream, 502, "Bad Gateway", "curl could not retrieve the media resource.");
                    return;
                }

                UpstreamResponse response;
                if (!TryReadUpstreamResponse(headerPath, result.HttpStatusCode, out response))
                {
                    SendError(stream, 502, "Bad Gateway", "curl returned an invalid upstream response.");
                    return;
                }

                if (response.StatusCode >= 300 && response.StatusCode <= 399)
                {
                    Log(
                        "curl proxy rejected an unsafe upstream redirect for " +
                        DescribeTarget(target) + " (HTTP " +
                        response.StatusCode.ToString(CultureInfo.InvariantCulture) + ").");
                    SendError(
                        stream,
                        502,
                        "Bad Gateway",
                        "The media server returned an unsafe redirect.");
                    return;
                }

                SendUpstreamResponse(
                    stream,
                    request,
                    response,
                    bodyPath,
                    cancellationToken);
                long successfulCount = Interlocked.Increment(ref _successfulRequestCount);
                if (successfulCount == 1 || successfulCount % 100 == 0)
                {
                    Log(
                        "curl proxy served " + successfulCount.ToString(CultureInfo.InvariantCulture) +
                        " request(s); latest " + request.Method + " " + DescribeTarget(target) +
                        " -> " + response.StatusCode.ToString(CultureInfo.InvariantCulture) +
                        "; automatic retries " +
                        Interlocked.Read(ref _transientRetryCount).ToString(CultureInfo.InvariantCulture) +
                        ", returned failures " +
                        Interlocked.Read(ref _upstreamFailureCount).ToString(CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                TryDeleteFile(bodyPath);
                TryDeleteFile(headerPath);
            }
        }

        private CurlResult ExecuteCurl(
            RegisteredTarget target,
            ProxyRequest request,
            string bodyPath,
            string headerPath,
            CancellationToken cancellationToken)
        {
            RegisteredTarget currentTarget = target;
            for (int redirectCount = 0; redirectCount <= MaximumUpstreamRedirects; redirectCount++)
            {
                CurlResult result = ExecuteCurlWithRetry(
                    currentTarget,
                    request,
                    bodyPath,
                    headerPath,
                    cancellationToken);
                if (result.ExitCode != 0)
                {
                    return result;
                }

                UpstreamResponse response;
                if (!TryReadUpstreamResponse(headerPath, result.HttpStatusCode, out response))
                {
                    return result;
                }

                string location;
                response.Headers.TryGetValue("Location", out location);
                string redirectedUrl;
                if (!TryResolveUpstreamRedirect(
                        response.StatusCode,
                        currentTarget.UpstreamUrl,
                        location,
                        out redirectedUrl))
                {
                    return result;
                }

                if (redirectCount == MaximumUpstreamRedirects)
                {
                    return result;
                }

                TryDeleteFile(bodyPath);
                TryDeleteFile(headerPath);
                currentTarget = new RegisteredTarget(
                    target.Id,
                    redirectedUrl,
                    target.LocalPath);
            }

            throw new InvalidOperationException("curl redirect state is invalid.");
        }

        private CurlResult ExecuteCurlWithRetry(
            RegisteredTarget target,
            ProxyRequest request,
            string bodyPath,
            string headerPath,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    TryDeleteFile(bodyPath);
                    TryDeleteFile(headerPath);
                    if (cancellationToken.WaitHandle.WaitOne(1000))
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                CurlResult result = ExecuteCurlOnce(
                    target,
                    request,
                    bodyPath,
                    headerPath,
                    cancellationToken);
                if (result.ExitCode == 0 ||
                    attempt > 0 ||
                    !IsTransientCurlExitCode(result.ExitCode))
                {
                    return result;
                }

                long retryCount = Interlocked.Increment(ref _transientRetryCount);
                if (ShouldLogRepeatedEvent(retryCount, 25))
                {
                    Log(
                        "temporary upstream failure was retried automatically " +
                        "(count " + retryCount.ToString(CultureInfo.InvariantCulture) +
                        ", latest curl exit " +
                        result.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");
                }
            }

            throw new InvalidOperationException("curl retry state is invalid.");
        }

        internal static bool TryResolveUpstreamRedirect(
            int statusCode,
            string currentUrl,
            string location,
            out string redirectedUrl)
        {
            redirectedUrl = null;
            if (statusCode != 300 && statusCode != 301 && statusCode != 302 &&
                statusCode != 303 && statusCode != 307 && statusCode != 308)
            {
                return false;
            }

            Uri current;
            Uri redirected;
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out current) ||
                string.IsNullOrWhiteSpace(location) ||
                !Uri.TryCreate(current, location.Trim(), out redirected) ||
                (redirected.Scheme != Uri.UriSchemeHttp && redirected.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            redirectedUrl = redirected.AbsoluteUri;
            return true;
        }

        private CurlResult ExecuteCurlOnce(
            RegisteredTarget target,
            ProxyRequest request,
            string bodyPath,
            string headerPath,
            CancellationToken cancellationToken)
        {
            List<string> arguments = new List<string>();
            arguments.Add("-q");
            arguments.Add(_supportsSslRevokeBestEffort
                ? "--ssl-revoke-best-effort"
                : "--ssl-no-revoke");
            arguments.Add("--silent");
            arguments.Add("--show-error");
            arguments.Add("--connect-timeout");
            arguments.Add(CurlConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--max-time");
            arguments.Add(CurlTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--speed-time");
            arguments.Add(CurlLowSpeedTimeSeconds.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--speed-limit");
            arguments.Add(CurlLowSpeedBytesPerSecond.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--http1.1");
            arguments.Add("--proto");
            arguments.Add("=http,https");
            arguments.Add("--dump-header");
            arguments.Add(headerPath);
            arguments.Add("--output");
            arguments.Add(bodyPath);
            arguments.Add("--write-out");
            arguments.Add("%{http_code}");

            if (string.Equals(request.Method, "HEAD", StringComparison.Ordinal))
            {
                arguments.Add("--head");
            }

            AddForwardedHeaders(arguments, target.UpstreamUrl, request.Range);
            arguments.Add("--url");
            arguments.Add(target.UpstreamUrl);

            string joinedArguments = JoinArguments(arguments);
            if (joinedArguments.Length > 30000)
            {
                throw new InvalidOperationException("Forwarded media headers exceed the Windows command-line limit.");
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = _curlPath;
            startInfo.Arguments = joinedArguments;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.ASCII;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                if (!process.Start())
                {
                    throw new InvalidOperationException("Unable to start Windows curl.exe.");
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                try
                {
                    while (!process.WaitForExit(200))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    process.WaitForExit();
                }
                catch
                {
                    TryKill(process);
                    throw;
                }

                string output = stdout.GetAwaiter().GetResult();
                string error = stderr.GetAwaiter().GetResult();
                int statusCode;
                if (!TryParseCurlStatus(output, out statusCode))
                {
                    statusCode = 0;
                }

                return new CurlResult(
                    process.ExitCode,
                    statusCode,
                    SanitizeCurlError(error, target));
            }
        }

        private static bool IsTransientCurlExitCode(int exitCode)
        {
            switch (exitCode)
            {
                case 5:
                case 6:
                case 7:
                case 18:
                case 28:
                case 35:
                case 52:
                case 55:
                case 56:
                case 92:
                    return true;
                default:
                    return false;
            }
        }

        private void AddForwardedHeaders(
            List<string> arguments,
            string upstreamUrl,
            string range)
        {
            MediaRequestHeaders forwarded = _headers.CreateSafeProjection(upstreamUrl);
            AddHeaderArgument(arguments, "Referer", forwarded.Referer);
            AddHeaderArgument(arguments, "User-Agent", forwarded.UserAgent);
            AddHeaderArgument(arguments, "Origin", forwarded.Origin);
            AddHeaderArgument(arguments, "Cookie", forwarded.Cookie);
            AddHeaderArgument(arguments, "Authorization", forwarded.Authorization);

            IReadOnlyDictionary<string, string> additional = forwarded.AdditionalHeaders;
            if (additional != null)
            {
                foreach (KeyValuePair<string, string> header in additional)
                {
                    if (BlockedForwardedHeaderNames.Contains(header.Key) ||
                        IsFixedHeaderName(header.Key))
                    {
                        continue;
                    }

                    AddHeaderArgument(arguments, header.Key, header.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(range))
            {
                AddHeaderArgument(arguments, "Range", range);
            }
        }

        private static void AddHeaderArgument(
            List<string> arguments,
            string name,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!IsValidHeaderName(name) || !IsValidHeaderValue(value))
            {
                throw new InvalidOperationException("A captured media header is not safe to forward.");
            }

            arguments.Add("--header");
            arguments.Add(name + ": " + value.Trim());
        }

        private bool TryResolveTarget(string rawPath, out RegisteredTarget target)
        {
            target = null;
            if (string.IsNullOrEmpty(rawPath) ||
                rawPath.Length > 256 ||
                rawPath.IndexOf('\\') >= 0 ||
                rawPath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            string prefix = "/" + _pathToken + "/";
            if (!rawPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string fileName = rawPath.Substring(prefix.Length);
            if (fileName.Length < 32 || fileName.IndexOf('/') >= 0)
            {
                return false;
            }

            string id = fileName.Substring(0, 32);
            Guid ignored;
            if (!Guid.TryParseExact(id, "N", out ignored) ||
                !_targets.TryGetValue(id, out target) ||
                !string.Equals(rawPath, target.LocalPath, StringComparison.Ordinal))
            {
                target = null;
                return false;
            }

            return true;
        }

        private static bool TryReadRequest(
            Stream stream,
            out ProxyRequest request,
            out int errorStatus,
            out string errorMessage)
        {
            request = null;
            errorStatus = 400;
            errorMessage = "Invalid HTTP request.";

            using (StreamReader reader = new StreamReader(
                stream,
                Encoding.ASCII,
                false,
                4096,
                true))
            {
                string requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine) ||
                    requestLine.Length > MaximumRequestLineLength)
                {
                    return false;
                }

                string[] parts = requestLine.Split(' ');
                if (parts.Length != 3 ||
                    (!string.Equals(parts[0], "GET", StringComparison.Ordinal) &&
                     !string.Equals(parts[0], "HEAD", StringComparison.Ordinal)) ||
                    (!string.Equals(parts[2], "HTTP/1.1", StringComparison.Ordinal) &&
                     !string.Equals(parts[2], "HTTP/1.0", StringComparison.Ordinal)))
                {
                    errorStatus = 405;
                    errorMessage = "Only GET and HEAD are supported.";
                    return false;
                }

                string requestTarget = parts[1];
                if (!requestTarget.StartsWith("/", StringComparison.Ordinal) ||
                    requestTarget.StartsWith("//", StringComparison.Ordinal) ||
                    ContainsControlCharacter(requestTarget))
                {
                    return false;
                }

                int queryIndex = requestTarget.IndexOf('?');
                string rawPath = queryIndex >= 0
                    ? requestTarget.Substring(0, queryIndex)
                    : requestTarget;

                string range = null;
                int totalBytes = requestLine.Length + 2;
                int headerCount = 0;
                while (true)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                    {
                        return false;
                    }

                    totalBytes += line.Length + 2;
                    if (totalBytes > MaximumRequestHeaderBytes ||
                        ++headerCount > MaximumRequestHeaderCount)
                    {
                        errorStatus = 431;
                        errorMessage = "Request headers are too large.";
                        return false;
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    if (line[0] == ' ' || line[0] == '\t')
                    {
                        return false;
                    }

                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        return false;
                    }

                    string name = line.Substring(0, colon);
                    string value = line.Substring(colon + 1).Trim();
                    if (!IsValidHeaderName(name) || !IsValidHeaderValue(value))
                    {
                        return false;
                    }

                    if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase))
                    {
                        if (range != null || !IsValidSingleByteRange(value))
                        {
                            errorStatus = 416;
                            errorMessage = "Only one byte range is supported.";
                            return false;
                        }

                        range = value;
                    }
                }

                request = new ProxyRequest(parts[0], rawPath, range);
                return true;
            }
        }

        private static bool TryReadUpstreamResponse(
            string headerPath,
            int curlStatusCode,
            out UpstreamResponse response)
        {
            response = null;
            if (!File.Exists(headerPath))
            {
                return false;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(headerPath, Encoding.GetEncoding(28591));
            }
            catch
            {
                return false;
            }

            UpstreamResponse current = null;
            UpstreamResponse last = null;
            foreach (string rawLine in lines)
            {
                string line = rawLine ?? string.Empty;
                if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    int status;
                    string reason;
                    if (TryParseStatusLine(line, out status, out reason))
                    {
                        current = new UpstreamResponse(status, reason);
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    if (current != null)
                    {
                        last = current;
                        current = null;
                    }

                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (IsValidHeaderName(name) && IsValidHeaderValue(value))
                {
                    current.Headers[name] = value;
                }
            }

            if (current != null)
            {
                last = current;
            }

            if (last == null && curlStatusCode >= 100 && curlStatusCode <= 599)
            {
                last = new UpstreamResponse(curlStatusCode, GetReasonPhrase(curlStatusCode));
            }

            if (last == null || last.StatusCode < 100 || last.StatusCode > 599)
            {
                return false;
            }

            response = last;
            return true;
        }

        private static void SendUpstreamResponse(
            Stream stream,
            ProxyRequest request,
            UpstreamResponse response,
            string bodyPath,
            CancellationToken cancellationToken)
        {
            bool isHead = string.Equals(request.Method, "HEAD", StringComparison.Ordinal);
            long bodyLength = File.Exists(bodyPath) ? new FileInfo(bodyPath).Length : 0;
            bool responseMayHaveBody = response.StatusCode != 204 && response.StatusCode != 304;

            StringBuilder headers = new StringBuilder();
            headers.Append("HTTP/1.1 ");
            headers.Append(response.StatusCode.ToString(CultureInfo.InvariantCulture));
            headers.Append(' ');
            headers.Append(SanitizeReasonPhrase(response.ReasonPhrase, response.StatusCode));
            headers.Append("\r\n");

            string contentType;
            if (response.Headers.TryGetValue("Content-Type", out contentType) &&
                IsValidHeaderValue(contentType))
            {
                headers.Append("Content-Type: ");
                headers.Append(contentType);
                headers.Append("\r\n");
            }
            else
            {
                headers.Append("Content-Type: application/octet-stream\r\n");
            }

            if (isHead)
            {
                string upstreamLength;
                long parsedLength;
                if (response.Headers.TryGetValue("Content-Length", out upstreamLength) &&
                    long.TryParse(
                        upstreamLength,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out parsedLength) &&
                    parsedLength >= 0)
                {
                    headers.Append("Content-Length: ");
                    headers.Append(parsedLength.ToString(CultureInfo.InvariantCulture));
                    headers.Append("\r\n");
                }
            }
            else
            {
                headers.Append("Content-Length: ");
                headers.Append((responseMayHaveBody ? bodyLength : 0).ToString(CultureInfo.InvariantCulture));
                headers.Append("\r\n");
            }

            AppendPassthroughHeader(headers, response.Headers, "Content-Range");
            AppendPassthroughHeader(headers, response.Headers, "Accept-Ranges");
            AppendPassthroughHeader(headers, response.Headers, "ETag");
            AppendPassthroughHeader(headers, response.Headers, "Last-Modified");
            headers.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (isHead || !responseMayHaveBody || bodyLength == 0)
            {
                stream.Flush();
                return;
            }

            using (FileStream body = new FileStream(
                bodyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[65536];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = body.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    stream.Write(buffer, 0, read);
                }
            }

            stream.Flush();
        }

        private static void AppendPassthroughHeader(
            StringBuilder builder,
            IDictionary<string, string> upstreamHeaders,
            string name)
        {
            string value;
            if (!upstreamHeaders.TryGetValue(name, out value) ||
                !IsValidHeaderValue(value))
            {
                return;
            }

            builder.Append(name);
            builder.Append(": ");
            builder.Append(value);
            builder.Append("\r\n");
        }

        private static void SendError(
            Stream stream,
            int statusCode,
            string reasonPhrase,
            string message)
        {
            string safeMessage = string.IsNullOrWhiteSpace(message)
                ? "Media proxy request failed."
                : message;
            byte[] body = Encoding.UTF8.GetBytes(safeMessage + "\r\n");
            string response =
                "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " +
                SanitizeReasonPhrase(reasonPhrase, statusCode) + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headers = Encoding.ASCII.GetBytes(response);
            stream.Write(headers, 0, headers.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static void TrySendError(
            TcpClient client,
            int statusCode,
            string reasonPhrase,
            string message)
        {
            try
            {
                if (client != null && client.Connected)
                {
                    SendError(client.GetStream(), statusCode, reasonPhrase, message);
                }
            }
            catch
            {
            }
        }

        private static bool IsCurlAvailable(
            out string curlPath,
            out string errorMessage,
            out bool supportsSslRevokeBestEffort)
        {
            curlPath = GetCurlPath();
            errorMessage = null;
            supportsSslRevokeBestEffort = false;
            if (string.IsNullOrWhiteSpace(curlPath) || !File.Exists(curlPath))
            {
                errorMessage = "Windows System32\\curl.exe is not available.";
                return false;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = curlPath;
                startInfo.Arguments = "--version";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        errorMessage = "Windows curl.exe could not be started.";
                        return false;
                    }

                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        TryKill(process);
                        errorMessage = "Windows curl.exe availability check timed out.";
                        return false;
                    }

                    process.WaitForExit();
                    string output = stdout.GetAwaiter().GetResult();
                    stderr.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 ||
                        output.IndexOf("curl ", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        errorMessage = "Windows curl.exe failed its availability check.";
                        return false;
                    }
                }

                supportsSslRevokeBestEffort = CurlSupportsOption(
                    curlPath,
                    "--ssl-revoke-best-effort");
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Windows curl.exe is unavailable: " + exception.Message;
                return false;
            }
        }

        private static bool CurlSupportsOption(string curlPath, string option)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = curlPath;
                startInfo.Arguments = "--help all";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        TryKill(process);
                        return false;
                    }

                    process.WaitForExit();
                    string output = stdout.GetAwaiter().GetResult();
                    stderr.GetAwaiter().GetResult();
                    return process.ExitCode == 0 &&
                           output.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetCurlPath()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windows))
            {
                return null;
            }

            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                string nativePath = Path.Combine(windows, "Sysnative", "curl.exe");
                if (File.Exists(nativePath))
                {
                    return nativePath;
                }
            }

            return Path.Combine(windows, "System32", "curl.exe");
        }

        private static string CreateRandomToken()
        {
            byte[] bytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return result.ToString();
        }

        private static string GetSafeExtension(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return ".bin";
            }

            int slash = absolutePath.LastIndexOf('/');
            int dot = absolutePath.LastIndexOf('.');
            if (dot <= slash || dot + 1 >= absolutePath.Length ||
                absolutePath.Length - dot > 12)
            {
                return ".bin";
            }

            for (int i = dot + 1; i < absolutePath.Length; i++)
            {
                char character = absolutePath[i];
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9')))
                {
                    return ".bin";
                }
            }

            return absolutePath.Substring(dot).ToLowerInvariant();
        }

        private static bool IsFixedHeaderName(string name)
        {
            return string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidHeaderName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128)
            {
                return false;
            }

            foreach (char character in name)
            {
                bool valid =
                    (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '!' || character == '#' || character == '$' ||
                    character == '%' || character == '&' || character == '\'' ||
                    character == '*' || character == '+' || character == '-' ||
                    character == '.' || character == '^' || character == '_' ||
                    character == '`' || character == '|' || character == '~';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidHeaderValue(string value)
        {
            if (value == null || value.Length > MaximumForwardedHeaderValueLength)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (character == '\r' || character == '\n' || character == '\0' ||
                    (character < 0x20 && character != '\t') || character == 0x7f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidSingleByteRange(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
                !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string range = value.Substring(6);
            int hyphen = range.IndexOf('-');
            if (hyphen < 0 || hyphen != range.LastIndexOf('-'))
            {
                return false;
            }

            string start = range.Substring(0, hyphen);
            string end = range.Substring(hyphen + 1);
            if (start.Length == 0 && end.Length == 0)
            {
                return false;
            }

            return IsDigitsOnly(start) && IsDigitsOnly(end);
        }

        private static bool IsDigitsOnly(string value)
        {
            foreach (char character in value)
            {
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsControlCharacter(string value)
        {
            foreach (char character in value)
            {
                if (character < 0x20 || character == 0x7f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseStatusLine(
            string line,
            out int statusCode,
            out string reasonPhrase)
        {
            statusCode = 0;
            reasonPhrase = null;
            string[] parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !int.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out statusCode) ||
                statusCode < 100 || statusCode > 599)
            {
                return false;
            }

            reasonPhrase = parts.Length >= 3 ? parts[2].Trim() : GetReasonPhrase(statusCode);
            return true;
        }

        private static bool TryParseCurlStatus(string output, out int statusCode)
        {
            statusCode = 0;
            string text = (output ?? string.Empty).Trim();
            if (text.Length < 3)
            {
                return false;
            }

            string candidate = text.Substring(text.Length - 3);
            return int.TryParse(
                       candidate,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out statusCode) &&
                   statusCode >= 100 && statusCode <= 599;
        }

        private static string SanitizeReasonPhrase(string value, int statusCode)
        {
            if (string.IsNullOrWhiteSpace(value) || !IsValidHeaderValue(value))
            {
                return GetReasonPhrase(statusCode);
            }

            StringBuilder result = new StringBuilder();
            foreach (char character in value.Trim())
            {
                if (character >= 0x20 && character <= 0x7e)
                {
                    result.Append(character);
                }
            }

            return result.Length == 0 ? GetReasonPhrase(statusCode) : result.ToString();
        }

        private static string GetReasonPhrase(int statusCode)
        {
            switch (statusCode)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 303: return "See Other";
                case 304: return "Not Modified";
                case 307: return "Temporary Redirect";
                case 308: return "Permanent Redirect";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 416: return "Range Not Satisfiable";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "HTTP Response";
            }
        }

        private static string DescribeTarget(RegisteredTarget target)
        {
            Uri uri;
            if (target != null && Uri.TryCreate(target.UpstreamUrl, UriKind.Absolute, out uri))
            {
                string fileName = Path.GetFileName(uri.AbsolutePath);
                return uri.Host + "/" + (string.IsNullOrEmpty(fileName) ? "media" : fileName);
            }

            return "media";
        }

        private static string SanitizeLogText(string value)
        {
            string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 400)
            {
                text = text.Substring(0, 400) + "...";
            }

            return text;
        }

        private string SanitizeCurlError(string value, RegisteredTarget target)
        {
            string text = value ?? string.Empty;
            if (target != null)
            {
                text = RedactSecret(text, target.UpstreamUrl, "[media URL]");
            }

            text = RedactSecret(text, _headers.SourceUrl, "[source URL]");
            text = RedactSecret(text, _headers.Cookie, "[cookie]");
            text = RedactSecret(text, _headers.Authorization, "[authorization]");

            IReadOnlyDictionary<string, string> additional = _headers.AdditionalHeaders;
            if (additional != null)
            {
                foreach (KeyValuePair<string, string> header in additional)
                {
                    text = RedactSecret(
                        text,
                        header.Value,
                        "[" + header.Key + " hidden]");
                }
            }

            return SanitizeLogText(text);
        }

        private static string RedactSecret(string text, string secret, string replacement)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret))
            {
                return text;
            }

            return text.Replace(secret, replacement);
        }

        private void Log(string message)
        {
            Action<string> callback = _log;
            if (callback == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                callback("[curl proxy] " + message);
            }
            catch
            {
            }
        }

        internal static bool ShouldLogRepeatedEvent(long count, long interval)
        {
            return count == 1 || (interval > 0 && count % interval == 0);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException("CurlMediaProxy");
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }
        }

        private static void TryCloseClient(TcpClient client)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                Socket socket = client.Client;
                if (socket != null)
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch
            {
            }

            try
            {
                client.Close();
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteOwnedWorkDirectory(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name) ||
                    !name.StartsWith("curl_proxy_", StringComparison.Ordinal) ||
                    name.Length != "curl_proxy_".Length + 32)
                {
                    return;
                }

                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    string extension = Path.GetExtension(file);
                    if (string.Equals(extension, ".body", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".headers", StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(file);
                    }
                }

                if (Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory, false);
                }
            }
            catch
            {
            }
        }

        private static string JoinArguments(IEnumerable<string> arguments)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(argument));
            }

            return builder.ToString();
        }

        private static string QuoteArgument(string argument)
        {
            if (argument == null)
            {
                return "\"\"";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(character);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        private sealed class RegisteredTarget
        {
            public RegisteredTarget(string id, string upstreamUrl, string localPath)
            {
                Id = id;
                UpstreamUrl = upstreamUrl;
                LocalPath = localPath;
            }

            public string Id { get; private set; }
            public string UpstreamUrl { get; private set; }
            public string LocalPath { get; private set; }
        }

        private sealed class ProxyRequest
        {
            public ProxyRequest(string method, string rawPath, string range)
            {
                Method = method;
                RawPath = rawPath;
                Range = range;
            }

            public string Method { get; private set; }
            public string RawPath { get; private set; }
            public string Range { get; private set; }
        }

        private sealed class CurlResult
        {
            public CurlResult(
                int exitCode,
                int httpStatusCode,
                string sanitizedStandardError)
            {
                ExitCode = exitCode;
                HttpStatusCode = httpStatusCode;
                SanitizedStandardError = sanitizedStandardError;
            }

            public int ExitCode { get; private set; }
            public int HttpStatusCode { get; private set; }
            public string SanitizedStandardError { get; private set; }
        }

        private sealed class UpstreamResponse
        {
            public UpstreamResponse(int statusCode, string reasonPhrase)
            {
                StatusCode = statusCode;
                ReasonPhrase = reasonPhrase;
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public int StatusCode { get; private set; }
            public string ReasonPhrase { get; private set; }
            public Dictionary<string, string> Headers { get; private set; }
        }
    }
}
