# Exportando para outros backends

Nada neste projeto é específico do Aspire Dashboard. O contrato de correlação são atributos OTLP comuns — `correlation.id` no span, `CorrelationId` no log — então chegam iguais em qualquer backend que fale OTLP.

**Trocar de destino não exige mudança de código.** O `ServiceDefaults` só liga o exportador quando `OTEL_EXPORTER_OTLP_ENDPOINT` está preenchido:

```csharp
var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
if (useOtlpExporter)
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}
```

`UseOtlpExporter()` respeita as variáveis padrão do OpenTelemetry. Basta apontá-las.

## ⚠ A armadilha: gRPC não serve

O padrão do exportador .NET é **gRPC**, e é o que o Aspire Dashboard usa. Mas:

- **Dynatrace** não aceita gRPC. Exige HTTP com protobuf **binário** (JSON também não serve).
- **Datadog**, no endpoint de ingestão direta, também não aceita gRPC.

Esquecer `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` é o erro mais provável aqui — e ele falha do jeito mais chato: os serviços sobem, respondem normalmente, e a telemetria simplesmente não aparece. Para diagnosticar, use o self-diagnostics do OTel descrito na skill [`run-stack`](../.claude/skills/run-stack/SKILL.md).

### Verificado

Rodando o Proxy com as variáveis abaixo e um listener HTTP local no lugar do fornecedor, sem alterar uma linha de código:

```
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:5599
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Api-Token dt0c01.FAKE,x-teste=valor
```

O que chegou no listener:

```
PATH=/v1/traces   TYPE=application/x-protobuf
PATH=/v1/logs     TYPE=application/x-protobuf
HEADERS=Authorization=Api-Token dt0c01.FAKE | x-teste=valor
        User-Agent=OTel-OTLP-Exporter-Dotnet/1.15.3
```

Ou seja: o sufixo do sinal (`/v1/traces`, `/v1/logs`) é acrescentado sozinho, o protobuf binário é usado, e os headers passam. É esse o mecanismo em que as duas seções abaixo se apoiam.

---

## Dynatrace

### Endpoint

| Modo | URL base |
|---|---|
| SaaS | `https://{environment-id}.live.dynatrace.com/api/v2/otlp` |
| ActiveGate | `https://{activegate}:9999/e/{environment-id}/api/v2/otlp` |

Use a URL **base**, sem `/v1/traces` — o exportador acrescenta o sufixo do sinal.

### Token

Crie um token de acesso com os escopos do que for exportar:

| Sinal | Escopo |
|---|---|
| Traces | `openTelemetryTrace.ingest` |
| Métricas | `metrics.ingest` |
| Logs | `logs.ingest` |

Um único token pode combinar os três.

### Variáveis

```bash
OTEL_EXPORTER_OTLP_ENDPOINT="https://{environment-id}.live.dynatrace.com/api/v2/otlp"
OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
OTEL_EXPORTER_OTLP_HEADERS="Authorization=Api-Token dt0c01.XXXX"
```

### O que esperar

- O `correlation.id` vira atributo de span pesquisável. Filtre por ele para achar a requisição inteira.
- Os logs chegam com `TraceId`/`SpanId` e o Dynatrace liga log↔trace sozinho.
- `service.name` (`pagamentos-bff`, `pagamentos-core`, `pagamentos-proxy`) separa os três serviços.

---

## Datadog

Há dois caminhos. O do Agent é o mais comum; o direto serve quando não dá para rodar Agent.

### Caminho A — Agent com receptor OTLP

O Agent aceita OTLP em gRPC **e** HTTP, então aqui o protocolo default funciona.

Configuração do Agent:

```bash
DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_GRPC_ENDPOINT=0.0.0.0:4317
DD_OTLP_CONFIG_RECEIVER_PROTOCOLS_HTTP_ENDPOINT=0.0.0.0:4318

# logs precisam ser ligados nos dois lugares
DD_LOGS_ENABLED=true
DD_OTLP_CONFIG_LOGS_ENABLED=true
```

Nos serviços:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT="http://{host-do-agent}:4317"
# protocolo default (grpc) serve neste caminho
```

### Caminho B — ingestão direta, sem Agent

Aqui **gRPC não é aceito**:

```bash
OTEL_EXPORTER_OTLP_TRACES_PROTOCOL="http/protobuf"
OTEL_EXPORTER_OTLP_TRACES_ENDPOINT="{endpoint-do-seu-site-datadog}"
OTEL_EXPORTER_OTLP_TRACES_HEADERS="dd-api-key=${DD_API_KEY},compute_stats=true"
```

O endpoint varia conforme o site da sua conta (`datadoghq.com`, `datadoghq.eu`, …) — confira na documentação do Datadog para o seu site. `compute_stats=true` habilita as métricas de trace.

### O que esperar

- O Datadog lê `trace_id` e `span_id` dos logs OTLP e liga log↔trace automaticamente. Ele suporta o formato nativo do OTel (trace de 128 bits em hex).
- O `correlation.id` chega como atributo de span; para usá-lo em dashboards e monitores, pode ser preciso promovê-lo a facet.
- `service.name` alimenta o campo `service` do Datadog, então os três serviços aparecem separados no APM.

---

## Coletor no meio

Se você quiser enviar para mais de um destino, ou desacoplar os serviços do fornecedor, ponha um **OpenTelemetry Collector** no meio: os serviços exportam para ele em gRPC (o default, sem armadilha de protocolo) e o Collector cuida do fan-out e da conversão de protocolo por destino.

É também o caminho recomendado quando o backend exige transformação — renomear atributos, remover campos, amostrar.

---

## Antes de trocar de backend

1. Confirme o protocolo. gRPC quebra em Dynatrace e no endpoint direto do Datadog.
2. Rodando sob o AppHost, o Aspire injeta `OTEL_EXPORTER_OTLP_ENDPOINT` apontando para o dashboard. Para mandar a um fornecedor, rode fora do AppHost ou defina a variável explicitamente por recurso no `AppHost.cs`.
3. Verifique que chegou. Dispare o cenário `999.98` e procure `fornecedor_indisponivel` no backend novo — ele exercita trace, log de erro e retry de uma vez.
4. Não confie no silêncio. Falha de export não derruba o serviço: `/health` continua verde com zero telemetria chegando.

## Fontes

- [Dynatrace — OTLP API endpoints](https://docs.dynatrace.com/docs/ingest-from/opentelemetry/otlp-api)
- [Dynatrace — Export with OTLP](https://docs.dynatrace.com/docs/ingest-from/opentelemetry/getting-started/otlp-export)
- [Datadog — OTLP Ingestion by the Datadog Agent](https://docs.datadoghq.com/opentelemetry/setup/otlp_ingest_in_the_agent/)
- [Datadog — OTLP Intake Endpoint](https://docs.datadoghq.com/opentelemetry/setup/otlp_ingest/)
- [Datadog — Correlate OpenTelemetry Traces and Logs](https://docs.datadoghq.com/opentelemetry/correlate/logs_and_traces/)
