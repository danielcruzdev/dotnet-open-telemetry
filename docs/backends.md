# Exportando para Dynatrace, Datadog e outros

Nada neste projeto é específico do Aspire Dashboard. O contrato de correlação são atributos OTLP comuns — `correlation.id` no span, `CorrelationId` no log — então chegam iguais em qualquer backend que fale OTLP.

**Trocar de destino não exige mudança de código.** O `ServiceDefaults` só liga o exportador quando `OTEL_EXPORTER_OTLP_ENDPOINT` está preenchido, e `UseOtlpExporter()` respeita as variáveis padrão do OpenTelemetry:

```csharp
var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
if (useOtlpExporter)
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}
```

---

## ⚠ Leia isto antes: gRPC não serve

O default do exportador .NET é **gRPC**, e é o que o Aspire Dashboard usa. Mas:

| Destino | gRPC? |
|---|---|
| Aspire Dashboard | ✅ (default) |
| Dynatrace | ❌ — só HTTP com protobuf **binário** (JSON também não) |
| Datadog via **Agent** | ✅ |
| Datadog **ingestão direta** | ❌ — só `http/protobuf` |
| OTel Collector | ✅ |

Esquecer `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` falha do jeito mais chato: os serviços sobem, `/health` responde, e a telemetria simplesmente não aparece. É o mesmo modo de falha do certificado não confiável da Fase 0 — silêncio total.

---

## Dynatrace

### O que você precisa

1. O **environment ID** (o `abc12345` de `https://abc12345.live.dynatrace.com`).
2. Um **token de acesso** com os escopos do que for exportar:

| Sinal | Escopo |
|---|---|
| Traces | `openTelemetryTrace.ingest` |
| Métricas | `metrics.ingest` |
| Logs | `logs.ingest` |

Um único token pode combinar os três.

### Exemplo 1 — pelo AppHost (recomendado em dev)

O Aspire injeta `OTEL_EXPORTER_OTLP_ENDPOINT` apontando para o próprio dashboard. Para mandar ao Dynatrace, sobrescreva **por recurso**. `WithEnvironment` vence a injeção do Aspire — verificado.

Como são três serviços, vale uma extensão para não repetir:

```csharp
// src/Pagamentos.AppHost/AppHost.cs
var builder = DistributedApplication.CreateBuilder(args);

var dynatraceUrl   = builder.Configuration["Dynatrace:Url"];    // https://abc12345.live.dynatrace.com/api/v2/otlp
var dynatraceToken = builder.Configuration["Dynatrace:Token"];  // dt0c01.XXXX

var proxy = builder.AddProject<Projects.Pagamentos_Proxy>("pagamentos-proxy")
    .ComDynatrace(dynatraceUrl, dynatraceToken);

var core = builder.AddProject<Projects.Pagamentos_Core>("pagamentos-core")
    .WithReference(proxy).WaitFor(proxy)
    .ComDynatrace(dynatraceUrl, dynatraceToken);

builder.AddProject<Projects.Pagamentos_Bff>("pagamentos-bff")
    .WithReference(core).WaitFor(core)
    .WithExternalHttpEndpoints()
    .ComDynatrace(dynatraceUrl, dynatraceToken);

builder.Build().Run();

static class DynatraceExtensions
{
    public static IResourceBuilder<ProjectResource> ComDynatrace(
        this IResourceBuilder<ProjectResource> recurso, string? url, string? token)
    {
        // Sem configuracao, segue para o dashboard do Aspire.
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return recurso;

        return recurso
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", url)
            // Dynatrace nao aceita gRPC. Sem esta linha, nada chega.
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithEnvironment("OTEL_EXPORTER_OTLP_HEADERS", $"Authorization=Api-Token {token}");
    }
}
```

O token fica fora do código — em user secrets:

```bash
cd src/Pagamentos.AppHost
dotnet user-secrets set "Dynatrace:Url"   "https://abc12345.live.dynatrace.com/api/v2/otlp"
dotnet user-secrets set "Dynatrace:Token" "dt0c01.XXXXXXXX.YYYYYYYY"
dotnet run
```

Note a URL: é a **base**, sem `/v1/traces`. O exportador acrescenta o sufixo do sinal sozinho.

### Exemplo 2 — sem o AppHost, por variável de ambiente

Rodando um serviço direto (container, VM, pipeline):

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="https://abc12345.live.dynatrace.com/api/v2/otlp"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Api-Token dt0c01.XXXXXXXX.YYYYYYYY"
export OTEL_SERVICE_NAME="pagamentos-core"

dotnet run --project src/Pagamentos.Core
```

No PowerShell:

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "https://abc12345.live.dynatrace.com/api/v2/otlp"
$env:OTEL_EXPORTER_OTLP_PROTOCOL = "http/protobuf"
$env:OTEL_EXPORTER_OTLP_HEADERS  = "Authorization=Api-Token dt0c01.XXXXXXXX.YYYYYYYY"
dotnet run --project src/Pagamentos.Core
```

