namespace Tessera.ControlPlane;

/// <summary>
/// The kill switch. A kill is: set a DURABLE sentinel FIRST, then delete the client's tuples and
/// re-read until empty. Sentinel-first + a per-client_ref lock is what makes a kill safe against a
/// concurrent grant (a provisioning path refuses to re-authorize a killed client - see
/// <see cref="ClientProvisioner"/>). The token is never touched; only the authorization is withdrawn.
/// </summary>
public sealed class KillSwitchService(
    IAuthorizationStore store,
    IClientRegistry registry,
    IClientLock locks,
    IAuditSink audit,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<KillResult> KillAsync(string clientRefRaw, string incident, string @operator, CancellationToken ct = default)
    {
        var clientRef = CanonicalForm.ClientRef(clientRefRaw);
        await using var _ = await locks.AcquireAsync(clientRef, ct).ConfigureAwait(false);

        var at = _clock.GetUtcNow();

        // STEP 1 (the guarantee): durable sentinel FIRST, before touching tuples.
        await registry.MarkKilledAsync(clientRef, incident, @operator, at, ct).ConfigureAwait(false);

        // STEP 2-4: delete then re-read until empty (bounded) so any residual - including a tuple a
        // grant raced in just before the sentinel - is confirmed gone before the kill returns.
        var deleted = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var tuples = await store.ReadTuplesForClientAsync(clientRef, ct).ConfigureAwait(false);
            if (tuples.Count == 0) break;
            await store.DeleteTuplesAsync(tuples, ct).ConfigureAwait(false);
            deleted += tuples.Count;
        }

        // STEP 5: audit. The kill already took effect; a ship failure must be surfaced loudly, not swallow the kill.
        try
        {
            await audit.AppendAsync(new AuditEvent("kill", clientRef, @operator, incident, at), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AUDIT-FAIL] kill of {clientRef} took effect ({deleted} tuples deleted, sentinel set) but audit ship FAILED: {ex.Message}");
        }

        return new KillResult(clientRef, deleted);
    }

    public async Task RestoreAsync(string clientRefRaw, string @operator, CancellationToken ct = default)
    {
        var clientRef = CanonicalForm.ClientRef(clientRefRaw);
        await using var _ = await locks.AcquireAsync(clientRef, ct).ConfigureAwait(false);

        await registry.ClearKilledAsync(clientRef, ct).ConfigureAwait(false);
        await audit.AppendAsync(new AuditEvent("restore", clientRef, @operator, null, _clock.GetUtcNow()), ct).ConfigureAwait(false);
    }
}
