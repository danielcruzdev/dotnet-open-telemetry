# PRD — Observabilidade Distribuída com OpenTelemetry

**Projeto:** dotnet-open-telemetry
**Data:** 2026-07-26
**Status:** especificado, não implementado

---

## 1. Problema

Quando uma requisição atravessa vários microsserviços e falha, a investigação hoje é arqueologia: abrir o log de cada serviço, tentar casar por horário e por payload, e torcer para que o erro tenha sido logado com contexto suficiente. Não existe uma resposta rápida para "por onde essa requisição passou e onde ela quebrou".

O custo disso não é o tempo de conserto — é o tempo até descobrir **qual serviço** consertar.

## 2. Objetivo

Um sistema de 3 microsserviços em que uma requisição pode ser seguida ponta a ponta por um único identificador, automaticamente, sem que nenhum desenvolvedor precise passar esse id de propósito em cada chamada ou em cada log.

Concretamente: dado um `correlationId`, ver em segundos o caminho completo da requisição, o que cada serviço decidiu, e em qual hop e por qual motivo ela falhou.

### Não-objetivos

Este é um projeto de referência de observabilidade. Ficam explicitamente de fora:

- Autenticação, autorização e multi-tenancy
- Banco de dados e persistência real (estado em memória)
- Integração com um fornecedor externo de verdade (o Proxy simula)
- Deploy, Kubernetes, CI/CD
- Mensageria assíncrona (só HTTP síncrono)
- Frontend

## 3. Arquitetura

```
Cliente
  │  POST /pagamentos
  │  X-Correlation-Id (opcional — gerado se ausente)
  ▼
┌──────────────┐   HTTP    ┌──────────────┐   HTTP    ┌──────────────┐   simulado
│     BFF      │ ────────► │     Core     │ ────────► │    Proxy     │ ────────►  "Banco Parceiro"
│  entrada     │           │  regra de    │           │  tradução    │
│  agregação   │ ◄──────── │  negócio     │ ◄──────── │  fornecedor  │ ◄────────
└──────────────┘           └──────────────┘           └──────────────┘
       │                          │                          │
       └──────────────────────────┴──────────────────────────┘
                        OTLP (traces, logs, métricas)
                                  ▼
                         Aspire Dashboard
```

Orquestração local: **.NET Aspire AppHost**, que sobe os três serviços, injeta o endpoint OTLP e provê service discovery entre eles.

### Responsabilidades

| Serviço | Responsabilidade | Não faz |
|---|---|---|
| **BFF** (`Pagamentos.Bff`) | Porta de entrada. Gera o `correlationId` quando ausente, valida o formato da requisição, chama o Core e adapta a resposta ao cliente. | Regra de negócio; falar com o Proxy |
| **Core** (`Pagamentos.Core`) | Regra de negócio: valida a chave PIX, decide se o pagamento pode seguir, orquestra a chamada ao Proxy e traduz o resultado. | Falar HTTP com o fornecedor diretamente |
| **Proxy** (`Pagamentos.Proxy`) | Tradução para o "fornecedor externo". Simula o parceiro, incluindo latência e falhas. Isola o Core do contrato do parceiro. | Regra de negócio |

O Proxy existe para que o Core nunca conheça o formato do parceiro. É também onde os cenários de falha são injetados — o que faz dele o destino mais frequente de uma investigação.

### Arquitetura interna: Vertical Slice

Cada serviço organiza o código por feature, não por camada técnica:

```
Features/Pagamentos/CriarPagamento/CriarPagamentoEndpoint.cs
Features/Pagamentos/ConsultarPagamento/ConsultarPagamentoEndpoint.cs
Infrastructure/          # clientes HTTP tipados
```

Convenções detalhadas na skill `vertical-slice`.

## 4. Domínio — Pagamentos PIX

Domínio escolhido por gerar cenários de erro ricos e realistas, que é o que dá utilidade ao tracing.

