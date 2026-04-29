#!/usr/bin/env bash
set -euo pipefail

# Repo root (directory containing this script)
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

EDITOR_PROJ="editor/LetsAdventure.WorldEditor/LetsAdventure.WorldEditor.csproj"
CONFIG="${CONFIG:-Debug}"

dotnet build "$EDITOR_PROJ" -c "$CONFIG" -t:Rebuild
dotnet run --project "$EDITOR_PROJ" -c "$CONFIG" --no-build
