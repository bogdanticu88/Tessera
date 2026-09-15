# Builds and runs Tessera.Service, the HTTP surface (onboard, kill,
# restore, get). Multi-stage: the SDK image only exists to publish, the
# shipped image is the ASP.NET runtime plus the published output.
#
# Same caveat as NIA's Dockerfiles: dotnet build/test have run clean
# against this code (see tools/ServiceHarness), including a live process
# spawned directly, but this Dockerfile itself has not been through an
# actual "docker build", this sandbox has no Docker daemon and NuGet
# restore is blocked here too (see README, "OpenFGA adapter" section, and
# each tools/*/README.md), all of which is exactly why the workaround of
# building outside Docker first existed in the first place. Confirm this
# builds before relying on it.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Tessera.Service -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
# curl for the HEALTHCHECK below, the base aspnet image doesn't include
# it. /healthz needs no auth, it's checked before this container has any
# reason to have a valid token to present.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=5 \
  CMD curl -f http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Tessera.Service.dll"]