Se usar **ActiveGate** em vez de SaaS, só a URL muda:

```
https://{activegate}:9999/e/{environment-id}/api/v2/otlp
```

### Como achar sua requisição no Dynatrace

O `correlation.id` chega como atributo de span. Em **Distributed Traces**, filtre por ele — ou use DQL:

```
fetch spans
| filter correlation.id == "minha-investigacao"
| sort start_time asc
| fields start_time, service.name, span.name, erro.motivo, duration
```

E para ver os logs da mesma requisição:

```
fetch logs
| filter CorrelationId == "minha-investigacao"
| sort timestamp asc
| fields timestamp, service.name, loglevel, content, Motivo
```

O Dynatrace liga log↔trace sozinho pelo `TraceId`/`SpanId` que já vão nos logs.

---

## Datadog

Dois caminhos. O do Agent é o mais comum e aceita gRPC; o direto dispensa Agent mas exige `http/protobuf`.

### Caminho A — Agent com receptor OTLP

O Agent pode subir como um recurso do próprio Aspire, o que deixa o `dotnet run` continuar sendo um comando só:

```csharp
// src/Pagamentos.AppHost/AppHost.cs
var builder = DistributedApplication.CreateBuilder(args);

var ddApiKey = builder.AddParameter("dd-api-key", secret: true);

var agent = builder.AddContainer("datadog-agent", "gcr.io/datadoghq/agent", "7")
    .WithEnvironment("DD_API_KEY", ddApiKey)
    .WithEnvironment("DD_SITE", "datadoghq.com")          // datadoghq.eu se for UE
    .WithEnvironment("DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_GRPC_ENDPOINT", "0.0.0.0:4317")
    .WithEnvironment("DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_HTTP_ENDPOINT", "0.0.0.0:4318")
    // logs precisam ser ligados nos DOIS lugares
    .WithEnvironment("DD_LOGS_ENABLED", "true")
    .WithEnvironment("DD_OTLP_CONFIG_LOGS_ENABLED", "true")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc");

var proxy = builder.AddProject<Projects.Pagamentos_Proxy>("pagamentos-proxy")
    .ComDatadogAgent(agent);

var core = builder.AddProject<Projects.Pagamentos_Core>("pagamentos-core")
    .WithReference(proxy).WaitFor(proxy)
    .ComDatadogAgent(agent);

builder.AddProject<Projects.Pagamentos_Bff>("pagamentos-bff")
    .WithReference(core).WaitFor(core)
    .WithExternalHttpEndpoints()
    .ComDatadogAgent(agent);

builder.Build().Run();

static class DatadogExtensions
{
    public static IResourceBuilder<ProjectResource> ComDatadogAgent(
        this IResourceBuilder<ProjectResource> recurso,
        IResourceBuilder<ContainerResource> agent) =>
        recurso
            .WaitFor(agent)
            // Aqui o gRPC default serve: o Agent aceita os dois.
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT",
                agent.GetEndpoint("otlp-grpc"));
}
```

```bash
dotnet user-secrets set "Parameters:dd-api-key" "sua-api-key"
dotnet run --project src/Pagamentos.AppHost
```

Preferindo docker-compose ao Aspire para o Agent:

```yaml
# docker-compose.datadog.yml
services:
  datadog-agent:
    image: gcr.io/datadoghq/agent:7
    environment:
      DD_API_KEY: ${DD_API_KEY}
      DD_SITE: datadoghq.com
      DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_GRPC_ENDPOINT: 0.0.0.0:4317
      DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_HTTP_ENDPOINT: 0.0.0.0:4318
      DD_LOGS_ENABLED: "true"
      DD_OTLP_CONFIG_LOGS_ENABLED: "true"
    ports:
      - "4317:4317"
      - "4318:4318"
```

```bash
docker compose -f docker-compose.datadog.yml up -d
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Pagamentos.Core
```

### Caminho B — ingestão direta, sem Agent

Aqui **gRPC não é aceito**, e a configuração é por sinal:

```bash
export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="<endpoint-de-traces-do-seu-site>"
export OTEL_EXPORTER_OTLP_TRACES_HEADERS="dd-api-key=${DD_API_KEY},compute_stats=true"

export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_LOGS_ENDPOINT="<endpoint-de-logs-do-seu-site>"
export OTEL_EXPORTER_OTLP_LOGS_HEADERS="dd-api-key=${DD_API_KEY}"

export OTEL_SERVICE_NAME="pagamentos-core"
dotnet run --project src/Pagamentos.Core
```

Os endpoints variam por site (`datadoghq.com`, `datadoghq.eu`, `us3`, …) — confira na documentação do Datadog para o seu. `compute_stats=true` habilita as métricas de trace do APM.

### Como achar sua requisição no Datadog

O `service.name` do OTel vira o campo `service`, então os três serviços aparecem separados no APM.

No **APM → Traces**, busque:

```
@correlation.id:minha-investigacao
```

Em **Logs**:

