#!/usr/bin/env bash
# Phase 0 acceptance: a deliberately added Process.Start in a collector MUST fail the build with RS0030.
# Adds a temporary file, builds, expects failure, always removes the file again.
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
canary="$root/src/RSM.Collectors.Sql/__AnalyzerCanary.cs"
trap 'rm -f "$canary"' EXIT

cat > "$canary" <<'CS'
namespace RSM.Collectors.Sql;
internal static class AnalyzerCanary
{
    public static void Run() => System.Diagnostics.Process.Start("cmd.exe", "/c whoami");
}
CS

output="$(dotnet build "$root/src/RSM.Collectors.Sql/RSM.Collectors.Sql.csproj" -c Release --no-incremental 2>&1)"
status=$?
if [ $status -ne 0 ] && grep -q "error RS0030" <<<"$output"; then
  echo "analyzer canary: OK (build failed with RS0030 as required)"
  exit 0
fi

echo "analyzer canary: FAILED — adding Process.Start did not break the build with RS0030" >&2
echo "$output" | tail -20 >&2
exit 1
