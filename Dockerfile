# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Jarvis.AiGateway/Jarvis.AiGateway.csproj src/Jarvis.AiGateway/
RUN dotnet restore src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
COPY . .
RUN dotnet publish src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Production images should be pushed and deployed with immutable ECR tags or digests,
# not :latest. The ECS example documents the expected Git SHA/release tag pattern.
RUN addgroup --system --gid 64198 jarvis && \
    adduser --system --uid 64198 --ingroup jarvis --home /app --no-create-home jarvis
COPY --from=build --chown=jarvis:jarvis /app/publish .
USER jarvis
ENTRYPOINT ["dotnet", "Jarvis.AiGateway.dll"]
