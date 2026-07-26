using Pagamentos.Proxy.Features.Fornecedor.ProcessarPagamento;
using Pagamentos.Proxy.Infrastructure;
using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddCorrelation();

builder.Services.Configure<FornecedorOptions>(builder.Configuration.GetSection(FornecedorOptions.Secao));
builder.Services.AddSingleton<FornecedorSimulado>();

var app = builder.Build();

app.MapDefaultEndpoints();

ProcessarPagamentoEndpoint.Map(app);

app.Run();

// Necessario para o WebApplicationFactory<Program> dos testes.
public partial class Program;
