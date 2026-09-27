using Microsoft.AspNetCore.Http;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Resolves the current actor from <see cref="HttpContext.User"/> exactly once per request and
/// caches the result in <see cref="HttpContext.Items"/>. gRPC services and REST endpoints share one
/// implementation because both are served by the same ASP.NET Core request pipeline.
/// </summary>
/// <remarks>
/// Outside a request — message handlers, hosted services, tests — the accessor returns
/// <see cref="CurrentActor.Anonymous"/>. Actor propagation across asynchronous messaging is
/// deliberately out of scope; a product that needs it registers its own
/// <see cref="ICurrentActorAccessor"/>.
/// </remarks>
internal sealed class HttpContextCurrentActorAccessor(
    IHttpContextAccessor httpContextAccessor,
    ClaimsPrincipalActorMapper mapper) : ICurrentActorAccessor
{
    internal static readonly object ItemsKey = new();

    public CurrentActor Current
    {
        get
        {
            var context = httpContextAccessor.HttpContext;
            if (context is null)
            {
                // Outside a request — a job, a consumer — the actor is whatever system scope is open.
                return SystemActorScope.Current ?? CurrentActor.Anonymous;
            }

            if (context.Items.TryGetValue(ItemsKey, out var cached) && cached is CurrentActor actor)
            {
                return actor;
            }

            actor = mapper.Map(context.User);
            context.Items[ItemsKey] = actor;
            return actor;
        }
    }
}
