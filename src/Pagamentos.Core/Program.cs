using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddCorrelation();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
