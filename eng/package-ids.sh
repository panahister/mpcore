# The identifiers of every package MP Core ships, in one place. Sourced by eng/verify-release-artifacts.sh
# and eng/restore-api-baseline.sh so the two never drift against each other.

RUNTIME_IDS=(
  MPCore.Application MPCore.Audit.Abstractions MPCore.Audit.EntityFrameworkCore.PostgreSql
  MPCore.Caching.Abstractions MPCore.Caching.Hybrid MPCore.Caching.Memory MPCore.Caching.Redis MPCore.Domain
  MPCore.Hosting MPCore.Idempotency.EntityFrameworkCore.PostgreSql MPCore.Localization MPCore.Localization.EntityFrameworkCore.PostgreSql
  MPCore.Messaging.Abstractions MPCore.Messaging.Wolverine
  MPCore.Messaging.Wolverine.Kafka MPCore.Messaging.Wolverine.RabbitMQ MPCore.Observability MPCore.Observability.Prometheus
  MPCore.Persistence.Abstractions MPCore.Persistence.EntityFrameworkCore.PostgreSql MPCore.Persistence.Timescale
  MPCore.Resilience.Http MPCore.Security.Abstractions MPCore.Security.AspNetCore MPCore.Tenancy.Abstractions MPCore.Transport.Grpc
  MPCore.Transport.Http MPCore.Validation.FluentValidation
)
TOOL_IDS=(MPCore.Cli MPCore.Templates)
# Packages that first ship in this cohort, read from MPCoreNewInCohort in src/Directory.Build.props, the one list
# the build reads as well. They have no baseline at MPCoreBaselineVersion by definition, so their absence from the
# baseline store is expected, not a skipped validation. eng/restore-api-baseline.sh fails, and so does the build,
# while a package listed there has a baseline: empty the list when the next cohort is frozen.
NEW_IN_COHORT=()
_mpcore_new_in_cohort="$(sed -n 's/.*<MPCoreNewInCohort>\(.*\)<\/MPCoreNewInCohort>.*/\1/p' \
  "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/Directory.Build.props" | head -1)"
if [ -n "$_mpcore_new_in_cohort" ]; then IFS=';' read -r -a NEW_IN_COHORT <<< "$_mpcore_new_in_cohort"; fi
unset _mpcore_new_in_cohort
