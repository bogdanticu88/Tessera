using System.Text.Json;
using System.Text.Json.Serialization;
using Tessera.ControlPlane;
using Tessera.ControlPlane.OpenFga;
using Tessera.Service.Auth;
using Tessera.Service.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// --- signing key: fail fast, never fall back to a default ---
// A missing signing key is a deploy-time mistake, not something to paper
// over with a weak default that would make every kill/restore call
// unauthenticated in practice.
var signingKeyB64 = Environment.GetEnvironmentVariable("TESSERA_JWT_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(signingKeyB64))
{
    Console.Error.WriteLine("TESSERA_JWT_SIGNING_KEY is required (base64-encoded, at least 32 bytes). Refusing to start without it.");
    return 1;
}

byte[] signingKey;
try
{
    signingKey = Convert.FromBase64String(signingKeyB64);
}
catch (FormatException)
{
    Console.Error.WriteLine("TESSERA_JWT_SIGNING_KEY is not valid base64.");
    return 1;
}

var issuer = Environment.GetEnvironmentVariable("TESSERA_JWT_ISSUER") ?? "tessera";
var audience = Environment.GetEnvironmentVariable("TESSERA_JWT_AUDIENCE") ?? "tessera-clients";

// --- endpoint catalog: optional, empty (no path collapsing) if unset ---
var catalog = EndpointCatalog.Empty;
var catalogPath = Environment.GetEnvironmentVariable("TESSERA_ENDPOINT_CATALOG_PATH");
if (!string.IsNullOrWhiteSpace(catalogPath))
{
    if (!File.Exists(catalogPath))
    {
        Console.Error.WriteLine($"TESSERA_ENDPOINT_CATALOG_PATH is set to '{catalogPath}' but that file doesn't exist. Refusing to start with a config path that can't be read.");
        return 1;
    }

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        // The catalog file carries method+path pairs (see .env.example),
        // but EndpointCatalog itself only ever templates the path,
        // CanonicalForm.Method handles the method half separately. Method
        // is read here only to validate the file shape, not used further.
        var paths = doc.RootElement.GetProperty("endpoints")
            .EnumerateArray()
            .Select(e =>
            {
                _ = e.GetProperty("method").GetString(); // validate presence/shape, unused otherwise
                return e.GetProperty("path").GetString() ?? throw new JsonException("endpoint entry is missing 'path'");
            })
            .ToList();
        catalog = new EndpointCatalog(paths);
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        Console.Error.WriteLine($"TESSERA_ENDPOINT_CATALOG_PATH points to a file that doesn't match the expected {{\"endpoints\":[{{\"method\":...,\"path\":...}}]}} shape: {ex.Message}");
        return 1;
    }
}

// --- authorization store: OpenFGA if configured, in-memory otherwise ---
var openFgaUrl = Environment.GetEnvironmentVariable("OPENFGA_API_URL");
var openFgaStoreId = Environment.GetEnvironmentVariable("OPENFGA_STORE_ID");

builder.Services.AddSingleton<CanonicalForm>(new CanonicalForm(catalog));
builder.Services.AddSingleton<IClientRegistry, InMemoryClientRegistry>();
builder.Services.AddSingleton<IClientLock, InProcessClientLock>();
builder.Services.AddSingleton<IAuditSink, ConsoleAuditSink>();

if (!string.IsNullOrWhiteSpace(openFgaUrl))
{
    if (string.IsNullOrWhiteSpace(openFgaStoreId))
    {
        Console.Error.WriteLine("OPENFGA_API_URL is set but OPENFGA_STORE_ID is not. Both are required to use a real OpenFGA store.");
        return 1;
    }

    var baseUrl = openFgaUrl.EndsWith('/') ? openFgaUrl : openFgaUrl + "/";
    var authorizationModelId = Environment.GetEnvironmentVariable("OPENFGA_MODEL_ID");
    var apiToken = Environment.GetEnvironmentVariable("OPENFGA_API_TOKEN");

    // AddTypedClient's TClient is inferred from the factory delegate's return
    // type, not from the AddHttpClient<TClient, TImplementation> call above
    // it. Leaving it inferred makes it bind the factory to the concrete
    // OpenFgaAuthorizationStore type, not to IAuthorizationStore, so a
    // request for IAuthorizationStore falls back to AddHttpClient's own
    // default factory, which uses ActivatorUtilities to construct
    // OpenFgaAuthorizationStore and can't resolve its plain `string storeId`
    // constructor parameter from the container. That throws
    // InvalidOperationException: Unable to resolve service for type
    // 'System.String' the first time something actually asks for
    // IAuthorizationStore, only reachable by pointing this service at a real
    // OpenFGA instance, which is why it slipped past every check so far. The
    // explicit <IAuthorizationStore> below is what makes this factory the
    // one that answers for the interface.
    builder.Services.AddHttpClient<IAuthorizationStore, OpenFgaAuthorizationStore>((sp, http) =>
    {
        http.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrWhiteSpace(apiToken))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
    }).AddTypedClient<IAuthorizationStore>((http, sp) => new OpenFgaAuthorizationStore(http, openFgaStoreId!, authorizationModelId));
}
else
{
    builder.Services.AddSingleton<IAuthorizationStore, InMemoryAuthorizationStore>();
}

