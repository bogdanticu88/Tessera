namespace Tessera.ControlPlane;

/// <summary>An OpenFGA relationship tuple: user has relation on object.</summary>
public sealed record RelationTuple(string User, string Relation, string Object);

/// <summary>Assurance of an identity resolution - gate high-value operations on <see cref="Strong"/>.</summary>
public enum Assurance { Weak = 0, Medium = 1, Strong = 2 }

/// <summary>A caller resolved to a canonical client reference.</summary>
public sealed record ResolvedClient(string ClientRef, Assurance Assurance);

/// <summary>What the gateway saw for a request, handed to an <see cref="IIdentityResolver"/>.</summary>
public sealed record ResolveContext(
    IReadOnlyDictionary<string, string> Claims,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>A declared grant: either a whole api_group, or a single endpoint (method + path).</summary>
public sealed record Grant
{
    public string? ApiGroup { get; init; }
    public string? Method { get; init; }
    public string? Path { get; init; }

    public bool IsApiGroup => !string.IsNullOrWhiteSpace(ApiGroup);
    public bool IsEndpoint => !string.IsNullOrWhiteSpace(Path);

    public static Grant ForApiGroup(string group) => new() { ApiGroup = group };
    public static Grant ForEndpoint(string method, string path) => new() { Method = method, Path = path };
}

/// <summary>A durable client record. Kill state must survive restarts and be preserved across upserts.</summary>
public sealed record ClientRecord
{
    public required string ClientRef { get; init; }
    public string? BusinessUnit { get; init; }
    public IReadOnlyList<Grant> Grants { get; init; } = Array.Empty<Grant>();

    public bool Killed { get; init; }
    public string? KillIncident { get; init; }
    public DateTimeOffset? KilledAt { get; init; }
    public string? KilledBy { get; init; }
}

/// <summary>An append-only audit event.</summary>
public sealed record AuditEvent(string Action, string ClientRef, string Operator, string? Incident, DateTimeOffset At);

/// <summary>Outcome of a kill.</summary>
public sealed record KillResult(string ClientRef, int TuplesDeleted);
