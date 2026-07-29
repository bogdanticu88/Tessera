namespace Tessera.ControlPlane;

/// <summary>
/// Provisions clients and reconciles their grants to OpenFGA tuples via an idempotent DIFF (write
/// desired-existing, delete existing-desired). This is what lets the same code run against real
/// OpenFGA (whose writes/deletes are NOT idempotent) as against the tolerant in-memory store.
/// Reconciliation never resurrects a kill: a killed client is skipped.
/// </summary>
public sealed class ClientProvisioner(
    IAuthorizationStore store,
    IClientRegistry registry,
    IClientLock locks,
    CanonicalForm canon)
{
    /// <summary>Onboard or update a client and reconcile its grants. Idempotent; safe to re-run.</summary>
    public async Task ApplyAsync(ClientRecord recordRaw, CancellationToken ct = default)
    {
        var clientRef = CanonicalForm.ClientRef(recordRaw.ClientRef);
        await using var _ = await locks.AcquireAsync(clientRef, ct).ConfigureAwait(false);

        var existingRecord = await registry.GetAsync(clientRef, ct).ConfigureAwait(false);
        if (existingRecord?.Killed == true)
            return; // never (re)authorize a killed client through reconcile

        var record = recordRaw with { ClientRef = clientRef };
        await registry.UpsertAsync(record, ct).ConfigureAwait(false); // preserves kill state

        var desired = GrantTupleMapper.ToTuples(clientRef, record.Grants, canon);
        var existing = await store.ReadTuplesForClientAsync(clientRef, ct).ConfigureAwait(false);

        var toWrite = desired.Except(existing).ToList();
        var toDelete = existing.Except(desired).ToList();

        if (toWrite.Count > 0) await store.WriteTuplesAsync(toWrite, ct).ConfigureAwait(false);
        if (toDelete.Count > 0) await store.DeleteTuplesAsync(toDelete, ct).ConfigureAwait(false);
    }

    /// <summary>Offboard a client: delete ALL its tuples AND its registry record.</summary>
    public async Task DeleteAsync(string clientRefRaw, CancellationToken ct = default)
    {
        var clientRef = CanonicalForm.ClientRef(clientRefRaw);
        await using var _ = await locks.AcquireAsync(clientRef, ct).ConfigureAwait(false);

        var tuples = await store.ReadTuplesForClientAsync(clientRef, ct).ConfigureAwait(false);
        if (tuples.Count > 0) await store.DeleteTuplesAsync(tuples, ct).ConfigureAwait(false);
        await registry.DeleteAsync(clientRef, ct).ConfigureAwait(false);
    }
}
