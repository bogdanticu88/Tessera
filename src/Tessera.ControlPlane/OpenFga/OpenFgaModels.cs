namespace Tessera.ControlPlane.OpenFga;

// Wire DTOs for the OpenFGA REST API. Field names match the API exactly once
// run through JsonNamingPolicy.SnakeCaseLower (see JsonOptions in
// OpenFgaAuthorizationStore), so a property here called ContinuationToken
// serializes as continuation_token on the wire. Shapes confirmed against
// openfga.dev/docs/interacting/relationship-queries and
// openfga.dev/docs/interacting/managing-relationships-between-objects.

/// <summary>A tuple key with all three fields required. Used for write, delete, and check, where a partial tuple doesn't make sense.</summary>
public sealed record OpenFgaTupleKey(string User, string Relation, string Object);

/// <summary>A tuple key filter with every field optional. Used for read, where you're asking "everything matching what I give you."</summary>
public sealed record OpenFgaTupleKeyFilter(string? User, string? Relation, string? Object);

public sealed record WriteTuples(IReadOnlyList<OpenFgaTupleKey> TupleKeys);

public sealed record WriteRequest(WriteTuples? Writes, WriteTuples? Deletes, string? AuthorizationModelId);

public sealed record CheckRequest(OpenFgaTupleKeyFilter TupleKey, string? AuthorizationModelId);

public sealed record CheckResponse(bool Allowed);

public sealed record ReadRequest(OpenFgaTupleKeyFilter? TupleKey, int? PageSize, string? ContinuationToken);

public sealed record ReadTupleEntry(OpenFgaTupleKey Key, DateTimeOffset Timestamp);

public sealed record ReadResponse(IReadOnlyList<ReadTupleEntry> Tuples, string? ContinuationToken);

/// <summary>OpenFGA's error body: {"code": "...", "message": "..."}.</summary>
public sealed record OpenFgaErrorBody(string? Code, string? Message);
