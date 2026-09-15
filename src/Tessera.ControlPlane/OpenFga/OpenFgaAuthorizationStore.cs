using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tessera.ControlPlane.OpenFga;

/// <summary>
/// IAuthorizationStore backed by a real OpenFGA instance, talking the plain
/// REST API over HttpClient rather than the official SDK. The SDK is a
/// NuGet package and this adapter needs to build in environments where
/// NuGet restore isn't available; the REST surface used here is four
/// endpoints (write, read, check) which is small enough to own directly
/// and verify by reading the wire format rather than trusting a dependency.
///
/// The caller owns the HttpClient: set BaseAddress to the FGA API URL,
/// with a trailing slash (see the constructor, this is enforced, a
/// missing trailing slash silently drops the last path segment under
/// HttpClient's URI-combining rules), and configure auth (bearer token,
/// mTLS, whatever the deployment needs) on it before passing it in. This
/// class only knows how to talk to the store once it's reachable, not
/// how to authenticate to it.
///
/// This store's ReadTuplesForClientAsync runs inside KillSwitchService's
/// read-delete loop while the per-client lock is held, so a hang here
/// stalls the kill switch, not just one check. That's why pagination is
/// bounded below rather than trusting the server to terminate cleanly.
/// </summary>
public sealed class OpenFgaAuthorizationStore : IAuthorizationStore
{
    // OpenFGA's documented cap on tuple operations per write/delete
    // request. Tessera's own README already calls this out as something
    // a production adapter has to handle; this is that handling.
    private const int MaxTuplesPerRequest = 100;
    private const int DefaultReadPageSize = 100;

    // A defensive ceiling, not an expected case: a well-behaved server
    // terminates the continuation token on its own. This exists so a
    // server bug (a token that never empties) turns into a clear
    // exception instead of an infinite loop holding KillSwitchService's
    // per-client lock forever.
    private const int MaxReadPages = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _storeId;
    private readonly string? _authorizationModelId;

    public OpenFgaAuthorizationStore(HttpClient httpClient, string storeId, string? authorizationModelId = null)
    {
        if (httpClient.BaseAddress is null)
            throw new ArgumentException("HttpClient.BaseAddress must be set to the FGA API URL before use.", nameof(httpClient));
        if (!httpClient.BaseAddress.AbsoluteUri.EndsWith('/'))
            throw new ArgumentException(
                "HttpClient.BaseAddress must end with a trailing slash (e.g. \"https://api.fga.example/\"). " +
                "Without it, HttpClient's URI-combining rules silently drop the last segment of the base " +
                "path when this class appends a relative request path, sending requests to the wrong endpoint.",
                nameof(httpClient));
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("storeId is required.", nameof(storeId));

        _http = httpClient;
        _storeId = storeId;
        _authorizationModelId = authorizationModelId;
    }

    public async Task WriteTuplesAsync(IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        if (tuples.Count == 0) return;

        var committed = 0;
        foreach (var chunk in tuples.Chunk(MaxTuplesPerRequest))
        {
            var keys = chunk.Select(ToWireKey).ToList();
            var body = new WriteRequest(new WriteTuples(keys), Deletes: null, _authorizationModelId);
            try
            {
                await SendAsync(HttpMethod.Post, $"stores/{_storeId}/write", body, ct).ConfigureAwait(false);
                committed += chunk.Length;
            }
            catch (Exception ex) when (committed > 0)
            {
                // Earlier chunks already committed at OpenFGA. Don't let
                // the caller believe the whole batch is a clean no-op.
                throw new OpenFgaPartialBatchException(committed, tuples.Count, ex);
            }
        }
    }

    public async Task DeleteTuplesAsync(IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        if (tuples.Count == 0) return;

        var committed = 0;
        foreach (var chunk in tuples.Chunk(MaxTuplesPerRequest))
        {
            var keys = chunk.Select(ToWireKey).ToList();
            var body = new WriteRequest(Writes: null, new WriteTuples(keys), _authorizationModelId);
            try
            {
                await SendAsync(HttpMethod.Post, $"stores/{_storeId}/write", body, ct).ConfigureAwait(false);
                committed += chunk.Length;
            }
            catch (Exception ex) when (committed > 0)
            {
                throw new OpenFgaPartialBatchException(committed, tuples.Count, ex);
            }
        }
    }

