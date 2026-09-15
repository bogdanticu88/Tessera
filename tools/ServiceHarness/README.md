# ServiceHarness

Same situation as `tools/AdapterHarness`: real tests would be xunit, xunit is a NuGet package, NuGet restore isn't reachable from wherever this got written. Plain console app instead, no test framework, no NuGet dependency.

Three layers of checks, run in order:

1. `Hs256JwtValidator` directly: valid tokens, tampered signatures, wrong issuer/audience, expiry, algorithm confusion, and the type-confusion cases (a claim present but the wrong JSON type) that are easy to miss and easy to turn into an unhandled exception if missed.
2. `ClientOperations` directly, in-process, no HTTP: onboard, kill, restore, and the invariants that matter (idempotent kill, no resurrecting a killed client through onboard).
3. `LiveService_OnboardKillRestoreOverRealHttp`: builds `Tessera.Service` first (`dotnet build src/Tessera.Service`), then spawns it as a real subprocess on a random free port, waits for `/healthz`, and drives onboard, kill, and restore over actual HTTP with an actual signed JWT. This exercises the real ASP.NET pipeline, the auth filter, JSON binding, all of it, not just the logic underneath.

Run it with:

```bash
dotnet build src/Tessera.Service    # the live-service test needs the DLL already built
dotnet run --project tools/ServiceHarness
```

Once `dotnet restore` works wherever this is being built, port these into `tests/Tessera.ControlPlane.Tests` (or a new `tests/Tessera.Service.Tests`) as proper `[Fact]`s, the live-HTTP test can become a `WebApplicationFactory`-based integration test at that point instead of spawning a real process, and delete this folder.
