using System.Globalization;
using System.Text;

namespace Tessera.ControlPlane;

/// <summary>
/// The one canonical form for client_ref / method / path. The string the gateway builds for a /check
/// MUST be byte-for-byte identical to what the control plane writes - so both sides use THIS.
/// Path parameters collapse to <c>{param}</c> only at declared catalog positions (never a blanket
/// "looks like an id", which would merge distinct resources and over-grant).
/// </summary>
public sealed class CanonicalForm
{
    private static readonly char[] AllowedRefExtra = { '.', '_', '-' };
    private static readonly HashSet<string> Methods =
        new(StringComparer.Ordinal) { "get", "post", "put", "delete", "patch", "head", "options" };

    private readonly EndpointCatalog _catalog;

    public CanonicalForm(EndpointCatalog catalog) => _catalog = catalog;

    public static string ClientRef(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("client_ref must not be empty.");
        var s = raw.Trim().ToLowerInvariant();
        foreach (var c in s)
        {
            var asciiAlnum = c < 128 && char.IsLetterOrDigit(c);
            if (!asciiAlnum && Array.IndexOf(AllowedRefExtra, c) < 0)
                throw new ArgumentException($"client_ref contains an invalid character: '{c}'.");
        }
        return s;
    }

    public static string Method(string raw)
    {
        var m = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (!Methods.Contains(m))
            throw new ArgumentException($"unsupported HTTP method: '{raw}'.");
        return m;
    }

    public string Path(string rawPath)
    {
        if (rawPath is null) throw new ArgumentException("path must not be null.");

        // Percent-decode to a fixed point; reject if encoding survives (double-encoding attack).
        var s = rawPath;
        for (var i = 0; i < 3 && s.Contains('%'); i++) s = Uri.UnescapeDataString(s);
        if (s.Contains('%')) throw new ArgumentException("path has residual percent-encoding after decode.");

        s = s.Normalize(NormalizationForm.FormC).Trim().Trim('/').ToLowerInvariant();
        if (s.Contains("..") || s.Contains('\\'))
            throw new ArgumentException("path contains a traversal sequence or backslash.");
        if (s.Any(char.IsControl))
            throw new ArgumentException("path contains a control character.");

        var segs = s.Length == 0 ? Array.Empty<string>() : s.Split('/');
        return _catalog.CollapseToTemplate(segs) ?? string.Join('/', segs);
    }
}

/// <summary>Your API surface as data. Only these templates' parameter positions get collapsed to {param}.</summary>
public sealed class EndpointCatalog
{
    private readonly List<string[]> _templates;

    public EndpointCatalog(IEnumerable<string> templatePaths)
    {
        _templates = templatePaths
            .Select(p => p.Trim().Trim('/').ToLowerInvariant().Split('/'))
            .ToList();
    }

    public static EndpointCatalog Empty { get; } = new(Array.Empty<string>());

    /// <summary>If <paramref name="segs"/> matches a template (same length, literals equal), returns the
    /// canonical template string with <c>{param}</c> at parameter positions; otherwise null.</summary>
    public string? CollapseToTemplate(string[] segs)
    {
        foreach (var t in _templates)
        {
            if (t.Length != segs.Length) continue;
            var ok = true;
            for (var i = 0; i < t.Length; i++)
            {
                if (t[i] == "{param}") continue;
                if (!string.Equals(t[i], segs[i], StringComparison.Ordinal)) { ok = false; break; }
            }
            if (ok) return string.Join('/', t);
        }
        return null;
    }
}

/// <summary>Maps declared <see cref="Grant"/>s to OpenFGA tuples (deduplicated).</summary>
public static class GrantTupleMapper
{
    public static IReadOnlyList<RelationTuple> ToTuples(string clientRef, IEnumerable<Grant> grants, CanonicalForm canon)
    {
        var user = $"client:{clientRef}";
        var tuples = new List<RelationTuple>();
        foreach (var g in grants)
        {
            if (g.IsApiGroup)
            {
                tuples.Add(new RelationTuple(user, "member", $"api_group:{g.ApiGroup!.Trim().ToLowerInvariant()}"));
            }
            else if (g.IsEndpoint)
            {
                var method = CanonicalForm.Method(g.Method ?? "get");
                var path = canon.Path(g.Path!);
                tuples.Add(new RelationTuple(user, method, $"api_endpoint:{path}"));
            }
        }
        return tuples.Distinct().ToList();
    }
}