    public async Task<IReadOnlyList<RelationTuple>> ReadTuplesForClientAsync(string clientRef, CancellationToken ct = default)
    {
        var user = $"client:{clientRef}";
        var results = new List<RelationTuple>();
        string? continuationToken = null;
        var page = 0;

        do
        {
            page++;
            if (page > MaxReadPages)
                throw new InvalidOperationException(
                    $"OpenFGA read for client {clientRef} did not terminate within {MaxReadPages} pages. " +
                    "Aborting rather than looping or growing memory without bound; this usually means a " +
                    "server that isn't emptying its continuation token, not a client with a legitimately huge tuple set.");

            var body = new ReadRequest(new OpenFgaTupleKeyFilter(user, null, null), DefaultReadPageSize, continuationToken);
            var response = await SendAsync<ReadRequest, ReadResponse>(HttpMethod.Post, $"stores/{_storeId}/read", body, ct).ConfigureAwait(false);

            foreach (var entry in response.Tuples)
                results.Add(new RelationTuple(entry.Key.User, entry.Key.Relation, entry.Key.Object));

            continuationToken = string.IsNullOrEmpty(response.ContinuationToken) ? null : response.ContinuationToken;
        } while (continuationToken is not null);

        return results;
    }

    public async Task<bool> CheckAsync(string user, string relation, string @object, CancellationToken ct = default)
    {
        var body = new CheckRequest(new OpenFgaTupleKeyFilter(user, relation, @object), _authorizationModelId);
        var response = await SendAsync<CheckRequest, CheckResponse>(HttpMethod.Post, $"stores/{_storeId}/check", body, ct).ConfigureAwait(false);
        return response.Allowed;
    }

    private static OpenFgaTupleKey ToWireKey(RelationTuple t) => new(t.User, t.Relation, t.Object);

    // Write and delete calls: fire the request, throw on failure, don't
    // care about the response body (OpenFGA returns {} on success).
    private async Task SendAsync<TRequest>(HttpMethod method, string path, TRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
            ThrowApiException(response.StatusCode, raw);
        }
    }

    // Check and read calls: fire the request, throw on failure, parse and
    // return the body on success. The body is read to a string exactly
    // once here, then parsed from that string, rather than letting
    // ReadFromJsonAsync consume the response stream directly, so a
    // malformed-response exception can always carry the raw text that
    // failed to parse instead of trying to re-read an already-consumed
    // stream.
    private async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            ThrowApiException(response.StatusCode, raw);

        try
        {
            var parsed = JsonSerializer.Deserialize<TResponse>(raw, JsonOptions);
            return parsed ?? throw new OpenFgaApiException(response.StatusCode, apiCode: "empty_response", apiMessage: "OpenFGA returned a success status with an empty body.", raw);
        }
        catch (JsonException ex)
        {
            throw new OpenFgaApiException(response.StatusCode, apiCode: "unparsable_response", apiMessage: ex.Message, raw);
        }
    }

    private static void ThrowApiException(HttpStatusCode statusCode, string raw)
    {
        OpenFgaErrorBody? errorBody = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                errorBody = JsonSerializer.Deserialize<OpenFgaErrorBody>(raw, JsonOptions);
            }
            catch (JsonException)
            {
                // Body isn't the shape we expect. Fall through and report
                // it raw rather than losing it.
            }
        }

        throw new OpenFgaApiException(statusCode, errorBody?.Code, errorBody?.Message, raw);
    }

    // Best-effort diagnostic read: if the body itself can't be read, we
    // still want to throw OpenFgaApiException with the status code that's
    // already known, not lose that in a secondary exception. The one
    // exception that must NOT be downgraded this way is a caller-driven
    // cancellation, that's not a body-read failure, it's the caller
    // asking to stop, and it needs to surface as OperationCanceledException
    // so callers that distinguish "timed out" from "API rejected" don't
    // misclassify a cancellation as a hard failure.
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
