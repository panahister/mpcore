using System.Globalization;
using Grpc.Core;
using MPCore.Application.Results;

namespace MPCore.Transport.Grpc;

/// <summary>Renders a <see cref="FailureMessageDescriptor"/> in the negotiated culture.</summary>
public interface IGrpcFailureLocalizer
{
    /// <summary>Localizes a message descriptor.</summary>
    /// <param name="message">The message descriptor.</param>
    /// <param name="culture">The negotiated culture.</param>
    /// <returns>The localized text, or <see langword="null"/> to omit it.</returns>
    string? Localize(FailureMessageDescriptor message, CultureInfo culture);
}

/// <summary>Maps a product exception onto the transport-neutral failure model.</summary>
public interface IGrpcExceptionMapper
{
    /// <summary>Maps an exception.</summary>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="context">The server call context.</param>
    /// <returns>The failure, or <see langword="null"/> to defer to the next mapper.</returns>
    FailureDescriptor? Map(Exception exception, ServerCallContext context);
}

/// <summary>Decides whether a retry directive may be disclosed for a given call.</summary>
public interface IGrpcRetrySafetyPolicy
{
    /// <summary>Determines whether the call may safely be retried.</summary>
    /// <param name="failure">The failure being rendered.</param>
    /// <param name="context">The server call context.</param>
    bool AllowsRetry(FailureDescriptor failure, ServerCallContext context);
}

/// <summary>
/// The default adapter: renders through the transport-neutral <see cref="IFailureMessageLocalizer"/>
/// when one is registered (for example MP Core's message catalog), and renders nothing otherwise.
/// </summary>
internal sealed class FailureMessageGrpcLocalizer(IEnumerable<IFailureMessageLocalizer> localizers) : IGrpcFailureLocalizer
{
    private readonly IFailureMessageLocalizer? _localizer = localizers.LastOrDefault();

    public string? Localize(FailureMessageDescriptor message, CultureInfo culture) =>
        _localizer?.Localize(message, culture);
}

internal sealed class DenyGrpcRetrySafetyPolicy : IGrpcRetrySafetyPolicy
{
    public bool AllowsRetry(FailureDescriptor failure, ServerCallContext context) => false;
}
