using Tessera.ControlPlane;

// Wire the in-memory reference implementations (swap these for OpenFGA + your DB + your SIEM).
var store = new InMemoryAuthorizationStore();
var registry = new InMemoryClientRegistry();
var locks = new InProcessClientLock();
var audit = new ConsoleAuditSink();

// Your API surface as data - only these template positions collapse to {param}.
var catalog = new EndpointCatalog(new[] { "orders/{param}/items", "orders" });
var canon = new CanonicalForm(catalog);

var provisioner = new ClientProvisioner(store, registry, locks, canon);
var killSwitch = new KillSwitchService(store, registry, locks, audit);

const string client = "demo-client-1";
const string user = "client:demo-client-1";
const string obj = "api_endpoint:orders/{param}/items";

Console.WriteLine("== onboard + grant GET orders/{id}/items ==");
await provisioner.ApplyAsync(new ClientRecord
{
    ClientRef = client,
    Grants = new[] { Grant.ForEndpoint("get", "orders/12345/items") }, // concrete id canonicalizes to {param}
});
Console.WriteLine($"check GET  -> {await store.CheckAsync(user, "get", obj)}  (expect True)");

Console.WriteLine("\n== re-apply the SAME desired state (idempotent, must not error) ==");
await provisioner.ApplyAsync(new ClientRecord
{
    ClientRef = client,
    Grants = new[] { Grant.ForEndpoint("get", "orders/99999/items") },
});
Console.WriteLine($"check GET  -> {await store.CheckAsync(user, "get", obj)}  (expect True)");

Console.WriteLine("\n== KILL (delete the tuple) ==");
var result = await killSwitch.KillAsync(client, incident: "INC-1001", @operator: "oncall");
Console.WriteLine($"tuples deleted: {result.TuplesDeleted}");
Console.WriteLine($"check GET  -> {await store.CheckAsync(user, "get", obj)}  (expect False)");

Console.WriteLine("\n== reconcile must NOT resurrect a killed client ==");
await provisioner.ApplyAsync(new ClientRecord
{
    ClientRef = client,
    Grants = new[] { Grant.ForEndpoint("get", "orders/1/items") },
});
Console.WriteLine($"check GET  -> {await store.CheckAsync(user, "get", obj)}  (expect False - still killed)");

Console.WriteLine("\n== RESTORE, then reconcile ==");
await killSwitch.RestoreAsync(client, @operator: "oncall");
await provisioner.ApplyAsync(new ClientRecord
{
    ClientRef = client,
    Grants = new[] { Grant.ForEndpoint("get", "orders/1/items") },
});
Console.WriteLine($"check GET  -> {await store.CheckAsync(user, "get", obj)}  (expect True)");

Console.WriteLine("\nDone. Token was never touched - only the authorization tuple changed.");
