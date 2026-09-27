using Grpc.Core;

namespace MPCore.Backend.Api.Grpc.Services;

/// <summary>
/// Transport-neutral scaffold probe. Business behavior starts only after the target Bounded Context
/// and Vertical Slice pass their required human gates.
/// </summary>
public sealed class PlatformProbeService : PlatformProbe.PlatformProbeBase
{
    public override Task<StatusReply> GetStatus(StatusRequest request, ServerCallContext context) =>
        Task.FromResult(new StatusReply { Service = "MPCore.Backend", Status = "ready" });
}
