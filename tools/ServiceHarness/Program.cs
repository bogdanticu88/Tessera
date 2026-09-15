using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.ControlPlane;
using Tessera.Service.Auth;
using Tessera.Service.Endpoints;

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

// --- an independent HS256 encoder, written separately from
// Hs256JwtValidator's decoder, so a passing test is checking wire-format
// compatibility, not just "this class agrees with itself" ---

static string Base64UrlEncode(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string CreateToken(string issuer, string audience, string? subject, byte[] key, string alg = "HS256",
    TimeSpan? expiresIn = null, TimeSpan? notBeforeOffset = null, DateTimeOffset? now = null)
{
    var effectiveNow = now ?? DateTimeOffset.UtcNow;
    var header = JsonSerializer.Serialize(new { alg, typ = "JWT" });

    var payloadObj = new Dictionary<string, object?>
    {
        ["iss"] = issuer,
        ["aud"] = audience,
        ["exp"] = effectiveNow.Add(expiresIn ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
    };
    if (subject is not null) payloadObj["sub"] = subject;
    if (notBeforeOffset is not null) payloadObj["nbf"] = effectiveNow.Add(notBeforeOffset.Value).ToUnixTimeSeconds();

    var payload = JsonSerializer.Serialize(payloadObj);

    var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
    var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
    var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{headerB64}.{payloadB64}"));

    return $"{headerB64}.{payloadB64}.{Base64UrlEncode(signature)}";
}

var testKey = RandomNumberGenerator.GetBytes(32);
const string TestIssuer = "tessera-test";
const string TestAudience = "tessera-test-clients";

Hs256JwtValidator MakeValidator(TimeProvider? clock = null) => new(TestIssuer, TestAudience, testKey, clockSkew: TimeSpan.FromSeconds(5), clock: clock);

// ============================================================
// Hs256JwtValidator
// ============================================================

await Run("Jwt_ValidToken_Accepted", async () =>
{
    var token = CreateToken(TestIssuer, TestAudience, "operator-1", testKey);
    var result = MakeValidator().Validate(token);
    Check(result.IsValid, $"expected valid, got failure: {result.FailureReason}");
    Check(result.Subject == "operator-1", $"expected subject operator-1, got {result.Subject}");
    await Task.CompletedTask;
});

await Run("Jwt_TamperedSignature_Rejected", async () =>
{
    var token = CreateToken(TestIssuer, TestAudience, "operator-1", testKey);
    var parts = token.Split('.');
    var tamperedSig = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var tampered = $"{parts[0]}.{parts[1]}.{tamperedSig}";

    var result = MakeValidator().Validate(tampered);
    Check(!result.IsValid, "expected an invalid result for a tampered signature");
    Check(result.FailureReason == "invalid signature", $"expected 'invalid signature', got {result.FailureReason}");
    await Task.CompletedTask;
});

await Run("Jwt_TamperedPayload_Rejected", async () =>
{
    var token = CreateToken(TestIssuer, TestAudience, "operator-1", testKey);
    var parts = token.Split('.');
    var forgedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes($"{{\"iss\":\"{TestIssuer}\",\"aud\":\"{TestAudience}\",\"sub\":\"admin\",\"exp\":9999999999}}"));
    var forged = $"{parts[0]}.{forgedPayload}.{parts[2]}"; // old signature, new payload

    var result = MakeValidator().Validate(forged);
    Check(!result.IsValid, "a payload edited after signing must not validate under the original signature");
    await Task.CompletedTask;
});

await Run("Jwt_WrongIssuer_Rejected", async () =>
{
    var token = CreateToken("someone-else", TestAudience, "operator-1", testKey);
    var result = MakeValidator().Validate(token);
    Check(!result.IsValid && result.FailureReason == "issuer does not match", $"expected issuer mismatch, got {result.FailureReason}");
    await Task.CompletedTask;
});

await Run("Jwt_WrongAudience_Rejected", async () =>
{
    var token = CreateToken(TestIssuer, "someone-elses-clients", "operator-1", testKey);
    var result = MakeValidator().Validate(token);
    Check(!result.IsValid && result.FailureReason == "audience does not match", $"expected audience mismatch, got {result.FailureReason}");
    await Task.CompletedTask;
});

await Run("Jwt_AudienceAsArrayContainingExpected_Accepted", async () =>
{
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        iss = TestIssuer,
        aud = new[] { "someone-else", TestAudience },
        sub = "operator-1",
        exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
    })));
    var sig = Base64UrlEncode(HMACSHA256.HashData(testKey, Encoding.ASCII.GetBytes($"{header}.{payload}")));
    var token = $"{header}.{payload}.{sig}";

    var result = MakeValidator().Validate(token);
    Check(result.IsValid, $"expected an aud array containing the expected audience to validate, got: {result.FailureReason}");
    await Task.CompletedTask;
});

