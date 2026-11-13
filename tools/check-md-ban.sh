#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet test tests -c Release --filter "FullyQualifiedName~MarkdownBanTests"
