#!/bin/bash
# Proves the consumer-side check of eng/consumer/MPCore.PinnedCommit.targets against packed packages.
#
#   ./eng/verify-consumer-pin.sh <packages-dir> <version> [expected-commit]
#
# A throw-away consumer project references MPCore.Security.AspNetCore <version> (and through it
# MPCore.Security.Abstractions and MPCore.Tenancy.Abstractions) from <packages-dir> only, and imports the
# check. The build must pass when it pins the commit the packages record, and fail, naming the package,
# when it pins another commit or a short one. With [expected-commit], the packages must record exactly it.
#
# The NuGet cache and the CLI home are created for this run and removed when it ends (ADR-010, section 1:
# an isolated verification environment), so no package reaches the default global cache.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGES="$(cd "${1:?usage: verify-consumer-pin.sh <packages-dir> <version> [expected-commit]}" && pwd)"
V="${2:?usage: verify-consumer-pin.sh <packages-dir> <version> [expected-commit]}"
EXPECTED="${3:-}"
PACKAGE=MPCore.Security.AspNetCore

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
export NUGET_PACKAGES="$WORK/nuget-packages"
export DOTNET_CLI_HOME="$WORK/cli-home"
export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=1

recorded="$(unzip -p "$PACKAGES/$PACKAGE.$V.nupkg" "$PACKAGE.nuspec" \
  | sed -n 's/.*<repository[^>]* commit="\([0-9a-f]\{40\}\)".*/\1/p' | head -1)"
[ -n "$recorded" ] || { echo "FAIL  $PACKAGE $V records no commit"; exit 1; }
if [ -n "$EXPECTED" ] && [ "$recorded" != "$EXPECTED" ]; then
  echo "FAIL  $PACKAGE $V records $recorded, not the expected $EXPECTED"; exit 1
fi
other="$(printf '%s' "$recorded" | tr '0-9a-f' '1-9a-f0')"

CONSUMER="$WORK/consumer"
mkdir -p "$CONSUMER"
cp "$ROOT/global.json" "$CONSUMER/global.json"
cp "$ROOT/eng/consumer/MPCore.PinnedCommit.targets" "$CONSUMER/MPCore.PinnedCommit.targets"
cat > "$CONSUMER/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$PACKAGES" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="MPCore.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
XML
cat > "$CONSUMER/Directory.Build.targets" <<'XML'
<Project>
  <Import Project="MPCore.PinnedCommit.targets" />
</Project>
XML
cat > "$CONSUMER/Consumer.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="$PACKAGE" Version="[$V]" />
  </ItemGroup>
</Project>
XML
printf 'public static class Probe { public static object Actor => MPCore.Security.CurrentActor.Anonymous; }\n' > "$CONSUMER/Probe.cs"

failed=0
dotnet restore "$CONSUMER/Consumer.csproj" > "$WORK/restore.log" 2>&1 || { cat "$WORK/restore.log"; echo "FAIL  restore"; exit 1; }

build() { dotnet build "$CONSUMER/Consumer.csproj" --no-restore -p:MPCorePinnedCommit="$1" > "$WORK/build.log" 2>&1; }

if build "$recorded"; then
  echo "PASS  a build that pins the recorded commit $recorded succeeds"
else
  echo "FAIL  a build that pins the recorded commit failed:"; grep -E "error" "$WORK/build.log" | head -5; failed=1
fi

if build "$other"; then
  echo "FAIL  a build that pins another commit was accepted"; failed=1
elif grep -q "records commit '$recorded', not the pinned $other" "$WORK/build.log"; then
  echo "PASS  a build that pins another commit fails and names the package and both commits"
else
  echo "FAIL  a build that pins another commit failed for another reason:"; grep -E "error" "$WORK/build.log" | head -5; failed=1
fi

if build "${recorded:0:7}"; then
  echo "FAIL  a short commit was accepted"; failed=1
elif grep -q "must be a full commit SHA" "$WORK/build.log"; then
  echo "PASS  a short commit is refused"
else
  echo "FAIL  a short commit failed for another reason:"; grep -E "error" "$WORK/build.log" | head -5; failed=1
fi

if build ""; then
  echo "PASS  without a pin the check does nothing"
else
  echo "FAIL  a build without a pin failed:"; grep -E "error" "$WORK/build.log" | head -5; failed=1
fi

[ "$failed" -eq 0 ] && { echo "consumer pin VERIFIED for $V at $recorded"; exit 0; }
echo "consumer pin FAILED"; exit 1