await Run("Jwt_Expired_Rejected", async () =>
{
    var token = CreateToken(TestIssuer, TestAudience, "operator-1", testKey, expiresIn: TimeSpan.FromMinutes(-10));
    var result = MakeValidator().Validate(token);
    Check(!result.IsValid && result.FailureReason == "token expired", $"expected expired, got {result.FailureReason}");
    await Task.CompletedTask;
});

await Run("Jwt_NotYetValid_Rejected", async () =>
{
    var token = CreateToken(TestIssuer, TestAudience, "operator-1", testKey, notBeforeOffset: TimeSpan.FromMinutes(10));
    var result = MakeValidator().Validate(token);
    Check(!result.IsValid && result.FailureReason == "token not yet valid", $"expected not-yet-valid, got {result.FailureReason}");
    await Task.CompletedTask;
});

await Run("Jwt_MissingSubject_Rejected", async () =>
{
    var token = CreateToken(TestIssuer, TestAudience, subject: null, key: testKey);
    var result = MakeValidator().Validate(token);
    Check(!result.IsValid, "a token with no sub claim has no operator identity for audit attribution and must be rejected");
    await Task.CompletedTask;
});

await Run("Jwt_NonStringAlg_RejectedNotThrown", async () =>
{
    // Pre-signature-check, reachable with zero valid signature: alg as a
    // JSON array rather than a string must fail cleanly, not throw
    // InvalidOperationException out of an unauthenticated auth gate.
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":[\"HS256\"],\"typ\":\"JWT\"}"));
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iss = TestIssuer, aud = TestAudience, sub = "operator-1", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })));
    var token = $"{header}.{payload}.somesig";

    var result = MakeValidator().Validate(token);
    Check(!result.IsValid, "a non-string alg must be rejected, not thrown");
    await Task.CompletedTask;
});

await Run("Jwt_NonObjectPayload_RejectedNotThrown", async () =>
{
    // A bare JSON array is still valid JSON; TryGetProperty on it throws
    // InvalidOperationException unless guarded, this must fail cleanly.
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes("[1,2,3]"));
    var token = $"{header}.{payload}.somesig";

    var result = MakeValidator().Validate(token);
    Check(!result.IsValid, "a non-object payload must be rejected, not thrown");
    await Task.CompletedTask;
});

await Run("Jwt_NonStringSubjectOnValidlySignedToken_RejectedNotThrown", async () =>
{
    // This one is validly signed, so it reaches the post-signature claim
    // checks, sub as a number rather than a string must still fail
    // cleanly rather than throw out of GetString().
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes($"{{\"iss\":\"{TestIssuer}\",\"aud\":\"{TestAudience}\",\"sub\":12345,\"exp\":{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}"));
    var sig = Base64UrlEncode(HMACSHA256.HashData(testKey, Encoding.ASCII.GetBytes($"{header}.{payload}")));
    var token = $"{header}.{payload}.{sig}";

    var result = MakeValidator().Validate(token);
    Check(!result.IsValid, "a validly-signed token with a non-string sub must still be rejected, not thrown");
    await Task.CompletedTask;
});

await Run("Jwt_UnsupportedAlgorithm_Rejected", async () =>
{
    // alg: none is the classic JWT bypass. Confirm it's rejected outright,
    // not just "signature won't match" (there'd be no signature to check).
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "none", typ = "JWT" })));
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iss = TestIssuer, aud = TestAudience, sub = "operator-1", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })));
    var token = $"{header}.{payload}.";

    var result = MakeValidator().Validate(token);
    Check(!result.IsValid, "alg: none must never be accepted");
    await Task.CompletedTask;
});

await Run("Jwt_MalformedToken_Rejected", async () =>
{
    var result = MakeValidator().Validate("not-a-jwt-at-all");
    Check(!result.IsValid, "expected a malformed token to be rejected, not throw");
    await Task.CompletedTask;
});

