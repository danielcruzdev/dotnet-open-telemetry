using Pagamentos.Core.Features.Pagamentos.ConsultarPagamento;
using Pagamentos.Core.Features.Pagamentos.CriarPagamento;
using Pagamentos.Core.Infrastructure;
using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddCorrelation();

// O nome do recurso e contrato com o AppHost. Sem porta, sem localhost:
// service discovery resolve na hora. O CorrelationIdHandler e o
// AddStandardResilienceHandler ja vem de ConfigureHttpClientDefaults
// (AddCorrelation e AddServiceDefaults) — registrar aqui duplicaria.
builder.Services.AddHttpClient<IFornecedorProxyClient, FornecedorProxyClient>(client =>
{
    client.BaseAddress = new Uri("https+http://pagamentos-proxy");
    client.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

app.MapDefaultEndpoints();

CriarPagamentoEndpoint.Map(app);
ConsultarPagamentoEndpoint.Map(app);

app.Run();

// Necessario para o WebApplicationFactory<Program> dos testes.
public partial class Program;
