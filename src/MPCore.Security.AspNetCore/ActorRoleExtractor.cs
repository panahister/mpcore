using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Extracts normalized roles from bounded, dotted JSON claim paths. Extraction is total-allocation
/// bounded, never throws on malformed provider JSON, and never logs a claim value.
/// </summary>
internal sealed class ActorRoleExtractor(
    IOptions<ActorClaimMappingOptions> options,
    ILogger<ActorRoleExtractor> logger)
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    private readonly ConcurrentDictionary<string, byte> _reportedSources = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Extract(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var mapping = options.Value;
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var sourceCount = 0;
        foreach (var source in mapping.RoleSources)
        {
            if (sourceCount == ActorClaimMappingOptions.MaximumRoleSources)
            {
                ReportOnce(
                    "(overflow)",
                    "Only the first {MaximumRoleSources} role sources are evaluated; extra sources are ignored.",
                    ActorClaimMappingOptions.MaximumRoleSources);
                break;
            }

            sourceCount++;
            if (source is null || string.IsNullOrWhiteSpace(source.Path))
            {
                ReportOnce("(empty)", "A role source with an empty path was skipped.");
                continue;
            }

            var segments = source.Path.Split('.', StringSplitOptions.TrimEntries);
            if (segments.Length is 0 or > RoleClaimSource.MaximumSegments ||
                segments.Any(string.IsNullOrEmpty) ||
                segments.Count(static segment => segment == "*") > 1 ||
                segments[0] == "*")
            {
                ReportOnce(
                    source.Path,
                    "Role source path {RoleSourcePath} is not a bounded dotted path with at most one wildcard segment; it was skipped.",
                    source.Path);
                continue;
            }

            CollectSource(principal, source, segments, results, seen);
            if (results.Count >= CurrentActor.MaximumRoleCount)
            {
                break;
            }
        }

        // Nothing else is folded in. ADR-007 section 5 makes RoleClaimType a synthesized output, so
        // a raw claim of that type on an inbound token is never treated as an authorization-bearing
        // role: every role must come from a configured, prefix-disambiguated RoleSource.
        return results;
    }

    private void CollectSource(
        ClaimsPrincipal principal,
        RoleClaimSource source,
        string[] segments,
        List<string> results,
        HashSet<string> seen)
    {
        foreach (var claim in principal.FindAll(segments[0]))
        {
            if (segments.Length == 1)
            {
                AppendScalarOrArray(claim.Value, source, null, results, seen);
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(claim.Value, DocumentOptions);
            }
            catch (JsonException)
            {
                ReportOnce(
                    source.Path,
                    "Role source {RoleSourcePath} carried malformed provider JSON and was skipped.",
                    source.Path);
                continue;
            }

            using (document)
            {
                Navigate(document.RootElement, segments, 1, source, null, results, seen);
            }
        }
    }

    private void Navigate(
        JsonElement element,
        string[] segments,
        int index,
        RoleClaimSource source,
        string? wildcardSegment,
        List<string> results,
        HashSet<string> seen)
    {
        if (results.Count >= CurrentActor.MaximumRoleCount)
        {
            return;
        }

        if (index == segments.Length)
        {
            AppendElement(element, source, wildcardSegment, results, seen);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var segment = segments[index];
        if (segment == "*")
        {
            foreach (var property in element.EnumerateObject())
            {
                Navigate(property.Value, segments, index + 1, source, property.Name, results, seen);
                if (results.Count >= CurrentActor.MaximumRoleCount)
                {
                    return;
                }
            }

            return;
        }

        if (element.TryGetProperty(segment, out var next))
        {
            Navigate(next, segments, index + 1, source, wildcardSegment, results, seen);
        }
    }

    private void AppendElement(
        JsonElement element,
        RoleClaimSource source,
        string? wildcardSegment,
        List<string> results,
        HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        Add(results, seen, Prefix(item.GetString(), source, wildcardSegment));
                    }

                    if (results.Count >= CurrentActor.MaximumRoleCount)
                    {
                        return;
                    }
                }

                break;
            case JsonValueKind.String:
                Add(results, seen, Prefix(element.GetString(), source, wildcardSegment));
                break;
            default:
                break;
        }
    }

    private void AppendScalarOrArray(
        string value,
        RoleClaimSource source,
        string? wildcardSegment,
        List<string> results,
        HashSet<string> seen)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '[')
        {
            try
            {
                using var document = JsonDocument.Parse(value, DocumentOptions);
                AppendElement(document.RootElement, source, wildcardSegment, results, seen);
                return;
            }
            catch (JsonException)
            {
                ReportOnce(
                    source.Path,
                    "Role source {RoleSourcePath} carried malformed provider JSON and was skipped.",
                    source.Path);
                return;
            }
        }

        Add(results, seen, Prefix(value, source, wildcardSegment));
    }

    private static string? Prefix(string? role, RoleClaimSource source, string? wildcardSegment)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        var prefix = source.Prefix switch
        {
            RolePrefixMode.WildcardSegment => wildcardSegment,
            RolePrefixMode.Literal => source.LiteralPrefix,
            _ => null
        };

        return string.IsNullOrEmpty(prefix)
            ? role
            : string.Concat(prefix, source.PrefixSeparator ?? ":", role);
    }

    private static void Add(List<string> results, HashSet<string> seen, string? role)
    {
        if (string.IsNullOrWhiteSpace(role) ||
            role.Length > CurrentActor.MaximumRoleLength ||
            results.Count >= CurrentActor.MaximumRoleCount ||
            !seen.Add(role))
        {
            return;
        }

        results.Add(role);
    }

    private void ReportOnce(string key, string message, params object?[] arguments)
    {
        if (_reportedSources.TryAdd(key, 0))
        {
#pragma warning disable CA2254
            logger.LogWarning(message, arguments);
#pragma warning restore CA2254
        }
    }
}
