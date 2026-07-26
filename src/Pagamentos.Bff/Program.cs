using Microsoft.Extensions.Http.Resilience;
using Pagamentos.Bff.Features.Pagamentos.ConsultarPagamento;
using Pagamentos.Bff.Features.Pagamentos.CriarPagamento;
using Pagamentos.Bff.Infrastructure;
using Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddCorrelation();

// Nome de recurso do AppHost: sem porta, sem localhost. Os handlers de
// correlacao e resiliencia ja vem de ConfigureHttpClientDefaults.
// O ServiceDefaults adiciona o AddStandardResilienceHandler dentro de
// ConfigureHttpClientDefaults, ou seja, no builder default — cujo Name e
// vazio. As opcoes resultantes chamam-se "-standard" e sao compartilhadas
// por todos os clientes. Usar "{cliente}-standard" aqui seria aceito e
// silenciosamente ignorado.
// Vale enquanto o BFF tiver um unico cliente de saida; um segundo exigiria
// separar o handler por cliente.
const string OpcoesDeResiliencia = "-standard";

builder.Services.AddHttpClient<IPagamentosCoreClient, PagamentosCoreClient>(client =>
{
    client.BaseAddress = new Uri("https+http://pagamentos-core");
    client.Timeout = TimeSpan.FromSeconds(120);
});

// Retry pertence a camada mais interna, e o Core ja retenta o Proxy.
// Retentar de novo aqui multiplica as tentativas (3 x 3) e a latencia ate
// estourar o timeout total — foi assim que timeout e indisponibilidade
// viraram 500 apos 30s em vez do motivo correto.
builder.Services.Configure<HttpStandardResilienceOptions>(OpcoesDeResiliencia, options =>
{
    // MaxRetryAttempts tem minimo 1 e nao pode ser zerado. Desligar o retry
    // e feito pelo predicado: nada e considerado retentavel.
    options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);

    // Precisa acomodar os retries do Core sem estourar.
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(45);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(90);
});

var app = builder.Build();

app.MapDefaultEndpoints();

CriarPagamentoEndpoint.Map(app);
ConsultarPagamentoEndpoint.Map(app);

app.Run();

// Necessario para o WebApplicationFactory<Program> dos testes.
public partial class Program;
