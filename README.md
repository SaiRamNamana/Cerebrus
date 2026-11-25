# Cerebrus

Cerebrus is a local HTTP reverse proxy written in C# for .NET 10. It forwards requests to two upstream servers, skips upstreams that fail TCP health checks, and exposes request counters and upstream status as JSON. The .NET project is named `Cerberus.Core`.

## Quick start

Install the .NET 10 SDK and Python 3, then clone the repository.

```sh
git clone https://github.com/SaiRamNamana/Cerebrus.git
cd Cerebrus
dotnet build Cerberus.Core.csproj
```

Start two small upstream servers in separate terminals. Each serves a different response so you can see which one handled a request.

```sh
upstream_dir=$(mktemp -d)
printf 'Upstream 9001\n' > "$upstream_dir/index.html"
python3 -m http.server 9001 --bind 127.0.0.1 --directory "$upstream_dir"
```

```sh
upstream_dir=$(mktemp -d)
printf 'Upstream 9002\n' > "$upstream_dir/index.html"
python3 -m http.server 9002 --bind 127.0.0.1 --directory "$upstream_dir"
```

From the repository root, start the proxy in a third terminal.

```sh
dotnet run --project Cerberus.Core.csproj
```

Send two requests through the proxy, then inspect the metrics.

```sh
curl -fsS http://127.0.0.1:8080/
curl -fsS http://127.0.0.1:8080/
curl -fsS http://127.0.0.1:8080/metrics
```

With a fresh proxy and both upstreams running, the requests return `Upstream 9001` and `Upstream 9002` in order. Stop one upstream and allow the next health-check cycle to finish. Subsequent requests go to the remaining upstream and the metrics show the stopped server as unhealthy.

## Request flow

1. The proxy accepts a TCP connection and parses the HTTP request line from its first read.
2. Requests whose target is exactly `/metrics` are handled locally, before filtering and rate limiting. An `OPTIONS` request receives `204 No Content` with CORS headers.
3. A basic web application firewall (WAF) checks the raw request target for a small set of literal strings. A match returns `403 Forbidden`.
4. The per-IP token bucket consumes one token per allowed request. An empty bucket returns `429 Too Many Requests`.
5. The load balancer picks the next upstream marked healthy in round-robin order. When no upstream is marked healthy, the proxy returns `503 Service Unavailable`.
6. The proxy sends the bytes from the initial client read to the selected upstream and relays the bytes from the first upstream read.

## Configuration

Defaults are defined in [Program.cs](Program.cs). Edit the source and restart the proxy to change them.

| Setting | Default |
| --- | --- |
| Proxy listener | `127.0.0.1:8080` |
| Upstream addresses | `127.0.0.1:9001`, `127.0.0.1:9002` |
| Health check | TCP connect, with a five-second delay between passes and a one-second timeout per upstream |
| Per-IP burst capacity | 5 requests |
| Per-IP refill rate | One token every two seconds |

Each upstream starts marked healthy. The first check runs after the initial five-second delay and updates the flag based on TCP reachability.

## Metrics

`GET /metrics` returns the following in-memory values. Counters reset on restart and exclude requests handled by the metrics endpoint itself.

| Field | Meaning |
| --- | --- |
| `totalRequests` | Requests counted before filtering, rate limiting, or upstream selection |
| `rateLimitedRequests` | Requests rejected by the per-IP token bucket |
| `wafBlockedRequests` | Requests rejected by the literal request-target filter |
| `successfulProxied` | Requests assigned an upstream. This counter increments before connecting to the upstream, so it measures selections rather than completed responses. |
| `upstreams` | Configured upstream addresses and their current `isHealthy` flags |

## Scope

The proxy is intended for local experimentation with small HTTP requests and responses. Each connection is handled with a single read of up to 4 KiB in each direction. TCP can split data across reads, so even a short message can arrive incomplete. The implementation uses plaintext HTTP, keeps the rate-limit and metrics state in memory, and uses a literal filter with limited coverage.
