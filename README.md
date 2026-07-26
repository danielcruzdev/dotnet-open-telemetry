# dotnet-open-telemetry

Três microsserviços .NET 10 com **correlação automática** via OpenTelemetry. Uma requisição que entra no BFF pode ser seguida até o fornecedor externo por um único identificador — em traces e em logs — sem que ninguém precise passar esse id à mão.

O problema que isso resolve: quando um erro atravessa vários serviços, a investigação vira arqueologia. Abre-se o log de cada serviço e tenta-se casar por horário. Este projeto existe para que a resposta a *"por onde essa requisição passou e onde quebrou?"* leve segundos.

## Como rodar

Requer **.NET SDK 10** e um certificado de desenvolvimento **confiável**:

```bash
dotnet dev-certs https --check --trust    # aceite o diálogo do Windows
dotnet run --project src/Pagamentos.AppHost
```

O console imprime a URL de login do dashboard com um token de uso único:

```
Login to the dashboard at https://localhost:17044/login?t=<token>
```

O token muda a cada execução — sempre use o da saída atual.

> **Se o dashboard ficar vazio**, o certificado provavelmente não é confiável. O endpoint OTLP é HTTPS, então o export falha no handshake TLS **em silêncio**: os serviços sobem, respondem `/health`, e nada chega. Detalhes em [`.claude/skills/run-stack`](.claude/skills/run-stack/SKILL.md).

Testes:

```bash
dotnet test        # 123 testes
```

## Arquitetura

```
Cliente
  │  POST /pagamentos   ·   X-Correlation-Id (opcional)
  ▼
┌──────────┐  HTTP  ┌──────────┐  HTTP  ┌──────────┐  simulado
│   BFF    │ ─────► │   Core   │ ─────► │  Proxy   │ ─────► "Banco Parceiro"
│ entrada  │ ◄───── │ negócio  │ ◄───── │ tradução │ ◄─────
└──────────┘        └──────────┘        └──────────┘
     └──────────────────┴──────────────────┘
                  OTLP (traces, logs, métricas)
                             ▼
                    Aspire Dashboard
```

| Serviço | Faz | Não faz |
|---|---|---|
| **BFF** | Porta de entrada. Gera o `correlationId` quando ausente, valida formato, adapta a resposta. | Regra de negócio; falar com o Proxy |
| **Core** | Valida a chave PIX, decide, orquestra e traduz o resultado. | Falar HTTP com o fornecedor |
| **Proxy** | Traduz para o "fornecedor externo" e simula latência e falhas. | Regra de negócio |

Cada serviço organiza o código por feature (Vertical Slice), não por camada técnica. Convenções em [`.claude/skills/vertical-slice`](.claude/skills/vertical-slice/SKILL.md).

## O contrato de correlação

Dois identificadores viajam juntos e **não** se substituem:

| Id | Origem | Papel |
|---|---|---|
| `trace_id` (W3C `traceparent`) | Instrumentação OTel | Amarra os spans num único trace |
| `correlation.id` (`X-Correlation-Id`) | Gerado pelo BFF | Id de negócio, aparece nos logs, é o que se cola na busca |

Cada serviço aplica o contrato inteiro com **uma linha**:

```csharp
builder.AddCorrelation();
```

Não há nada por serviço para configurar: a correlação só vale se os três se comportarem de forma idêntica. A extensão registra o middleware via `IStartupFilter` (garantindo que seja o primeiro do pipeline) e o handler de saída via `ConfigureHttpClientDefaults` (cobrindo todo `HttpClient`, inclusive os criados depois).

Contrato completo, com os seis passos e a tabela de modos de falha: [`.claude/skills/correlation-id`](.claude/skills/correlation-id/SKILL.md).

## Investigando um problema

O cenário real: alguém reporta que um pagamento falhou e te passa um id.

### 1. Comece pelo id que o cliente tem

Toda resposta — inclusive as de erro — devolve o header:

```
X-Correlation-Id: 4a8f2c1e9b7d
```

Se o cliente não tiver o id, use o horário e o endpoint para achar o trace na lista.

### 2. Ache o trace

No dashboard, **Rastreamentos** → filtre pelo id. A lista já mostra a composição por serviço, e isso sozinho costuma responder onde parou:

| Composição | Leitura |
|---|---|
| `bff 2 · core 3 · proxy 2` | percorreu a cadeia inteira |
| `bff 2 · core 2` — sem proxy | parou no Core; o fornecedor nunca foi chamado |
| `bff 2 · core 6 · proxy 8` | houve retry: 4 tentativas |

