#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/RadioSipGateway.Linux/RadioSipGateway.Linux.csproj"
OUT="$ROOT/artifacts/linux-x64"
mkdir -p "$OUT"
dotnet publish "$PROJECT" -c Release -r linux-x64 --self-contained false -p:UseAppHost=true -o "$OUT"
printf '\nPublished files: %s\n' "$OUT"
printf 'Note: this scaffold currently supports config validation only; SIP/audio/PTT runtime is not implemented yet.\n'
