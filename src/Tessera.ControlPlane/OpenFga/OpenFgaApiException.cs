using System.Net;

namespace Tessera.ControlPlane.OpenFga;

/// <summary>
/// Thrown for any non-success response from the OpenFGA API. Carries the
/// HTTP status and, where the body parsed, OpenFGA's own error code and
/// message, so a caller can tell a 400 (bad tuple) from a 429 (rate
/// limited, worth a retry with backoff) from a 5xx (their problem, not
/// yours) without string-matching an exception message.
/// </summary>
public sealed class OpenFgaApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ApiCode { get; }

    public OpenFgaApiException(HttpStatusCode statusCode, string? apiCode, string? apiMessage, string? rawBody = null)
        : base(BuildMessage(statusCode, apiCode, apiMessage, rawBody))
    {
        StatusCode = statusCode;
        ApiCode = apiCode;
    }

    private static string BuildMessage(HttpStatusCode statusCode, string? apiCode, string? apiMessage, string? rawBody)
    {
        if (apiCode is not null || apiMessage is not null)
            return $"OpenFGA API error {(int)statusCode} ({statusCode}): {apiCode ?? "unknown_code"}, {apiMessage ?? "no message"}";

        var bodyPreview = string.IsNullOrEmpty(rawBody) ? "empty body" : rawBody.Length > 200 ? rawBody[..200] + "..." : rawBody;
        return $"OpenFGA API error {(int)statusCode} ({statusCode}), body did not parse as the expected error shape: {bodyPreview}";
    }
}

/// <summary>
/// Thrown when a chunked write or delete fails partway through. Some
/// tuples from earlier chunks already committed at OpenFGA before the
/// failing chunk was sent, there's no way to undo that from here, and the
/// caller needs to know it happened rather than treat the whole batch as
/// a no-op. TuplesSucceeded counts what committed before the failure;
/// InnerException is why the next chunk failed.
/// </summary>
public sealed class OpenFgaPartialBatchException(int tuplesSucceeded, int tuplesRequested, Exception inner)
    : Exception(
        $"OpenFGA batch operation failed after {tuplesSucceeded} of {tuplesRequested} tuples were already committed in earlier chunks. The succeeded tuples are applied and were not rolled back; see the inner exception for why the rest of the batch failed.",
        inner)
{
    public int TuplesSucceeded { get; } = tuplesSucceeded;
    public int TuplesRequested { get; } = tuplesRequested;
}
