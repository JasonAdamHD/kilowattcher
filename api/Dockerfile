# Stage 1: build with the full .NET SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until the project file changes.
COPY Kilowattcher.Api.csproj ./
RUN dotnet restore Kilowattcher.Api.csproj

COPY . ./
RUN dotnet publish Kilowattcher.Api.csproj -c Release -o /app --no-restore

# Stage 2: run on the chiseled ASP.NET runtime: no shell, no package manager,
# and a non-root user by default. Secrets are passed at runtime, never baked in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app ./

# Non-root can't bind port 80, so the app listens on PORT (default 8080).
ENV PORT=8080
USER $APP_UID

ENTRYPOINT ["dotnet", "Kilowattcher.Api.dll"]
