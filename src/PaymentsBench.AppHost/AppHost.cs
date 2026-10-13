// The composition root. Everything the system is made of is declared here and nowhere else:
// one PostgreSQL, the partner simulator, and the API that talks to both.
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithDataVolume();
var paymentsDb = postgres.AddDatabase("payments");

var partner = builder.AddProject<Projects.PaymentsBench_PartnerSimulator>("partner");

builder.AddProject<Projects.PaymentsBench_Api>("api")
    .WithReference(paymentsDb)
    .WithReference(partner)
    .WithEnvironment("Partner__BaseAddress", "http://partner")
    .WaitFor(paymentsDb)
    .WaitFor(partner);

builder.Build().Run();