await Run("Jwt_EmptyOrNullToken_Rejected", async () =>
{
    Check(!MakeValidator().Validate("").IsValid, "empty token should be rejected");
    Check(!MakeValidator().Validate(null).IsValid, "null token should be rejected");
    await Task.CompletedTask;
});

await Run("Jwt_ConstructorRejectsShortSigningKey", async () =>
{
    try
    {
        _ = new Hs256JwtValidator(TestIssuer, TestAudience, new byte[16]); // 128 bits, too short for HS256
        throw new Exception("expected ArgumentException for a too-short signing key, none was thrown");
    }
    catch (ArgumentException)
    {
        // expected
    }
    await Task.CompletedTask;
});

// ============================================================
// ClientOperations, in-process, no HTTP
// ============================================================

(ClientOperations ops, IClientRegistry registry) MakeOperations()
{
    var store = new InMemoryAuthorizationStore();
    var registry = new InMemoryClientRegistry();
    var locks = new InProcessClientLock();
    var audit = new NullAuditSink();
    var canon = new CanonicalForm(EndpointCatalog.Empty);
    var provisioner = new ClientProvisioner(store, registry, locks, canon);
    var killSwitch = new KillSwitchService(store, registry, locks, audit);
    return (new ClientOperations(provisioner, killSwitch, registry), registry);
}

await Run("Ops_Onboard_CreatesClientWithGrants", async () =>
{
    var (ops, _) = MakeOperations();
    var result = await ops.OnboardAsync(new OnboardRequest("Billing-Reconciler", "finance", new[] { new GrantDto("orders", null, null) }), default);

    Check(result.Success, $"expected success, got: {result.ErrorMessage}");
    Check(result.Client!.ClientRef == "billing-reconciler", $"expected canonicalized ref, got {result.Client.ClientRef}");
    Check(result.Client.Grants.Count == 1, "expected the api_group grant to round-trip");
});

await Run("Ops_Onboard_RejectsInvalidClientRef", async () =>
{
    var (ops, _) = MakeOperations();
    var result = await ops.OnboardAsync(new OnboardRequest("has a space", null, null), default);
    Check(!result.Success, "expected onboarding an invalid client_ref to fail");
});

await Run("Ops_Onboard_RejectsGrantWithNeitherApiGroupNorPath", async () =>
{
    var (ops, _) = MakeOperations();
    var result = await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto(null, null, null) }), default);
    Check(!result.Success, "expected a grant with no api_group and no path to be rejected before it reaches the core");
});

await Run("Ops_Kill_RequiresIncident", async () =>
{
    var (ops, _) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g", null, null) }), default);
    var result = await ops.KillAsync("a", incident: null, operatorId: "op-1", default);
    Check(!result.Success, "a kill with no incident id must be rejected, it wouldn't be auditable");
});

await Run("Ops_Kill_DeletesGrantsAndRegistryReflectsIt", async () =>
{
    var (ops, registry) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g", null, null) }), default);

    var kill = await ops.KillAsync("a", "INC-1", "op-1", default);
    Check(kill.Success, $"expected kill to succeed, got {kill.ErrorMessage}");
    Check(kill.TuplesDeleted == 1, $"expected 1 tuple deleted (the api_group grant), got {kill.TuplesDeleted}");

    var record = await registry.GetAsync("a", default);
    Check(record is { Killed: true }, "expected the registry to reflect the kill");
});

await Run("Ops_Kill_IsIdempotent", async () =>
{
    var (ops, _) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g", null, null) }), default);
    await ops.KillAsync("a", "INC-1", "op-1", default);

    var secondKill = await ops.KillAsync("a", "INC-2", "op-2", default);
    Check(secondKill.Success, $"a second kill on an already-killed client should still succeed, got {secondKill.ErrorMessage}");
    Check(secondKill.TuplesDeleted == 0, $"expected 0 tuples on the second kill, nothing left to delete, got {secondKill.TuplesDeleted}");
});

await Run("Ops_Onboard_OnKilledClient_DoesNotResurrectIt", async () =>
{
    var (ops, registry) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g", null, null) }), default);
    await ops.KillAsync("a", "INC-1", "op-1", default);

    var secondOnboard = await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g2", null, null) }), default);

    var record = await registry.GetAsync("a", default);
    Check(record is { Killed: true }, "onboarding a killed client must not clear the kill sentinel, that's ClientProvisioner's own invariant and this layer must not weaken it");
    Check(secondOnboard.Success, "the onboard call itself still succeeds, the core just skips reconciling grants");
    Check(!string.IsNullOrEmpty(secondOnboard.Warning), "expected a warning telling the caller the grants were not applied, so it's not mistaken for a normal success");
});