### 3. Leia a árvore

Um pagamento saudável tem **7 spans em 3 serviços**:

```
POST /pagamentos                      pagamentos-bff
└─ HTTP POST 200                      pagamentos-bff
   └─ POST /pagamentos                pagamentos-core
      ├─ ValidarChavePix              pagamentos-core
      └─ HTTP POST 200                pagamentos-core
         └─ POST /fornecedor/pagamentos   pagamentos-proxy
            └─ ChamarFornecedor           pagamentos-proxy
```

O span marcado em vermelho é onde a falha nasceu. Abra-o e leia `erro.motivo` — ele diz **o quê**, não só que deu errado.

### 4. Distinga recusa de falha

Isto é deliberado e vale entender antes de investigar:

| Situação | Span do servidor | Log | Exemplo |
|---|---|---|---|
| **Recusa de negócio** | `Unset` (limpo) | `Warning` | `saldo_insuficiente`, `chave_invalida` |
| **Falha de infraestrutura** | `Error` | `Error` | `fornecedor_timeout`, `fornecedor_indisponivel` |

Uma recusa de saldo **não** aparece como erro. Se aparecesse, o painel de erros encheria de comportamento normal e as indisponibilidades reais sumiriam no ruído.

> Spans de **cliente** com `422` aparecem como erro. É o padrão do OpenTelemetry — para o cliente, um 4xx é falha da chamada; para o servidor, não. Não é defeito deste projeto.

### 5. Vá para os logs

No trace, cada span linka para os logs daquele momento. Ou vá em **Estruturado** e filtre pelo trace: os três serviços aparecem numa linha do tempo só.

Todo log de aplicação carrega `CorrelationId`, `TraceId` e `SpanId`, e os campos são estruturados — `motivo=fornecedor_indisponivel` é um campo, não texto colado na mensagem.

> **Um limite conhecido:** `Request starting` e `Request finished`, do `Microsoft.AspNetCore.Hosting.Diagnostics`, são emitidos fora do pipeline de middleware. Nenhum escopo os alcança, então esses dois não têm `CorrelationId`. É inerente, não regressão — há teste fixando isso.

### 6. Reproduza

Os desfechos são determinísticos por valor, de propósito — investigar exige poder reproduzir:

| Valor | Desfecho | Onde para |
|---|---|---|
| < 1000 | `200 aprovado` | — |
| ≥ 1000 | `422 saldo_insuficiente` | Proxy |
| `999.99` | `504 fornecedor_timeout` | Proxy |
| `999.98` | `502 fornecedor_indisponivel` | Proxy, após retries |
| `999.97` | `200`, ~3s | Proxy |
| chave inválida | `422 chave_invalida` | Core — o Proxy nunca é chamado |
| valor ≤ 0 | `422 valor_invalido` | BFF — o Core nunca é chamado |

```bash
curl -i -k -X POST https://localhost:<porta-bff>/pagamentos \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: minha-investigacao" \
  -d '{"chavePix":"usuario@exemplo.com","valor":999.98,"descricao":"teste"}'
```

A porta do BFF é atribuída pelo AppHost — leia na página **Recursos** do dashboard.

## Outros backends

Nada aqui é específico do Aspire Dashboard. Todo o contrato de correlação são atributos OTLP comuns, então `correlation.id` e `CorrelationId` chegam iguais em qualquer backend.

Trocar de destino é **só variável de ambiente, sem tocar em código**. Guia para **Dynatrace** e **Datadog**: [`docs/backends.md`](docs/backends.md).

## Estrutura

```
src/
  Pagamentos.AppHost/          orquestração (Aspire)
  Pagamentos.ServiceDefaults/  OTel, health checks, resiliência
  Shared.Observability/        o contrato de correlação
  Pagamentos.Bff/              entrada
  Pagamentos.Core/             negócio
  Pagamentos.Proxy/            fornecedor simulado
tests/                         123 testes, espelhando os slices
.claude/                       agents e skills (convenções do projeto)
.specs/                        PRD e PROGRESSO
```

## Documentação

- [`.specs/PRD.md`](.specs/PRD.md) — o que é e por quê, com os critérios de aceite
- [`.specs/PROGRESSO.md`](.specs/PROGRESSO.md) — as fases, com a evidência de cada verificação
- [`.claude/skills/`](.claude/skills/) — as convenções: correlação, telemetria, logs, testes, comunicação entre serviços
