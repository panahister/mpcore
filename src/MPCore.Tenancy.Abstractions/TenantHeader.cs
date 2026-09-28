namespace MPCore.Tenancy;

/// <summary>
/// How the tenant of a call travels from one service of a platform to another: in the request header
/// <c>x-tenant-id</c>, the name a message uses for the same thing. A service writes it when it calls
/// another on behalf of a tenant; the called service believes it only from a caller it was told to trust
/// (ADR-014, addendum on calls between services).
/// </summary>
public static class TenantHeader
{
    /// <summary>The header's name: <c>x-tenant-id</c>. Lower case, as HTTP/2 and gRPC metadata require.</summary>
    public const string Name = "x-tenant-id";

    /// <summary>The longest tenant identifier the header carries.</summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// Whether a value may travel in the header: 1 to <see cref="MaximumLength"/> characters, each an
    /// ASCII letter or digit, <c>-</c>, <c>_</c>, <c>.</c> or <c>:</c>. A tenant identifier is a name, not text.
    /// </summary>
    /// <param name="value">The tenant identifier.</param>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }
}