await Run("Ops_Restore_ClearsKillSentinel", async () =>
{
    var (ops, registry) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g", null, null) }), default);
    await ops.KillAsync("a", "INC-1", "op-1", default);

    var restore = await ops.RestoreAsync("a", "op-2", default);
    Check(restore.Success, $"expected restore to succeed, got {restore.ErrorMessage}");

    var record = await registry.GetAsync("a", default);
    Check(record is { Killed: false }, "expected the kill sentinel cleared after restore");
});

await Run("Ops_Get_ReturnsOnboardedClientState", async () =>
{
    var (ops, _) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("Billing-Reconciler", "finance", new[] { new GrantDto("orders", null, null) }), default);

    var result = await ops.GetAsync("Billing-Reconciler", default);
    Check(result.Outcome == GetClientOutcome.Found, $"expected Found, got {result.Outcome}");
    Check(result.Client!.ClientRef == "billing-reconciler", $"expected canonicalized ref, got {result.Client.ClientRef}");
    Check(result.Client.Grants.Count == 1, "expected the onboarded grant to come back");
    Check(!result.Client.Killed, "freshly onboarded client should not be killed");
});

await Run("Ops_Get_ReflectsKillState", async () =>
{
    var (ops, _) = MakeOperations();
    await ops.OnboardAsync(new OnboardRequest("a", null, new[] { new GrantDto("g", null, null) }), default);
    await ops.KillAsync("a", "INC-1", "op-1", default);

    var result = await ops.GetAsync("a", default);
    Check(result.Outcome == GetClientOutcome.Found, "a killed client is still found, killed is a state on it, not a deletion");
    Check(result.Client!.Killed, "expected the kill to show up in the read");
});

await Run("Ops_Get_UnknownClientRef_ReturnsNotFound", async () =>
{
    var (ops, _) = MakeOperations();
    var result = await ops.GetAsync("never-onboarded", default);
    Check(result.Outcome == GetClientOutcome.NotFound, $"expected NotFound for a ref that was never onboarded, got {result.Outcome}");
});

await Run("Ops_Get_InvalidClientRef_ReturnsInvalidClientRefNotNotFound", async () =>
{
    var (ops, _) = MakeOperations();
    var result = await ops.GetAsync("has a space", default);
    Check(result.Outcome == GetClientOutcome.InvalidClientRef, $"a malformed ref is a 400, not a 404, got {result.Outcome}");
    Check(!string.IsNullOrEmpty(result.ErrorMessage), "expected an error message explaining the invalid ref");
});

// ============================================================
// Live HTTP smoke test against the real running service
// ============================================================

