using Tessera.ControlPlane;
using Xunit;

namespace Tessera.ControlPlane.Tests;

public class CanonicalFormTests
{
    private static CanonicalForm Canon() =>
        new(new EndpointCatalog(new[] { "orders/{param}/items", "orders" }));

    [Theory]
    [InlineData("  Demo-Client-1 ", "demo-client-1")]
    [InlineData("ACME.svc_02", "acme.svc_02")]
    public void ClientRef_is_trimmed_and_lowercased(string raw, string expected)
        => Assert.Equal(expected, CanonicalForm.ClientRef(raw));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("weird/slash")]
    public void ClientRef_rejects_invalid(string raw)
        => Assert.Throws<ArgumentException>(() => CanonicalForm.ClientRef(raw));

    [Fact]
    public void Method_allow_list_enforced()
    {
        Assert.Equal("get", CanonicalForm.Method("GET"));
        Assert.Throws<ArgumentException>(() => CanonicalForm.Method("TRACE"));
    }

    [Fact]
    public void Path_collapses_only_at_catalog_positions()
    {
        var c = Canon();
        Assert.Equal("orders/{param}/items", c.Path("/orders/12345/items"));
        Assert.Equal("orders/{param}/items", c.Path("orders/abc-guid/items"));
    }

    [Fact]
    public void Path_does_not_blanket_collapse_unknown_routes()
    {
        // Not in the catalog -> numeric segment is preserved, NOT collapsed (avoids cross-resource over-grant).
        var c = Canon();
        Assert.Equal("reports/2024", c.Path("/reports/2024"));
    }

    [Fact]
    public void Path_rejects_double_encoding_and_traversal()
    {
        var c = Canon();
        Assert.Throws<ArgumentException>(() => c.Path("orders/%252e%252e/items")); // residual % after decode
        Assert.Throws<ArgumentException>(() => c.Path("orders/../secrets"));
    }
}

public class ProvisionAndKillTests
{
    private static (ClientProvisioner prov, KillSwitchService kill, InMemoryAuthorizationStore store, InMemoryClientRegistry reg)
        Build()
    {
        var store = new InMemoryAuthorizationStore();
        var reg = new InMemoryClientRegistry();
        var locks = new InProcessClientLock();
        var canon = new CanonicalForm(new EndpointCatalog(new[] { "orders/{param}/items" }));
        var prov = new ClientProvisioner(store, reg, locks, canon);
        var kill = new KillSwitchService(store, reg, locks, new ConsoleAuditSink());
        return (prov, kill, store, reg);
    }

    private static ClientRecord Rec(string reff, string method, string path) =>
        new() { ClientRef = reff, Grants = new[] { Grant.ForEndpoint(method, path) } };

    [Fact]
    public async Task Reconcile_is_idempotent_and_diffs()
    {
        var (prov, _, store, _) = Build();
        await prov.ApplyAsync(Rec("c1", "get", "orders/1/items"));
        await prov.ApplyAsync(Rec("c1", "get", "orders/2/items")); // same canonical object; no error
        Assert.True(await store.CheckAsync("client:c1", "get", "api_endpoint:orders/{param}/items"));

        // Removing the grant deletes the tuple.
        await prov.ApplyAsync(new ClientRecord { ClientRef = "c1", Grants = Array.Empty<Grant>() });
        Assert.False(await store.CheckAsync("client:c1", "get", "api_endpoint:orders/{param}/items"));
    }

    [Fact]
    public async Task Kill_flips_check_and_restore_brings_it_back()
    {
        var (prov, kill, store, _) = Build();
        const string obj = "api_endpoint:orders/{param}/items";
        await prov.ApplyAsync(Rec("c1", "get", "orders/1/items"));
        Assert.True(await store.CheckAsync("client:c1", "get", obj));

        var r = await kill.KillAsync("c1", "INC-1", "op");
        Assert.Equal(1, r.TuplesDeleted);
        Assert.False(await store.CheckAsync("client:c1", "get", obj));

        await kill.RestoreAsync("c1", "op");
        await prov.ApplyAsync(Rec("c1", "get", "orders/1/items"));
        Assert.True(await store.CheckAsync("client:c1", "get", obj));
    }

    [Fact]
    public async Task Reconcile_does_not_resurrect_a_killed_client()
    {
        var (prov, kill, store, _) = Build();
        const string obj = "api_endpoint:orders/{param}/items";
        await prov.ApplyAsync(Rec("c1", "get", "orders/1/items"));
        await kill.KillAsync("c1", "INC-1", "op");

        await prov.ApplyAsync(Rec("c1", "get", "orders/1/items")); // reconcile after kill
        Assert.False(await store.CheckAsync("client:c1", "get", obj)); // still denied
    }

    [Fact]
    public async Task Kill_is_case_insensitive_at_the_boundary()
    {
        var (prov, kill, store, _) = Build();
        const string obj = "api_endpoint:orders/{param}/items";
        await prov.ApplyAsync(Rec("Acme-ES1", "get", "orders/1/items")); // canonicalizes to acme-es1
        Assert.True(await store.CheckAsync("client:acme-es1", "get", obj));

        var r = await kill.KillAsync("acme-es1", "INC-1", "op"); // different casing than input
        Assert.Equal(1, r.TuplesDeleted);
        Assert.False(await store.CheckAsync("client:acme-es1", "get", obj));
    }

    [Fact]
    public async Task Delete_removes_tuples_and_record()
    {
        var (prov, _, store, reg) = Build();
        await prov.ApplyAsync(Rec("c1", "get", "orders/1/items"));
        await prov.DeleteAsync("c1");
        Assert.False(await store.CheckAsync("client:c1", "get", "api_endpoint:orders/{param}/items"));
        Assert.Null(await reg.GetAsync("c1"));
    }
}
