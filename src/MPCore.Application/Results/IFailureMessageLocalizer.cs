using System.Globalization;

namespace MPCore.Application.Results;

/// <summary>
/// Renders a <see cref="FailureMessageDescriptor"/> as text in one culture. Transport-neutral: the HTTP
/// and gRPC adapters both use the registered implementation to fill their human-readable members.
/// </summary>
/// <remarks>
/// The application layer never produces a sentence. It returns a stable key and bounded arguments, and
/// the edge renders them in the language the caller negotiated. This separation follows RFC 9457
/// (Problem Details), where <c>title</c> and <c>detail</c> are presentation text while clients branch
/// on stable identifiers. When nothing is registered, no text is rendered and the transports omit the
/// human-readable members.
/// </remarks>
public interface IFailureMessageLocalizer
{
    /// <summary>Renders the message in the culture, or returns null when no text exists for it.</summary>
    /// <param name="message">The message key and arguments.</param>
    /// <param name="culture">The culture the caller negotiated.</param>
    string? Localize(FailureMessageDescriptor message, CultureInfo culture);
}
