using System.Net;
using System.Text;
using System.Text.Json;
using Tessera.ControlPlane;
using Tessera.ControlPlane.OpenFga;

var failures = new List<string>();

async Task Run(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures.Add(name);
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception($"assertion failed: {message}");
}

static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

static HttpClient MakeClient(FakeHandler handler) =>
    new(handler) { BaseAddress = new Uri("https://fga.test/") };

static int CountTupleKeys(string body, string field)
{
    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty(field, out var el)) return 0;
    return el.GetProperty("tuple_keys").GetArrayLength();
}

// --- write ---

await Run("Write_SendsSingleRequest_ForSmallBatch", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    await store.WriteTuplesAsync(new[] { new RelationTuple("client:a", "member", "api_group:g") });

    Check(handler.Requests.Count == 1, $"expected 1 request, got {handler.Requests.Count}");
    Check(handler.Requests[0].Path == "/stores/store1/write", $"unexpected path {handler.Requests[0].Path}");
    Check(handler.Requests[0].Body.Contains("\"writes\""), "expected a writes field in the body");
    Check(!handler.Requests[0].Body.Contains("\"deletes\""), "a write-only call should not send a deletes field");
    Check(handler.Requests[0].Body.Contains("client:a"), "expected the tuple's user in the body");
});

await Run("Write_ChunksAt100TuplesPerRequest", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    var tuples = Enumerable.Range(0, 150)
        .Select(i => new RelationTuple($"client:c{i}", "member", "api_group:g"))
        .ToList();

    await store.WriteTuplesAsync(tuples);

    Check(handler.Requests.Count == 2, $"expected 2 chunked requests for 150 tuples, got {handler.Requests.Count}");
    Check(CountTupleKeys(handler.Requests[0].Body, "writes") == 100, "first chunk should carry 100 tuples");
    Check(CountTupleKeys(handler.Requests[1].Body, "writes") == 50, "second chunk should carry the remaining 50");
});

await Run("Write_EmptyBatch_SendsNoRequest", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    await store.WriteTuplesAsync(Array.Empty<RelationTuple>());

    Check(handler.Requests.Count == 0, "an empty batch should never hit the network");
});

// --- delete ---

await Run("Delete_SendsCorrectPayload", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    await store.DeleteTuplesAsync(new[] { new RelationTuple("client:a", "member", "api_group:g") });

    Check(handler.Requests.Count == 1, $"expected 1 request, got {handler.Requests.Count}");
    Check(handler.Requests[0].Body.Contains("\"deletes\""), "expected a deletes field in the body");
    Check(!handler.Requests[0].Body.Contains("\"writes\""), "a delete-only call should not send a writes field");
});

// --- check ---

await Run("Check_ReturnsTrueWhenAllowed", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"allowed\":true}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    var allowed = await store.CheckAsync("client:a", "member", "api_group:g");

    Check(allowed, "expected allowed to be true");
    Check(handler.Requests[0].Path == "/stores/store1/check", $"unexpected path {handler.Requests[0].Path}");
});

await Run("Check_ReturnsFalseWhenDenied", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"allowed\":false}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    var allowed = await store.CheckAsync("client:a", "member", "api_group:g");

    Check(!allowed, "expected allowed to be false");
});

// --- read ---

await Run("Read_LoopsAcrossPagesUntilContinuationTokenIsEmpty", async () =>
{
    // Reads now go out once per known object type (api_group, then
    // api_endpoint, see KnownObjectTypes), each paginated on its own, so
    // branch on which type a given request is filtering by rather than
    // assuming one continuous page sequence.
    var handler = new FakeHandler((_, body, _) =>
    {
        if (body.Contains("\"object\":\"api_group:\""))
        {
            if (!body.Contains("tok1"))
            {
                Check(!body.Contains("continuation_token"), "the first api_group page request should not carry a continuation token");
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    "{\"tuples\":[{\"key\":{\"user\":\"client:a\",\"relation\":\"member\",\"object\":\"api_group:g1\"},\"timestamp\":\"2024-01-01T00:00:00Z\"}],\"continuation_token\":\"tok1\"}"));
            }
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                "{\"tuples\":[{\"key\":{\"user\":\"client:a\",\"relation\":\"member\",\"object\":\"api_group:g2\"},\"timestamp\":\"2024-01-01T00:00:00Z\"}],\"continuation_token\":\"\"}"));
        }

        Check(body.Contains("\"object\":\"api_endpoint:\""), $"expected a read filtered by api_group: or api_endpoint:, got {body}");
        return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"tuples\":[],\"continuation_token\":\"\"}"));
    });
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    var result = await store.ReadTuplesForClientAsync("a");

    Check(handler.Requests.Count == 3, $"expected 3 requests, 2 pages for api_group plus 1 for api_endpoint, got {handler.Requests.Count}");
    Check(result.Count == 2, $"expected tuples from both api_group pages, got {result.Count}");
});

