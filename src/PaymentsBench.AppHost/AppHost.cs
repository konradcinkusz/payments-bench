// The composition root. Everything the system is made of is declared here and nowhere else:
// one PostgreSQL, the partner simulator, the API that talks to both, and - when it has a signing
// key - authservice, the one service that issues tokens.
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithDataVolume();
var paymentsDb = postgres.AddDatabase("payments");

var partner = builder.AddProject<Projects.PaymentsBench_PartnerSimulator>("partner");

var api = builder.AddProject<Projects.PaymentsBench_Api>("api")
    .WithReference(paymentsDb)
    .WithReference(partner)
    .WithEnvironment("Partner__BaseAddress", "http://partner")
    .WaitFor(paymentsDb)
    .WaitFor(partner);

// Identity is declared only when its signing key is configured:
//   dotnet user-secrets set Parameters:auth-signing-key "$(openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048)" --project src/PaymentsBench.AppHost
// Without it the API starts in its Development policy (anonymous allowed) and says so in its log.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:auth-signing-key"]))
{
    const string issuerAndAudience = "PaymentsBench";
    var signingKey = builder.AddParameter("auth-signing-key", secret: true);
    var authDb = postgres.AddDatabase("authdb");

    var authservice = builder.AddContainer("authservice", "ghcr.io/konradcinkusz/authservice", "v0.3.4") // pinned, never :latest
        .WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithReference(authDb, connectionName: "DefaultConnection")
        .WithEnvironment("DATABASE_PROVIDER", "PostgreSQL")
        .WithEnvironment("Jwt__PrivateKeyPem", signingKey)
        .WithEnvironment("Jwt__Issuer", issuerAndAudience)
        .WithEnvironment("Jwt__Audience", issuerAndAudience)
        .WithHttpHealthCheck("/health/ready")
        .WaitFor(authDb);

    api.WithEnvironment("Auth__Authority", authservice.GetEndpoint("http"))
        .WithEnvironment("Auth__Issuer", issuerAndAudience)
        .WithEnvironment("Auth__Audience", issuerAndAudience)
        .WaitFor(authservice);
}
else
{
    Console.WriteLine("AppHost: Parameters:auth-signing-key is not configured; authservice is not started and the API runs its Development policy (anonymous allowed).");
}

builder.Build().Run();