### `POST /pagamentos` (BFF)

```json
{
  "chavePix": "usuario@exemplo.com",
  "valor": 150.00,
  "descricao": "pagamento teste"
}
```

Resposta `200`:

```json
{
  "pagamentoId": "0f8a...",
  "status": "aprovado",
  "autorizacao": "AUT-99213"
}
```

Resposta `422` (rejeição de negócio):

```json
{
  "status": "recusado",
  "motivo": "saldo_insuficiente",
  "detalhe": "Saldo insuficiente para o valor solicitado."
}
```

Todas as respostas, incluindo erro, carregam o header `X-Correlation-Id`.

### `GET /pagamentos/{id}` (BFF)

Consulta de status. Percorre a mesma cadeia BFF → Core → Proxy.

### Cenários de falha simulados

O Proxy decide o desfecho a partir do valor ou da chave, de forma determinística — investigar exige poder reproduzir.

| Cenário | Gatilho | Desfecho | Onde para |
|---|---|---|---|
| Sucesso | valor < 1000 | `200 aprovado` | — |
| Saldo insuficiente | valor >= 1000 | `422 saldo_insuficiente` | Proxy (rejeição) |
| Chave PIX inválida | formato inválido | `422 chave_invalida` | Core — Proxy nunca é chamado |
| Timeout do parceiro | valor = 999.99 | `504 fornecedor_timeout` | Proxy |
| Parceiro indisponível | valor = 999.98 | `502 fornecedor_indisponivel` | Proxy, após retries |
| Latência alta | valor = 999.97 | `200`, ~3s | Proxy |

Rejeição de negócio (`422`) **não** é erro: log `Warning`, span com status `Unset`. Falha de infraestrutura (`502`/`504`) é erro: log `Error`, span com status `Error`. Misturar as duas coisas torna o painel de erros inútil.

## 5. Requisitos de observabilidade

Esta é a parte central do projeto.

### 5.1 Contrato de correlação

Dois identificadores viajam juntos e não se substituem:

| Id | Origem | Papel |
|---|---|---|
| `trace_id` (W3C `traceparent`) | Instrumentação OTel | Amarra os spans em um único trace distribuído |
| `correlation.id` (`X-Correlation-Id`) | Gerado pelo BFF | Id de negócio, aparece nos logs, é o que uma pessoa cola na busca do dashboard |

Os seis passos, implementados identicamente nos três serviços (detalhe na skill `correlation-id`):

1. **Entrada** — middleware lê `X-Correlation-Id`; ausente ou inválido, gera um novo. Validação: até 64 caracteres, apenas `[A-Za-z0-9._-]`.
2. **Baggage** — `Baggage.SetBaggage("correlation.id", id)`, propagado automaticamente pelo `BaggagePropagator`.
3. **Todo span** — um `BaseProcessor<Activity>` copia o id do Baggage para tag em `OnStart`, em **todos** os spans, não só no raiz.
4. **Todo log** — `BeginScope` com `CorrelationId` + `IncludeScopes = true`. `TraceId`/`SpanId` vêm automáticos.
5. **Response** — o header volta em toda resposta, via `OnStarting`.
6. **Outbound** — `DelegatingHandler` grava `X-Correlation-Id` explicitamente em cada chamada de saída.

O passo 6 é redundante com o Baggage de propósito: o header explícito em cada hop torna o id visível em qualquer captura HTTP, sem decodificar baggage.

### 5.2 Nomes fixos

| Onde | Nome |
|---|---|
| Header HTTP | `X-Correlation-Id` |
| Chave de Baggage | `correlation.id` |
| Atributo de span | `correlation.id` |
| Propriedade de log | `CorrelationId` |

### 5.3 Traces

Instrumentação automática de ASP.NET Core e HttpClient nos três serviços. Spans manuais apenas para operações de negócio que valem medição própria. Falhas registram `AddException` **e** `SetStatus`.

