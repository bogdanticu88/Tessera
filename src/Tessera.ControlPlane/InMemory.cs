using System.Collections.Concurrent;

namespace Tessera.ControlPlane;

/// <summary>
/// Reference in-memory authorization store - makes the project run without OpenFGA and is ideal for
/// tests. It uses SET semantics (tolerant of duplicate writes / missing deletes). NOTE: real OpenFGA
/// is NOT tolerant - that is exactly why the core mutates via a reconcile-diff (see ClientProvisioner)
/// and why your production <see cref="IAuthorizationStore"/> adapter must handle paging + chunking.
/// </summary>
public sealed class InMemoryAuthorizationStore : IAuthorizationStore
{
    private readonly ConcurrentDictionary<RelationTuple, byte> _tuples = new();

    public Task WriteTuplesAsync(IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        foreach (var t in tuples) _tuples[t] = 1;
        return Task.CompletedTask;
    }

    public Task DeleteTuplesAsync(IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        foreach (var t in tuples) _tuples.TryRemove(t, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RelationTuple>> ReadTuplesForClientAsync(string clientRef, CancellationToken ct = default)
    {
        var user = $"client:{clientRef}";
        IReadOnlyList<RelationTuple> result = _tuples.Keys
            .Where(t => string.Equals(t.User, user, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> CheckAsync(string user, string relation, string @object, CancellationToken ct = default)
        => Task.FromResult(_tuples.ContainsKey(new RelationTuple(user, relation, @object)));
}

/// <summary>Reference in-memory registry. Upsert preserves kill state; killed set is queryable.</summary>
public sealed class InMemoryClientRegistry : IClientRegistry
{
    // Ordinal on purpose: OpenFGA is case-sensitive, so the registry must be too.
    private readonly ConcurrentDictionary<string, ClientRecord> _records = new(StringComparer.Ordinal);

    public Task<ClientRecord?> GetAsync(string clientRef, CancellationToken ct = default)
    {
        _records.TryGetValue(clientRef, out var r);
        return Task.FromResult<ClientRecord?>(r);
    }

    public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ClientRecord>>(_records.Values.ToList());

    public Task UpsertAsync(ClientRecord record, CancellationToken ct = default)
    {
        _records.AddOrUpdate(record.ClientRef, record, (_, existing) => record with
        {
            Killed = existing.Killed,
            KillIncident = existing.KillIncident,
            KilledAt = existing.KilledAt,
            KilledBy = existing.KilledBy,
        });
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string clientRef, CancellationToken ct = default)
        => Task.FromResult(_records.TryRemove(clientRef, out _));

    public Task MarkKilledAsync(string clientRef, string incident, string @operator, DateTimeOffset at, CancellationToken ct = default)
    {
        _records.AddOrUpdate(clientRef,
            _ => new ClientRecord { ClientRef = clientRef, Killed = true, KillIncident = incident, KilledAt = at, KilledBy = @operator },
            (_, existing) => existing with { Killed = true, KillIncident = incident, KilledAt = at, KilledBy = @operator });
        return Task.CompletedTask;
    }

    public Task ClearKilledAsync(string clientRef, CancellationToken ct = default)
    {
        if (_records.TryGetValue(clientRef, out var existing))
            _records[clientRef] = existing with { Killed = false, KillIncident = null, KilledAt = null, KilledBy = null };
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetKilledRefsAsync(CancellationToken ct = default)
    {
        IReadOnlySet<string> set = _records.Values.Where(r => r.Killed).Select(r => r.ClientRef).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(set);
    }
}

/// <summary>In-process per-client_ref lock (correct for a single replica). Multi-replica: DB advisory lock.</summary>
public sealed class InProcessClientLock : IClientLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(string clientRef, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(clientRef, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Development audit sink. Swap for your SIEM in production.</summary>
public sealed class ConsoleAuditSink : IAuditSink
{
    public Task AppendAsync(AuditEvent evt, CancellationToken ct = default)
    {
        Console.WriteLine($"[audit] {evt.At:O} {evt.Action} client={evt.ClientRef} by={evt.Operator} incident={evt.Incident ?? "-"}");
        return Task.CompletedTask;
    }
}

/// <summary>Reference resolver: maps a configured claim (or, fallback, header) to a client_ref.</summary>
public sealed class ClaimOrHeaderIdentityResolver(string claimName, string? headerName = null, Assurance assurance = Assurance.Medium)
    : IIdentityResolver
{
    public ResolvedClient? Resolve(ResolveContext ctx)
    {
        if (ctx.Claims.TryGetValue(claimName, out var fromClaim) && !string.IsNullOrWhiteSpace(fromClaim))
            return new ResolvedClient(CanonicalForm.ClientRef(fromClaim), assurance);
        if (headerName is not null && ctx.Headers.TryGetValue(headerName, out var fromHeader) && !string.IsNullOrWhiteSpace(fromHeader))
            return new ResolvedClient(CanonicalForm.ClientRef(fromHeader), Assurance.Weak); // header is weaker
        return null;
    }
}
