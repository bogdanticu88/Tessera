# Builds and runs Tessera.Service, the HTTP surface (onboard, kill,
# restore, get). Multi-stage: the SDK image only exists to publish, the
# shipped image is the ASP.NET runtime plus the published output.
#
# This has been through an actual `docker build` and run, on a machine
# with both Docker and working NuGet access, neither of which this
# sandbox has (see README, "OpenFGA adapter" section, and each
# tools/*/README.md, for why the workaround of building outside Docker
# first existed here in the first place). The resulting container came
# up healthy, onboarded a client against a real OpenFGA instance, and
# handled a kill against it, real tuples written and deleted, not the
# in-memory store. That run is what caught the two bugs fixed in this
# file's own history, right after this Dockerfile was added: an
# IAuthorizationStore dependency injection mistake, and
# ReadTuplesForClientAsync filtering OpenFGA's read endpoint by user
# alone, which OpenFGA rejects. See NIA's docs/ARCHITECTURE.md for the
# full account, that repo's compose stack is what actually exercised
# this image.
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
