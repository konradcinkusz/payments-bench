using PaymentsBench.Api;
using PaymentsBench.Infrastructure;
using PaymentsBench.ServiceDefaults;

// Program.cs is the manifest: what this service is made of, in the order it starts.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPaymentsInfrastructure(builder.Configuration);
builder.Services.AddPaymentsApi();

var app = builder.Build();
app.Services.LogStorageMode();
app.MapDefaultEndpoints();
app.UsePaymentsApi();

await app.RunAsync();
