# AdapterHarness

A stand-in for real tests. This repo's test project (`tests/Tessera.ControlPlane.Tests`) uses xunit, which is a NuGet package, and NuGet restore isn't reachable from wherever this got written. So this exists as a plain console app, no test framework, no NuGet dependency, that runs real assertions against `OpenFgaAuthorizationStore` using a fake `HttpMessageHandler` and prints PASS/FAIL per case, exiting non-zero if anything fails.

Run it with:

```bash
dotnet run --project tools/AdapterHarness
```

Once `dotnet restore` works wherever this is being built (it needs real internet access to nuget.org, not a proxied or sandboxed shell), port these cases into `tests/Tessera.ControlPlane.Tests/OpenFgaAuthorizationStoreTests.cs` as proper `[Fact]`s and delete this folder. The assertions themselves don't need to change, only the harness they run in.