```
@CorrelationId:minha-investigacao
```

Duas coisas que valem saber antes:

- O Datadog lê `trace_id`/`span_id` dos logs OTLP e liga log↔trace automaticamente — suporta o formato nativo do OTel (trace de 128 bits em hex).
- Para usar `correlation.id` em dashboards, monitores ou como filtro salvo, promova o atributo a **facet**. Sem isso ele aparece no span mas não é agregável.

---

## Mandando para dois lugares ao mesmo tempo

Se você quiser manter o Aspire Dashboard em dev **e** enviar para o fornecedor, ponha um **OTel Collector** no meio. Os serviços exportam para ele em gRPC — sem armadilha de protocolo — e o Collector cuida da conversão por destino.

```yaml
# otel-collector.yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317

processors:
  batch:
    timeout: 5s

exporters:
  # Dynatrace exige http/protobuf — o Collector converte.
  otlphttp/dynatrace:
    endpoint: https://abc12345.live.dynatrace.com/api/v2/otlp
    headers:
      Authorization: "Api-Token ${env:DYNATRACE_TOKEN}"

  otlp/datadog-agent:
    endpoint: datadog-agent:4317
    tls:
      insecure: true

  otlp/aspire:
    endpoint: ${env:ASPIRE_OTLP_ENDPOINT}

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp/dynatrace, otlp/datadog-agent, otlp/aspire]
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp/dynatrace, otlp/datadog-agent, otlp/aspire]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlphttp/dynatrace, otlp/datadog-agent, otlp/aspire]
```

Os serviços continuam com o default:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"   # grpc, sem surpresa
```

É também o caminho quando o backend exige transformação — renomear atributos, remover campos, amostrar.

---

## Confirmando que chegou

Não confie no silêncio: falha de export **não derruba o serviço**. `/health` continua verde com zero telemetria chegando.

### 1. Dispare o cenário que exercita tudo de uma vez

O valor `999.98` produz trace, log de erro e retry numa tacada:

```bash
curl -k -X POST https://localhost:<porta-bff>/pagamentos \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: teste-backend-novo" \
  -d '{"chavePix":"usuario@exemplo.com","valor":999.98,"descricao":"teste"}'
```

Procure `teste-backend-novo` no backend novo. Deve aparecer um trace com 3 serviços e as 4 tentativas de retry.

### 2. Se não aparecer, ligue o self-diagnostics do OTel

Crie `OTEL_DIAGNOSTICS.json` ao lado do binário do serviço:

```json
{ "LogDirectory": ".", "FileSize": 1024, "LogLevel": "Warning" }
```

Rode, e filtre o log gerado:

```bash
grep -iE "export|certific|ssl|refused|unauthenticated|401|403" *.log
```

Erros típicos:

| Mensagem | Causa |
|---|---|
| `UntrustedRoot` / SSL | certificado não confiável (dev) ou cadeia corporativa |
| `401` / `403` | token errado, ou faltando escopo (`openTelemetryTrace.ingest`) |
| `404` no `/v1/traces` | endpoint com sufixo duplicado — use a URL base |
| gRPC `Unimplemented` | você esqueceu `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` |

Apague o arquivo depois — ele enche 1 MB com ruído de MsQuic.

### 3. Rodando sob o AppHost

Lembre que o Aspire injeta o endpoint dele. Sem sobrescrever por recurso, tudo continua indo para o dashboard e nada chega ao fornecedor.

---

## O que foi verificado aqui

Para separar o que testei do que segue a documentação dos fornecedores:

✔ **Verificado nesta máquina**
- Trocar endpoint, protocolo e headers **só por variável de ambiente**, sem alterar código: chegaram `POST /v1/traces` e `/v1/logs` com `application/x-protobuf` e os headers customizados.
- `WithEnvironment` no AppHost **sobrescreve** a injeção do Aspire: com o Proxy configurado para um listener local, chegaram `/v1/logs` e `/v1/metrics` com `Authorization=Api-Token …` e `application/x-protobuf`.

📄 **Conforme a documentação dos fornecedores, não testado contra conta real**
- Endpoints, escopos de token e restrição de protocolo do Dynatrace.
- Variáveis do Agent do Datadog, endpoint de ingestão direta e correlação log↔trace.

## Fontes

- [Dynatrace — OTLP API endpoints](https://docs.dynatrace.com/docs/ingest-from/opentelemetry/otlp-api)
- [Dynatrace — Export with OTLP](https://docs.dynatrace.com/docs/ingest-from/opentelemetry/getting-started/otlp-export)
- [Datadog — OTLP Ingestion by the Datadog Agent](https://docs.datadoghq.com/opentelemetry/setup/otlp_ingest_in_the_agent/)
- [Datadog — OTLP Intake Endpoint](https://docs.datadoghq.com/opentelemetry/setup/otlp_ingest/)
- [Datadog — Correlate OpenTelemetry Traces and Logs](https://docs.datadoghq.com/opentelemetry/correlate/logs_and_traces/)
