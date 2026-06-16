#!/usr/bin/env bash
set -euo pipefail
dotnet restore ./src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
dotnet build ./src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release