Um trace saudável de um pagamento tem 6 spans: server e client no BFF, server e client no Core, server no Proxy, e o span da chamada simulada ao parceiro.

### 5.4 Logs

Logs estruturados via OTel, exportados por OTLP. Dois `Information` por serviço por requisição — entrada e desfecho — mais `Warning`/`Error` quando aplicável.

Nunca logar: chave PIX, CPF/CNPJ, nome, e-mail, telefone, token, corpo completo de requisição. Registrar o **tipo** da chave, nunca o valor. Vale também para mensagens de exceção.

### 5.5 Métricas

Além do que a instrumentação automática já dá (taxa, latência e erro por endpoint):

- `pagamentos.solicitados` — `Counter<long>`, tag `status`
- `pagamentos.duracao` — `Histogram<double>`, segundos
- `fornecedor.chamadas` — `Counter<long>` (Proxy), tag `resultado`

Tags de métrica são obrigatoriamente de baixa cardinalidade. `correlation.id` ou `pagamento.id` em tag de métrica cria uma série temporal por requisição e derruba o backend.

## 6. Stack

| Item | Escolha |
|---|---|
| Runtime | .NET 10 (`net10.0`) — SDK 10.0.302 verificado na máquina |
| API | ASP.NET Core Minimal API |
| Arquitetura | Vertical Slice |
| Orquestração | .NET Aspire (AppHost + ServiceDefaults) |
| Telemetria | OpenTelemetry .NET — traces, logs e métricas via OTLP |
| Backend | Aspire Dashboard |
| Resiliência | `Microsoft.Extensions.Http.Resilience` |
| Testes | xUnit + `WebApplicationFactory` + exporters in-memory |

Versões de pacote centralizadas em `Directory.Packages.props`.

## 7. Critérios de aceite

O projeto está pronto quando todos passarem:

1. `dotnet run --project src/Pagamentos.AppHost` sobe os três serviços saudáveis.
2. `POST /pagamentos` no BFF sem `X-Correlation-Id` retorna o header preenchido na resposta.
3. `POST /pagamentos` com `X-Correlation-Id: teste-123` mantém esse valor no header de saída de cada hop e no atributo `correlation.id` de **todos** os spans dos três serviços.
4. No Aspire Dashboard, filtrar traces por `correlation.id` traz um único trace atravessando os três serviços.
5. Todo log da requisição, nos três serviços, tem `CorrelationId`, `TraceId` e `SpanId`.
6. Cada um dos 6 cenários da seção 4 é reproduzível, e o dashboard mostra **qual serviço** falhou e o `erro.motivo`.
7. Um motivo de recusa gerado no Proxy chega íntegro na resposta do BFF, sem virar "erro interno".
8. A suíte de testes cobre: id recebido é preservado, id ausente é gerado, id inválido é rejeitado, todos os spans têm o atributo, todos os logs têm a propriedade, e os headers de saída carregam `X-Correlation-Id`, `traceparent` e `baggage`.
9. Nenhum dado sensível em atributo de span ou log.
10. `README.md` descreve o passo a passo de investigar um problema pelo `correlationId`.

O critério 3 é o coração: se ele falhar, o projeto não cumpriu seu propósito, por mais que tudo o mais funcione.

## 8. Riscos conhecidos

| Risco | Mitigação |
|---|---|
| Correlação quebra em silêncio — nada lança exceção | Testes de telemetria com exporter in-memory (Fase 5), rodando em CI |
| Um passo implementado em 2 de 3 serviços | O contrato vive em `Shared.Observability`, aplicado por uma única extensão |
| Substituir o propagador default e perder o Baggage | Documentado nas skills `correlation-id` e `otel-conventions` |
| PII vazando em atributo de span | Revisão pelo agent `observability-reviewer` antes de cada merge |
| Retry duplicando pagamento | Retry restrito a timeout e falha de conexão; `POST` não é idempotente |
