# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Jarvis.AiGateway/Jarvis.AiGateway.csproj src/Jarvis.AiGateway/
RUN dotnet restore src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
COPY . .
RUN dotnet publish src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Jarvis.AiGateway.dll"]