await Run("Read_FiltersByCanonicalClientUser", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"tuples\":[]}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    await store.ReadTuplesForClientAsync("billing-reconciler");

    Check(handler.Requests.Count == 2, $"expected one read per known object type, got {handler.Requests.Count}");
    foreach (var req in handler.Requests)
        Check(req.Body.Contains("\"user\":\"client:billing-reconciler\""),
            "expected the read filter to use the canonical client: prefix, matching InMemoryAuthorizationStore's convention");
});

await Run("Read_FiltersByObjectType_BecauseOpenFgaRejectsAUserOnlyFilter", async () =>
{
    // Regression test for a real bug: OpenFGA's /read endpoint returns a
    // 400 ("the object type field is required") for a tuple_key that
    // only sets user. Found by running this adapter against a live
    // OpenFGA instance, not by any test, hence this test now.
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"tuples\":[]}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    await store.ReadTuplesForClientAsync("a");

    Check(handler.Requests.Any(r => r.Body.Contains("\"object\":\"api_group:\"")),
        "expected a read filtered to api_group: objects");
    Check(handler.Requests.Any(r => r.Body.Contains("\"object\":\"api_endpoint:\"")),
        "expected a read filtered to api_endpoint: objects");
    Check(handler.Requests.All(r => !r.Body.Contains("\"user\":\"client:a\"}")),
        "every read should carry an object filter alongside the user filter, not user alone");
});

// --- error handling ---

await Run("Error_NonSuccessStatus_ParsesApiCodeAndMessage", async () =>
{
    var handler = new FakeHandler((_, _, _) =>
        Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, "{\"code\":\"validation_error\",\"message\":\"invalid tuple\"}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    try
    {
        await store.WriteTuplesAsync(new[] { new RelationTuple("client:a", "member", "api_group:g") });
        throw new Exception("expected OpenFgaApiException, none was thrown");
    }
    catch (OpenFgaApiException ex)
    {
        Check(ex.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {ex.StatusCode}");
        Check(ex.ApiCode == "validation_error", $"expected validation_error, got {ex.ApiCode}");
        Check(ex.Message.Contains("invalid tuple"), "expected the API's message text to be in the exception message");
    }
});

await Run("Error_MalformedErrorBody_StillThrowsWithRawTextPreserved", async () =>
{
    var handler = new FakeHandler((_, _, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("not json at all {{{", Encoding.UTF8, "text/plain"),
        }));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    try
    {
        await store.CheckAsync("client:a", "member", "api_group:g");
        throw new Exception("expected OpenFgaApiException, none was thrown");
    }
    catch (OpenFgaApiException ex)
    {
        Check(ex.StatusCode == HttpStatusCode.InternalServerError, $"expected 500, got {ex.StatusCode}");
        Check(ex.ApiCode is null, "an unparsable body has no API code, should be null rather than a guess");
        Check(ex.Message.Contains("not json at all"), "the raw body should be preserved in the exception message, not silently dropped");
    }
});

await Run("Error_NetworkFailure_PropagatesRatherThanBeingSwallowed", async () =>
{
    var handler = new FakeHandler((_, _, _) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset by peer")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    try
    {
        await store.CheckAsync("client:a", "member", "api_group:g");
        throw new Exception("expected HttpRequestException, none was thrown");
    }
    catch (HttpRequestException)
    {
        // expected: a transport failure is not our exception type to wrap, it
        // should reach the caller as what it actually is.
    }
});

await Run("Cancellation_PropagatesOperationCanceledException", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"allowed\":true}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    try
    {
        await store.CheckAsync("client:a", "member", "api_group:g", cts.Token);
        throw new Exception("expected OperationCanceledException, none was thrown");
    }
    catch (OperationCanceledException)
    {
        // expected
    }
});

// --- config ---

await Run("Constructor_RejectsMissingBaseAddress", () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
    var client = new HttpClient(handler); // no BaseAddress set

    try
    {
        _ = new OpenFgaAuthorizationStore(client, "store1");
        throw new Exception("expected ArgumentException, none was thrown");
    }
    catch (ArgumentException)
    {
        // expected: fail fast on misconfiguration rather than on the first request
    }

    return Task.CompletedTask;
});

