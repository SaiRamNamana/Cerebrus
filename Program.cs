using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cerberus.Core
{
    // Global metrics aggregator using thread-safe counters
    public static class ProxyMetrics
    {
        private static long _totalRequests = 0;
        private static long _rateLimitedRequests = 0;
        private static long _wafBlockedRequests = 0;
        private static long _successfulProxied = 0;

        public static void IncrementTotal() => Interlocked.Increment(ref _totalRequests);
        public static void IncrementRateLimited() => Interlocked.Increment(ref _rateLimitedRequests);
        public static void IncrementWafBlocked() => Interlocked.Increment(ref _wafBlockedRequests);
        public static void IncrementSuccessful() => Interlocked.Increment(ref _successfulProxied);

        public static string GetMetricsJson(IEnumerable<BackendServer> servers)
        {
            var serverStatuses = new StringBuilder();
            foreach (var server in servers)
            {
                serverStatuses.Append($"{{\"address\":\"{server.Address}\",\"isHealthy\":{server.IsHealthy.ToString().ToLower()}}},");
            }
            if (serverStatuses.Length > 0) serverStatuses.Length--; // Remove trailing comma

            return "{\n" +
                   $"  \"totalRequests\": {_totalRequests},\n" +
                   $"  \"rateLimitedRequests\": {_rateLimitedRequests},\n" +
                   $"  \"wafBlockedRequests\": {_wafBlockedRequests},\n" +
                   $"  \"successfulProxied\": {_successfulProxied},\n" +
                   $"  \"upstreams\": [{serverStatuses}]\n" +
                   "}";
        }
    }

    public class TokenBucketRateLimiter
    {
        private class ClientBucket
        {
            public double Tokens { get; set; }
            public DateTime LastRefillTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, ClientBucket> _buckets = new();
        private readonly double _maxTokens;
        private readonly double _refillRatePerSecond;

        public TokenBucketRateLimiter(double maxTokens, double refillRatePerSecond)
        {
            _maxTokens = maxTokens;
            _refillRatePerSecond = refillRatePerSecond;
        }

        public bool AllowRequest(string clientIdentifier)
        {
            var now = DateTime.UtcNow;
            var bucket = _buckets.GetOrAdd(clientIdentifier, _ => new ClientBucket { Tokens = _maxTokens, LastRefillTime = now });

            lock (bucket)
            {
                var elapsedSeconds = (now - bucket.LastRefillTime).TotalSeconds;
                bucket.LastRefillTime = now;
                bucket.Tokens = Math.Min(_maxTokens, bucket.Tokens + (elapsedSeconds * _refillRatePerSecond));

                if (bucket.Tokens >= 1.0)
                {
                    bucket.Tokens -= 1.0;
                    return true;
                }
                return false;
            }
        }
    }

    public class BackendServer
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; }
        public bool IsHealthy { get; set; } = true;
        public string Address => $"{Host}:{Port}";
    }

    public class LoadBalancer
    {
        private readonly List<BackendServer> _servers;
        private int _currentIndex = -1;

        public LoadBalancer(List<BackendServer> servers) => _servers = servers;

        public BackendServer? GetNextHealthyServer()
        {
            int serverCount = _servers.Count;
            if (serverCount == 0) return null;

            for (int i = 0; i < serverCount; i++)
            {
                int nextIndex = Interlocked.Increment(ref _currentIndex) % serverCount;
                if (nextIndex < 0) nextIndex = Math.Abs(nextIndex);

                var server = _servers[nextIndex];
                if (server.IsHealthy) return server;
            }
            return null;
        }

        public IEnumerable<BackendServer> GetAllServers() => _servers;
    }

    class Program
    {
        private static readonly LoadBalancer _loadBalancer = new(new List<BackendServer>
        {
            new() { Host = "127.0.0.1", Port = 9001 },
            new() { Host = "127.0.0.1", Port = 9002 }
        });

        private static readonly TokenBucketRateLimiter _rateLimiter = new(maxTokens: 5, refillRatePerSecond: 0.5);

        static async Task Main(string[] args)
        {
            var ipAddress = IPAddress.Loopback;
            int port = 8080;
            
            var listener = new TcpListener(ipAddress, port);
            listener.Start();
            
            Console.WriteLine($"[Cerberus Gateway] Observability Engine live on {ipAddress}:{port}...");
            _ = StartHealthCheckerAsync(TimeSpan.FromSeconds(5));

            try
            {
                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task StartHealthCheckerAsync(TimeSpan interval)
        {
            while (true)
            {
                await Task.Delay(interval);
                foreach (var server in _loadBalancer.GetAllServers())
                {
                    bool reachable = false;
                    try
                    {
                        using var tcpClient = new TcpClient();
                        var connectTask = tcpClient.ConnectAsync(server.Host, server.Port);
                        if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask && tcpClient.Connected)
                        {
                            reachable = true;
                        }
                    }
                    catch { reachable = false; }

                    server.IsHealthy = reachable;
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    using NetworkStream clientStream = client.GetStream();
                    
                    byte[] buffer = new byte[4096];
                    int bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) return;

                    string rawRequest = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    ParsedHttpRequest request = ParseHttpRequest(rawRequest);

                    // Handle built-in /metrics admin endpoint for Angular Dashboard
                    // Handle built-in /metrics admin endpoint for Angular Dashboard
                    if (request.Path == "/metrics")
                    {
                        string httpResponse;

                        // Browsers send an OPTIONS preflight request before fetching cross-origin data
                        if (request.Method == "OPTIONS")
                        {
                            httpResponse = 
                                "HTTP/1.1 204 No Content\r\n" +
                                "Access-Control-Allow-Origin: *\r\n" +
                                "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
                                "Access-Control-Allow-Headers: *\r\n" +
                                "Connection: close\r\n\r\n";
                        }
                        else
                        {
                            string json = ProxyMetrics.GetMetricsJson(_loadBalancer.GetAllServers());
                            httpResponse = 
                                "HTTP/1.1 200 OK\r\n" +
                                "Content-Type: application/json; charset=utf-8\r\n" +
                                "Access-Control-Allow-Origin: *\r\n" +
                                $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n" +
                                "Connection: close\r\n\r\n" + 
                                json;
                        }
                        
                        byte[] respBytes = Encoding.UTF8.GetBytes(httpResponse);
                        await clientStream.WriteAsync(respBytes, 0, respBytes.Length);
                        
                        Console.WriteLine($"[Metrics Telemetry] Handled {request.Method} request from dashboard.");
                        return;
                    }

                    ProxyMetrics.IncrementTotal();

                    // WAF Filter
                    if (request.Path.Contains("UNION SELECT") || request.Path.Contains("../"))
                    {
                        ProxyMetrics.IncrementWafBlocked();
                        await SendErrorResponse(clientStream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 22\r\n\r\nBlocked by Cerberus WAF");
                        return;
                    }

                    // Rate Limiting
                    if (!_rateLimiter.AllowRequest(clientIp))
                    {
                        ProxyMetrics.IncrementRateLimited();
                        await SendErrorResponse(clientStream, "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 27\r\n\r\nRate limit exceeded. Slow down.");
                        return;
                    }

                    // Load Balancing
                    BackendServer? targetServer = _loadBalancer.GetNextHealthyServer();
                    if (targetServer == null)
                    {
                        await SendErrorResponse(clientStream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 31\r\n\r\nNo healthy upstreams available");
                        return;
                    }

                    ProxyMetrics.IncrementSuccessful();

                    using TcpClient upstreamClient = new TcpClient();
                    await upstreamClient.ConnectAsync(targetServer.Host, targetServer.Port);
                    using NetworkStream upstreamStream = upstreamClient.GetStream();

                    await upstreamStream.WriteAsync(buffer, 0, bytesRead);
                    await upstreamStream.FlushAsync();

                    byte[] upstreamBuffer = new byte[4096];
                    int upstreamBytesRead = await upstreamStream.ReadAsync(upstreamBuffer, 0, upstreamBuffer.Length);

                    await clientStream.WriteAsync(upstreamBuffer, 0, upstreamBytesRead);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Proxy Error]: {ex.Message}");
                }
            }
        }

        private static async Task SendErrorResponse(NetworkStream stream, string response)
        {
            byte[] errBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(errBytes, 0, errBytes.Length);
        }

        private static ParsedHttpRequest ParseHttpRequest(string rawRequest)
        {
            int firstLineEnd = rawRequest.IndexOf("\r\n");
            if (firstLineEnd == -1) firstLineEnd = rawRequest.IndexOf('\n');
            string requestLine = firstLineEnd != -1 ? rawRequest.Substring(0, firstLineEnd) : rawRequest;
            string[] parts = requestLine.Split(' ');

            return new ParsedHttpRequest
            {
                Method = parts.Length > 0 ? parts[0] : "UNKNOWN",
                Path = parts.Length > 1 ? parts[1] : "/",
                HttpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.1"
            };
        }
    }

    public class ParsedHttpRequest
    {
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string HttpVersion { get; set; } = string.Empty;
    }
}