await Run("LiveService_OnboardKillRestoreOverRealHttp", async () =>
{
    var repoRoot = FindRepoRoot();
    var dllPath = Path.Combine(repoRoot, "src", "Tessera.Service", "bin", "Debug", "net8.0", "Tessera.Service.dll");
    if (!File.Exists(dllPath))
        throw new Exception($"Tessera.Service.dll not found at {dllPath}, build it first (dotnet build src/Tessera.Service).");

    var port = GetFreeTcpPort();
    var signingKey = RandomNumberGenerator.GetBytes(32);
    var signingKeyB64 = Convert.ToBase64String(signingKey);

    var psi = new ProcessStartInfo("dotnet", dllPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    psi.Environment["TESSERA_JWT_SIGNING_KEY"] = signingKeyB64;
    psi.Environment["TESSERA_JWT_ISSUER"] = "smoke-test-issuer";
    psi.Environment["TESSERA_JWT_AUDIENCE"] = "smoke-test-audience";

    using var process = Process.Start(psi) ?? throw new Exception("failed to start Tessera.Service process");
    var stderrTask = process.StandardError.ReadToEndAsync();

    try
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        await WaitForHealthyAsync(http, TimeSpan.FromSeconds(15));

        var token = CreateToken("smoke-test-issuer", "smoke-test-audience", "smoke-operator", signingKey);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // unauthenticated request rejected
        using (var noAuthClient = new HttpClient { BaseAddress = http.BaseAddress })
        {
            var unauth = await noAuthClient.PostAsJsonAsync("clients/onboard", new { client_ref = "x" });
            Check(unauth.StatusCode == HttpStatusCode.Unauthorized, $"expected 401 without a token, got {unauth.StatusCode}");
        }

        // onboard
        var onboardResp = await http.PostAsJsonAsync("clients/onboard", new
        {
            client_ref = "smoke-client",
            business_unit = "test-bu",
            grants = new[] { new { api_group = "smoke-group" } },
        });
        var onboardBody = await onboardResp.Content.ReadAsStringAsync();
        Check(onboardResp.StatusCode == HttpStatusCode.OK, $"expected 200 from onboard, got {onboardResp.StatusCode}: {onboardBody}");
        Check(onboardBody.Contains("smoke-client"), "expected the onboarded client_ref in the response");

        // get, over real HTTP, authenticated
        var getResp = await http.GetAsync("clients/smoke-client");
        var getBody = await getResp.Content.ReadAsStringAsync();
        Check(getResp.StatusCode == HttpStatusCode.OK, $"expected 200 from get, got {getResp.StatusCode}: {getBody}");
        Check(getBody.Contains("smoke-client"), "expected the client_ref in the get response");
        Check(getBody.Contains("smoke-group"), "expected the onboarded grant to round-trip through get");

        // get, unauthenticated, rejected same as the mutating routes
        using (var noAuthClient = new HttpClient { BaseAddress = http.BaseAddress })
        {
            var unauthGet = await noAuthClient.GetAsync("clients/smoke-client");
            Check(unauthGet.StatusCode == HttpStatusCode.Unauthorized, $"expected 401 for get without a token, got {unauthGet.StatusCode}");
        }

        // get, unknown client_ref, 404 not 400, with a body, same result
        // shape as every other status code on this route
        var missingResp = await http.GetAsync("clients/never-onboarded");
        var missingBody = await missingResp.Content.ReadAsStringAsync();
        Check(missingResp.StatusCode == HttpStatusCode.NotFound, $"expected 404 for a client_ref that was never onboarded, got {missingResp.StatusCode}");
        Check(missingBody.Contains("\"outcome\":\"not_found\""), $"expected a JSON body on the 404, not an empty response, got: {missingBody}");

        // get, invalid client_ref, 400 with the same result shape, error_message set
        var invalidRefResp = await http.GetAsync("clients/has%20a%20space");
        var invalidRefBody = await invalidRefResp.Content.ReadAsStringAsync();
        Check(invalidRefResp.StatusCode == HttpStatusCode.BadRequest, $"expected 400 for an invalid client_ref, got {invalidRefResp.StatusCode}: {invalidRefBody}");
        Check(invalidRefBody.Contains("error_message"), $"expected error_message in the 400 body, got: {invalidRefBody}");

        // kill without incident rejected
        var killNoIncident = await http.PostAsJsonAsync("clients/smoke-client/kill", new { });
        Check(killNoIncident.StatusCode == HttpStatusCode.BadRequest, $"expected 400 for a kill with no incident, got {killNoIncident.StatusCode}");

        // kill
        var killResp = await http.PostAsJsonAsync("clients/smoke-client/kill", new { incident = "INC-SMOKE-1" });
        var killBody = await killResp.Content.ReadAsStringAsync();
        Check(killResp.StatusCode == HttpStatusCode.OK, $"expected 200 from kill, got {killResp.StatusCode}: {killBody}");
        Check(killBody.Contains("\"tuplesDeleted\":1") || killBody.Contains("\"tuples_deleted\":1"), $"expected 1 tuple deleted, got: {killBody}");

        // restore
        var restoreResp = await http.PostAsJsonAsync("clients/smoke-client/restore", new { });
        Check(restoreResp.StatusCode == HttpStatusCode.OK, $"expected 200 from restore, got {restoreResp.StatusCode}");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
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

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task WaitForHealthyAsync(HttpClient http, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    Exception? last = null;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            var resp = await http.GetAsync("healthz");
            if (resp.IsSuccessStatusCode) return;
        }
        catch (Exception ex)
        {
            last = ex;
        }
        await Task.Delay(200);
    }
    throw new Exception($"service did not become healthy within {timeout}, last error: {last?.Message}");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tessera.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new Exception("could not find repo root (Tessera.sln) from " + AppContext.BaseDirectory);
}

sealed class NullAuditSink : IAuditSink
{
    public Task AppendAsync(AuditEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
