#!/usr/bin/env bash
# Prints a short Markdown test summary from the TRX files in a directory (default: TestResults).
# Usage: .github/scripts/test-summary.sh [dir] >> "$GITHUB_STEP_SUMMARY"
dir="${1:-TestResults}"
echo "## Test results"
shopt -s nullglob globstar
files=("$dir"/**/*.trx)
if [ ${#files[@]} -eq 0 ]; then
  echo "No TRX files found in \`$dir\` (build or test run failed before tests executed)."
  exit 0
fi
for f in "${files[@]}"; do
  counters=$(grep -o '<Counters [^>]*>' "$f" | head -n1)
  get() { echo "$counters" | grep -o "$1=\"[0-9]*\"" | grep -o '[0-9]*'; }
  echo "- \`$(basename "$f")\`: total $(get total), passed $(get passed), failed $(get failed), not executed $(get notExecuted)"
  # List failed tests (name only).
  grep -o '<UnitTestResult [^>]*outcome="Failed"[^>]*>' "$f" | grep -o 'testName="[^"]*"' | sed 's/^testName="\(.*\)"$/  - FAILED: \1/'
done
