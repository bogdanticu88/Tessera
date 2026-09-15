using Tessera.ControlPlane;

namespace Tessera.Service.Endpoints;

// DTOs are deliberately separate from Tessera.ControlPlane's own types
// (ClientRecord, Grant): the wire shape and the domain model are allowed
// to drift, and keeping them apart means a JSON contract change doesn't
// force a change to the core library, or the other way around.

public sealed record GrantDto(string? ApiGroup, string? Method, string? Path);

public sealed record OnboardRequest(string? ClientRef, string? BusinessUnit, IReadOnlyList<GrantDto>? Grants);

public sealed record ClientStateDto(string ClientRef, string? BusinessUnit, bool Killed, IReadOnlyList<GrantDto> Grants);

public sealed record OnboardResult(bool Success, string? ErrorMessage, string? Warning, ClientStateDto? Client)
{
    public static OnboardResult Ok(ClientStateDto client, string? warning = null) => new(true, null, warning, client);
    public static OnboardResult Failure(string message) => new(false, message, null, null);
}

public sealed record KillResultDto(bool Success, string? ErrorMessage, string? ClientRef, int TuplesDeleted)
{
    public static KillResultDto Ok(string clientRef, int tuplesDeleted) => new(true, null, clientRef, tuplesDeleted);
    public static KillResultDto Failure(string message) => new(false, message, null, 0);
}

public sealed record RestoreResultDto(bool Success, string? ErrorMessage, string? ClientRef)
{
    public static RestoreResultDto Ok(string clientRef) => new(true, null, clientRef);
    public static RestoreResultDto Failure(string message) => new(false, message, null);
}

/// <summary>
/// The actual onboard/kill/restore logic, as plain methods with no
/// dependency on ASP.NET's hosting model. Program.cs's minimal API
/// endpoints are a thin HTTP adapter around this class: parse the
/// request, call in here, translate the result to a status code. Kept
/// this way so the logic can be exercised directly in tools/ServiceHarness
/// without needing a running web server, only the live-request smoke
/// test needs the real HTTP stack.
///
/// operatorId is never read from a request body. It comes from the
/// caller's validated JWT subject (Program.cs extracts it after auth),
/// so a caller can't claim to be a different operator than the token
/// they presented says they are, that would defeat audit attribution
/// entirely.
/// </summary>
public sealed class ClientOperations(ClientProvisioner provisioner, KillSwitchService killSwitch, IClientRegistry registry)
{
    public async Task<OnboardResult> OnboardAsync(OnboardRequest request, CancellationToken ct)
    {
        string clientRef;
        try
        {
            clientRef = CanonicalForm.ClientRef(request.ClientRef ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            return OnboardResult.Failure(ex.Message);
        }

        var grants = new List<Grant>();
        foreach (var g in request.Grants ?? Array.Empty<GrantDto>())
        {
            if (!string.IsNullOrWhiteSpace(g.ApiGroup))
                grants.Add(Grant.ForApiGroup(g.ApiGroup));
            else if (!string.IsNullOrWhiteSpace(g.Path))
                grants.Add(Grant.ForEndpoint(g.Method ?? "get", g.Path));
            else
                return OnboardResult.Failure("each grant must set either api_group, or method and path");
        }

        var record = new ClientRecord { ClientRef = clientRef, BusinessUnit = request.BusinessUnit, Grants = grants };

        try
        {
            await provisioner.ApplyAsync(record, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // A bad individual grant (unsupported method, an out-of-catalog
            // path, a traversal sequence) surfaces here, canonicalizing
            // grants happens inside ApplyAsync, not before it.
            return OnboardResult.Failure(ex.Message);
        }

        var stored = await registry.GetAsync(clientRef, ct).ConfigureAwait(false);
        if (stored is null)
            return OnboardResult.Failure("onboard reported success but the client record can't be read back, this is a bug, not a client error");

        // ClientProvisioner.ApplyAsync silently skips reconciling grants
        // for an already-killed client, that's the core's own
        // never-resurrect-a-kill invariant, and this layer must not weaken
        // it. But "silently" is right for the core and wrong for an HTTP
        // caller: surface it, so an operator doing incident response
        // can't mistake a no-op onboard for grants having actually applied.
        var warning = stored.Killed
            ? "client is killed, grants were not applied, restore the client first if new grants should take effect"
            : null;

        return OnboardResult.Ok(ToDto(stored), warning);
    }

    public async Task<KillResultDto> KillAsync(string clientRefRaw, string? incident, string operatorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incident))
            return KillResultDto.Failure("incident is required, a kill with no incident id is not auditable");

        string clientRef;
        try
        {
            clientRef = CanonicalForm.ClientRef(clientRefRaw);
        }
        catch (ArgumentException ex)
        {
            return KillResultDto.Failure(ex.Message);
        }

        var result = await killSwitch.KillAsync(clientRef, incident, operatorId, ct).ConfigureAwait(false);
        return KillResultDto.Ok(result.ClientRef, result.TuplesDeleted);
    }

    public async Task<RestoreResultDto> RestoreAsync(string clientRefRaw, string operatorId, CancellationToken ct)
    {
        string clientRef;
        try
        {
            clientRef = CanonicalForm.ClientRef(clientRefRaw);
        }
        catch (ArgumentException ex)
        {
            return RestoreResultDto.Failure(ex.Message);
        }

        await killSwitch.RestoreAsync(clientRef, operatorId, ct).ConfigureAwait(false);
        return RestoreResultDto.Ok(clientRef);
    }

    private static ClientStateDto ToDto(ClientRecord record) => new(
        record.ClientRef,
        record.BusinessUnit,
        record.Killed,
        record.Grants.Select(g => new GrantDto(g.ApiGroup, g.Method, g.Path)).ToList());
}