builder.Services.AddSingleton<ClientProvisioner>();
builder.Services.AddSingleton<KillSwitchService>();
builder.Services.AddSingleton<ClientOperations>();
builder.Services.AddSingleton(new Hs256JwtValidator(issuer, audience, signingKey));

// snake_case throughout, matching the OpenFGA adapter's own wire format
// (OpenFgaAuthorizationStore) and the rest of this system's JSON, rather
// than minimal APIs' default camelCase. Applies to both request binding
// and response serialization. Enums get the same treatment and are
// written as strings, not numbers, System.Text.Json's default for an enum
// is its underlying int, which is a wire contract that breaks the moment
// someone reorders or inserts an enum member. GetClientOutcome is the
// first enum on this surface, this converter is here so the next one gets
// the same treatment without anyone having to remember to add it.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

var app = builder.Build();

// Defense in depth, in addition to Hs256JwtValidator's own catch-all: no
// handler below should ever be able to leak an exception detail or a
// stack trace to the caller, on an admin surface that's exactly the kind
// of thing worth guarding twice.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "internal server error" });
}));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var clients = app.MapGroup("/clients").AddEndpointFilter<AuthEndpointFilter>();

clients.MapPost("/onboard", async (OnboardRequest body, ClientOperations ops, CancellationToken ct) =>
{
    var result = await ops.OnboardAsync(body, ct).ConfigureAwait(false);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

clients.MapPost("/{clientRef}/kill", async (string clientRef, KillRequestBody body, HttpContext http, ClientOperations ops, CancellationToken ct) =>
{
    var result = await ops.KillAsync(clientRef, body.Incident, RequireOperator(http), ct).ConfigureAwait(false);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

clients.MapPost("/{clientRef}/restore", async (string clientRef, HttpContext http, ClientOperations ops, CancellationToken ct) =>
{
    var result = await ops.RestoreAsync(clientRef, RequireOperator(http), ct).ConfigureAwait(false);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

clients.MapGet("/{clientRef}", async (string clientRef, ClientOperations ops, CancellationToken ct) =>
{
    var result = await ops.GetAsync(clientRef, ct).ConfigureAwait(false);

    // Same result record on every status code, 200, 400, and 404 alike,
    // matching how onboard/kill/restore all return their own result DTO
    // rather than a bespoke shape per status. A caller that deserializes
    // error_message off any response from this service gets the same
    // field name everywhere, and a 404 carries a body instead of being the
    // one status code on this whole surface that silently doesn't.
    return result.Outcome switch
    {
        GetClientOutcome.Found => Results.Ok(result),
        GetClientOutcome.NotFound => Results.NotFound(result),
        _ => Results.BadRequest(result),
    };
});

app.Run();
return 0;

// AuthEndpointFilter always runs before these handlers and always sets
// this on success, but "always" is a wiring guarantee, not a type
// guarantee, a future route added to the group without the filter, or a
// reordering, would otherwise silently hand a null operator to an audit
// trail instead of failing. This throws instead of casting, so that
// mistake becomes a loud 500 (caught by UseExceptionHandler above)
// rather than a quietly wrong audit entry.
static string RequireOperator(HttpContext http) =>
    http.Items["operator"] as string is { Length: > 0 } op
        ? op
        : throw new InvalidOperationException("no authenticated operator on an authenticated route; AuthEndpointFilter did not run or did not set one");

public sealed record KillRequestBody(string? Incident);
