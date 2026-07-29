namespace Tessera.ControlPlane;

/// <summary>The binding to OpenFGA (or a stand-in). See docs/CUSTOMIZING.md.</summary>
public interface IAuthorizationStore
{
    Task WriteTuplesAsync(IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task DeleteTuplesAsync(IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> ReadTuplesForClientAsync(string clientRef, CancellationToken ct = default);
    Task<bool> CheckAsync(string user, string relation, string @object, CancellationToken ct = default);
}

/// <summary>Durable client + kill-sentinel storage. Upsert MUST preserve kill state.</summary>
public interface IClientRegistry
{
    Task<ClientRecord?> GetAsync(string clientRef, CancellationToken ct = default);
    Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(ClientRecord record, CancellationToken ct = default);
    Task<bool> DeleteAsync(string clientRef, CancellationToken ct = default);
    Task MarkKilledAsync(string clientRef, string incident, string @operator, DateTimeOffset at, CancellationToken ct = default);
    Task ClearKilledAsync(string clientRef, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetKilledRefsAsync(CancellationToken ct = default);
}

/// <summary>Maps a gateway request to a canonical client_ref + assurance.</summary>
public interface IIdentityResolver
{
    ResolvedClient? Resolve(ResolveContext ctx);
}

/// <summary>Append-only audit destination.</summary>
public interface IAuditSink
{
    Task AppendAsync(AuditEvent evt, CancellationToken ct = default);
}

/// <summary>Per-client_ref mutual exclusion. Single-replica: in-process; multi-replica: DB advisory lock.</summary>
public interface IClientLock
{
    Task<IAsyncDisposable> AcquireAsync(string clientRef, CancellationToken ct = default);
}