await Run("AuthorizationModelId_IncludedWhenProvided", async () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"allowed\":true}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1", authorizationModelId: "01HVMMBCMGZNT3SED4Z17ECXCA");

    await store.CheckAsync("client:a", "member", "api_group:g");

    Check(handler.Requests[0].Body.Contains("01HVMMBCMGZNT3SED4Z17ECXCA"), "expected the configured model id in the request body");
});

await Run("Constructor_RejectsBaseAddressWithoutTrailingSlash", () =>
{
    var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://fga.test/v1") }; // no trailing slash

    try
    {
        _ = new OpenFgaAuthorizationStore(client, "store1");
        throw new Exception("expected ArgumentException, none was thrown");
    }
    catch (ArgumentException)
    {
        // expected: a missing trailing slash silently drops the last
        // base-path segment under HttpClient's URI rules, this must be
        // caught at construction, not discovered as a wrong-endpoint bug
    }

    return Task.CompletedTask;
});

// --- partial batch failure ---

await Run("Write_PartialChunkFailure_ReportsHowManyTuplesAlreadyCommitted", async () =>
{
    var handler = new FakeHandler((_, _, callNumber) => callNumber == 1
        ? Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))
        : Task.FromResult(JsonResponse(HttpStatusCode.InternalServerError, "{\"code\":\"internal_error\",\"message\":\"boom\"}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    var tuples = Enumerable.Range(0, 150)
        .Select(i => new RelationTuple($"client:c{i}", "member", "api_group:g"))
        .ToList();

    try
    {
        await store.WriteTuplesAsync(tuples);
        throw new Exception("expected OpenFgaPartialBatchException, none was thrown");
    }
    catch (OpenFgaPartialBatchException ex)
    {
        Check(ex.TuplesSucceeded == 100, $"expected 100 tuples committed before the failing chunk, got {ex.TuplesSucceeded}");
        Check(ex.TuplesRequested == 150, $"expected 150 tuples requested in total, got {ex.TuplesRequested}");
        Check(ex.InnerException is OpenFgaApiException, "expected the underlying API failure as the inner exception");
    }
});

await Run("Write_FirstChunkFails_ThrowsPlainApiExceptionNotPartialBatch", async () =>
{
    // Nothing committed yet, so there's no partial state worth reporting,
    // the plain OpenFgaApiException should propagate unwrapped.
    var handler = new FakeHandler((_, _, _) =>
        Task.FromResult(JsonResponse(HttpStatusCode.InternalServerError, "{\"code\":\"internal_error\",\"message\":\"boom\"}")));
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    try
    {
        await store.WriteTuplesAsync(new[] { new RelationTuple("client:a", "member", "api_group:g") });
        throw new Exception("expected OpenFgaApiException, none was thrown");
    }
    catch (OpenFgaApiException)
    {
        // expected
    }
});

// --- pagination guard ---

await Run("Read_UnboundedContinuationToken_ThrowsRatherThanLoopingForever", async () =>
{
    var handler = new FakeHandler((_, _, callNumber) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
        $"{{\"tuples\":[],\"continuation_token\":\"tok{callNumber}\"}}"))); // never terminates
    var store = new OpenFgaAuthorizationStore(MakeClient(handler), "store1");

    try
    {
        await store.ReadTuplesForClientAsync("a");
        throw new Exception("expected InvalidOperationException, none was thrown");
    }
    catch (InvalidOperationException ex)
    {
        Check(ex.Message.Contains("did not terminate"), $"expected a clear pagination-guard message, got: {ex.Message}");
    }
});

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"{failures.Count} check(s) failed: {string.Join(", ", failures)}");
    return 1;
}

Console.WriteLine("All checks passed.");
return 0;

sealed class RecordedRequest
{
    public required HttpMethod Method { get; init; }
    public required string Path { get; init; }
    public required string Body { get; init; }
}

/// <summary>
/// A fake transport that records every request it sees and hands the
/// response, or exception, to a caller-supplied function. callNumber is
/// 1-based, so a multi-page test can tell its first call from its second.
/// </summary>
sealed class FakeHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();

    private readonly Func<HttpRequestMessage, string, int, Task<HttpResponseMessage>> _respond;
    private int _callCount;

    public FakeHandler(Func<HttpRequestMessage, string, int, Task<HttpResponseMessage>> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        _callCount++;
        Requests.Add(new RecordedRequest { Method = request.Method, Path = request.RequestUri!.AbsolutePath, Body = body });
        return await _respond(request, body, _callCount);
    }
}
