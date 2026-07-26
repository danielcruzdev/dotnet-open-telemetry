# PROGRESSO

Divisão do projeto em tarefas verificáveis. Referência: [PRD.md](PRD.md).

**Regra:** uma tarefa só é marcada `[x]` quando a linha `verificar:` foi executada e passou. Build quebrado, teste falhando ou verificação pulada = tarefa não concluída. O agent `spec-keeper` mantém este arquivo.

**Iniciado em:** 2026-07-26
**Status atual:** Fases 0 a 4 concluídas e verificadas em 2026-07-26. 103 testes verdes. A cadeia BFF -> Core -> Proxy funciona ponta a ponta. Próxima: Fase 5 (testes) e Fase 6 (validação E2E no dashboard)

---

## Fase 0 — Fundação

Agent: `aspire-wiring` · Concluída em 2026-07-26 · Aspire 13.4.6, SDK 10.0.302

- [x] **0.1** Criar a solution `DotnetOpenTelemetry.slnx` e a estrutura `src/` e `tests/`
  `verificar:` `dotnet build` roda sem erro na solution vazia
  ✔ `dotnet build` → `Compilação com êxito. 0 Aviso(s) 0 Erro(s)`
  ℹ o SDK 10 gera o formato novo `.slnx` (XML), não `.sln` — a tarefa dizia `.sln`
- [x] **0.2** Criar `Directory.Packages.props` (central package management) e `Directory.Build.props` (`net10.0`, nullable, warnings as errors)
  `verificar:` um projeto novo herda `net10.0` sem declarar `TargetFramework`
  ✔ os 5 csproj não declaram `TargetFramework` e compilam em `bin/Debug/net10.0`. `TreatWarningsAsErrors=true` ativo, build com 0 avisos
- [x] **0.3** Criar os 3 projetos Minimal API: `Pagamentos.Bff`, `Pagamentos.Core`, `Pagamentos.Proxy`
  `verificar:` os 3 sobem individualmente e respondem em `/health`
  ✔ rodados isoladamente nas portas 5301/5302/5303 → `Healthy [HTTP 200]` nos 3
- [x] **0.4** Criar `Pagamentos.ServiceDefaults` com OTel (traces, logs, métricas), health checks e resiliência padrão
  `verificar:` os 3 serviços chamam `AddServiceDefaults()` e compilam
  ✔ gerado do template `aspire-servicedefaults`; já traz `IncludeScopes = true` (passo 4 do contrato de correlação), `AddStandardResilienceHandler` e `AddServiceDiscovery` como default de todo `HttpClient`
- [x] **0.5** Criar `Pagamentos.AppHost` referenciando os 3 serviços, com os nomes de recurso `pagamentos-bff`, `pagamentos-core`, `pagamentos-proxy`
  `verificar:` `dotnet run --project src/Pagamentos.AppHost` sobe os 3 e o dashboard mostra todos saudáveis
  ✔ AppHost subiu os 3 (perfil `https`); `/health` → `Healthy [HTTP 200]` em cada um; dashboard respondeu HTTP 302 em `https://localhost:17044`
  ⚠ **não verificado:** que o dashboard *exibe* os recursos — exige login interativo no browser. Ver 0.6
- [x] **0.6** Confiar no certificado de desenvolvimento HTTPS — resolvido em 2026-07-26
  `verificar:` `dotnet dev-certs https --check --trust` reporta o certificado como confiável, e o dashboard mostra traces após uma requisição
  **Era:** 2 certificados dev na máquina, nenhum confiável. O endpoint OTLP do dashboard é HTTPS, então todo export falhava no handshake TLS — em silêncio: serviços saudáveis, dashboard vazio. Diagnosticado pelo self-diagnostics do OTel:
  `AuthenticationException: ... certificate chain: UntrustedRoot` em `TraceService/Export` e `LogsService/Export`.
  ✔ resolvido com `dotnet dev-certs https --trust` (interativo, rodado pelo usuário). Após isso: cert `F84D67C5...` confiável; cada serviço mantém **3 conexões Established** para o OTLP em 21117 (antes eram só TIME_WAIT em looping); self-diagnostics dos 3 serviços **sem nenhum erro de export**
  ⚠ **não verificado:** a renderização dos traces na UI do dashboard — exige login interativo no browser

### Notas da Fase 0

- Templates do Aspire não vinham instalados; instalado `Aspire.ProjectTemplates@13.4.6`. Necessário só para *criar* projetos, não para rodar.
- O perfil de launch `http` do AppHost **não** funciona sem `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` — o AppHost recusa `applicationUrl` não-HTTPS. Confiar no certificado é o caminho correto, não contornar.
- `.gitignore` criado em 2026-07-26, após o `git init`: 366 arquivos → 42 versionados (324 eram `bin/`+`obj/`). `.claude/` e `.specs/` entram no repo de propósito; só `.claude/settings.local.json` fica de fora.

## Fase 1 — Telemetria compartilhada

Agent: `otel-instrumentation` · Skills: `correlation-id`, `otel-conventions`

O contrato completo em um só lugar, aplicado igualmente pelos 3 serviços. Nada aqui é opcional — um passo faltando em um serviço quebra a correlação daquele hop em diante, sem erro.

Concluída em 2026-07-26 · **29 testes, todos verdes** em `tests/Shared.Observability.Tests`

- [x] **1.1** Criar `Shared.Observability` com a constante do header, a chave de Baggage e a validação do id (até 64 chars, `[A-Za-z0-9._-]`)
  `verificar:` teste unitário — id válido passa, id com aspas/espaço/longo demais é rejeitado
  ✔ 16 casos em `CorrelationIdTests`: aceita `A1._-` e 64 chars; rejeita vazio, 65 chars, espaço, `"; DROP TABLE`, quebra de linha, acento e barra
- [x] **1.2** `CorrelationIdMiddleware`: lê ou gera o id, seta no Baggage, abre o `BeginScope`, devolve o header via `OnStarting`
  `verificar:` teste de integração — response traz o header; id enviado é preservado; id inválido é substituído
  ✔ 4 testes; confirmado também entre processos reais sob o AppHost
- [x] **1.3** `CorrelationIdSpanProcessor` (`BaseProcessor<Activity>`) copiando o id do Baggage para tag em `OnStart`
  `verificar:` teste com exporter in-memory — **todos** os spans têm `correlation.id`, não só o raiz
  ✔ span raiz, de negócio e de saída, cada um com teste próprio
  ⚠ **descoberta:** o processor **não** alcança o span do servidor — ele nasce antes de qualquer middleware, quando o Baggage ainda está vazio. O middleware marca esse span na mão. Registrado na skill `correlation-id`
- [x] **1.4** Ativar `IncludeScopes = true` no logging OTel do `ServiceDefaults`
  `verificar:` teste com exporter in-memory de logs — todo `LogRecord` tem o atributo `CorrelationId`
  ✔ já vinha ligado do template; o id é lido via `ForEachScope`, não via `LogRecord.Attributes`
  ⚠ **limite inerente:** `Request starting`/`Request finished` do `Microsoft.AspNetCore.Hosting.Diagnostics` são emitidos fora do pipeline de middleware — nenhum escopo os alcança. Fixado em teste próprio para nunca ser lido como regressão
- [x] **1.5** `CorrelationIdHandler` (`DelegatingHandler`) gravando `X-Correlation-Id` na requisição de saída
  `verificar:` teste com handler capturando a request — `X-Correlation-Id`, `traceparent` e `baggage` presentes
  ✔ os 3 headers confirmados
  ⚠ **descoberta:** capturar com `ConfigurePrimaryHttpMessageHandler` **não funciona** — trocar o primary handler tira o `DiagnosticsHandler` do caminho e some com o span de cliente e com a injeção de `traceparent`/`baggage`. O teste usa um servidor Kestrel real de eco em porta dinâmica
- [x] **1.6** Extensão única `AddCorrelation()` que registra middleware, processor e handler; aplicada nos 3 serviços
  `verificar:` grep confirma a chamada nos 3 `Program.cs`, cada um com uma linha só
  ✔ `grep -c AddCorrelation` → 1 em cada um dos 3. Usa `IStartupFilter` (garante o middleware em primeiro no pipeline) e `ConfigureHttpClientDefaults` (cobre todo `HttpClient`, inclusive os criados depois)
- [x] **1.7** Definir `Telemetry.Source` e `Telemetry.Meter` por serviço, registrados via `.AddSource()`/`.AddMeter()`
  `verificar:` um span manual de teste aparece no exporter — se não aparecer, o source não foi registrado
  ✔ `ServiceDefaultsTests` prova que source e meter nomeados pelo `ApplicationName` são captados. `.AddMeter()` faltava no `ServiceDefaults` e foi adicionado. O nome vem de `Assembly.GetName().Name` em vez de constante literal, para não poder divergir

### Teste de mutação (exigido pelas tarefas 5.4/5.5/5.7)

Cada peça foi removida e o teste correspondente confirmado falhando, depois restaurada:

| Removido | Falhou |
|---|---|
| `AddProcessor(new CorrelationIdSpanProcessor())` | `Todo_span...`, `Span_de_negocio_e_span_de_saida...` |
| `Activity.Current?.SetTag(...)` do middleware | `Span_raiz_do_servidor...` |
| conteúdo do `BeginScope` | `Todo_log_de_aplicacao...` |
| `CorrelationIdHandler` dos defaults de `HttpClient` | `Chamada_de_saida_leva_o_header...` |

### Verificação E2E sob o AppHost

Com os 3 serviços rodando como processos separados:

| Cenário | BFF | Core | Proxy |
|---|---|---|---|
| envia `e2e-fase1` | `e2e-fase1` | `e2e-fase1` | `e2e-fase1` |
| sem header | `25015811…` | `7d54afb2…` | `a85b17b6…` |
| envia `invalido; DROP` | `6af0ce24…` | `27950296…` | `075f766e…` |

### Correções feitas nas skills

A implementação contradisse o que estava escrito. Corrigido em `correlation-id` (span raiz e logs do hosting), `service-to-service` (handler agora é global — **não** registrar por cliente), `telemetry-testing` (harness com servidor real, `ForEachScope`, xUnit em vez de FluentAssertions, spans de hosts auxiliares vazando) e `otel-conventions` (nome derivado do assembly; `ConfigureOpenTelemetryTracerProvider` não existe nesta versão).

## Fase 2 — Proxy

Agents: `slice-builder`, `otel-instrumentation` · Skills: `vertical-slice`, `structured-logging`

Concluída em 2026-07-26 · **17 testes** em `tests/Pagamentos.Proxy.Tests`

- [x] **2.1** Slice `Features/Fornecedor/ProcessarPagamento/` — recebe do Core e chama o parceiro simulado
  `verificar:` `POST` direto no Proxy retorna aprovação para valor < 1000
  ✔ `POST /fornecedor/pagamentos` → `200 aprovado` com `pagamentoId` e `autorizacao`
- [x] **2.2** Simulador do parceiro externo, com span próprio nomeado `ChamarFornecedor` e atributo `fornecedor.nome`
  `verificar:` o trace do Proxy mostra o span do parceiro aninhado no span do servidor
  ✔ `ParentSpanId` do span do fornecedor == `SpanId` do span do servidor, mesmo `TraceId`; `fornecedor.nome = banco-parceiro`
- [x] **2.3** Injeção determinística de falhas conforme a tabela da seção 4 do PRD (999.99 timeout, 999.98 indisponível, 999.97 lento, >= 1000 saldo insuficiente)
  `verificar:` os 4 gatilhos reproduzem o desfecho esperado, repetidamente
  ✔ os 5 cenários confirmados sob o AppHost, e o gatilho 999.98 repetido 3× com o mesmo desfecho
- [x] **2.4** Semântica de erro: `502`/`504` marcam o span como `Error` com `erro.motivo`; `422` de saldo deixa o span `Unset`
  `verificar:` no dashboard, o cenário de saldo insuficiente **não** aparece como erro; o de indisponibilidade aparece
  ✔ recusa deixa o span `Unset`; falha de infra marca `Error` + `StatusDescription`
  ⚠ **descoberta:** asserir só `Status == Error` não prova nada — a instrumentação do ASP.NET Core já marca todo 5xx como erro sozinha. O teste passava mesmo sem o nosso `SetStatus`. Passou a asserir `StatusDescription`, que só o nosso código define
- [x] **2.5** Métrica `fornecedor.chamadas` com tag `resultado`
  `verificar:` a métrica aparece no dashboard com valores distintos por resultado
  ✔ separada por `Aprovado` e `SaldoInsuficiente`; a mutação para tag de alta cardinalidade é detectada pelo teste

### Contrato do Proxy

`POST /fornecedor/pagamentos` · `{ chavePix, valor, descricao }`

| HTTP | Corpo |
|---|---|
| 200 | `{ pagamentoId, status: "aprovado", autorizacao }` |
| 422 / 502 / 504 | `{ status, motivo, detalhe }` |

Toda resposta não-200 usa a **mesma forma**, para o Core ter só um formato a entender e o motivo atravessar os hops sem virar erro genérico.

### Cenários verificados sob o AppHost

| Cenário | valor | HTTP | motivo | ms |
|---|---|---|---|---|
| sucesso | 150 | 200 | — | 52 |
| saldo insuficiente | 1000 | 422 | `saldo_insuficiente` | 8 |
| timeout do parceiro | 999,99 | 504 | `fornecedor_timeout` | 7 |
| parceiro indisponível | 999,98 | 502 | `fornecedor_indisponivel` | 8 |
| latência alta | 999,97 | 200 | — | 3014 |

`X-Correlation-Id: e2e-fase2` preservado em todas.

### Teste de mutação

| Removido / alterado | Falhou |
|---|---|
| recusa de saldo passa a marcar `Error` | `Recusa_de_saldo_nao_marca_o_span...` |
| `SetStatus` da falha de infra | `Falha_de_infraestrutura...` (só após reforçar a asserção) |
| tag `fornecedor.nome` | `Span_do_fornecedor_identifica_o_parceiro` |
| métrica com tag de alta cardinalidade | `Metrica_de_chamadas_separa_por_resultado` |

### Decisões

- A latência do cenário 999,97 vem de `Fornecedor:LatenciaAlta` (default 3s). Os testes reduzem para 50 ms — sem isso a suíte pagaria 3s por execução. Confirmado que o default vale em produção: 3014 ms sob o AppHost.
- `[assembly: CollectionBehavior(DisableTestParallelization = true)]` no projeto de teste. A instrumentação do ASP.NET Core é do processo inteiro, então classes de teste em paralelo derrubam spans umas no exporter das outras.
- `public partial class Program;` adicionado ao Proxy agora (a tarefa 5.1 previa isso para depois) porque o `WebApplicationFactory` já precisa dele.

## Fase 3 — Core

Agents: `slice-builder`, `otel-instrumentation` · Skills: `vertical-slice`, `service-to-service`

Concluída em 2026-07-26 · **34 testes** em `tests/Pagamentos.Core.Tests`

- [x] **3.1** Cliente tipado `IFornecedorProxyClient` com `CorrelationIdHandler` e `AddStandardResilienceHandler`, apontando para `https+http://pagamentos-proxy`
  `verificar:` sem porta hardcoded no código; a chamada resolve com o AppHost rodando
  ✔ resolve sob o AppHost; nenhuma porta nem `localhost` no código
  ⚠ **desvio da tarefa:** os dois handlers **não** são registrados por cliente. Desde a Fase 1 eles vêm de `ConfigureHttpClientDefaults` (`AddCorrelation` e `AddServiceDefaults`), e registrar de novo duplicaria o handler no pipeline. O texto da tarefa é anterior a essa correção
- [x] **3.2** Slice `Features/Pagamentos/CriarPagamento/` — valida a chave PIX, chama o Proxy, traduz o resultado
  `verificar:` chave inválida retorna `422 chave_invalida` **sem** chamar o Proxy (confirmar no trace: nenhum span do Proxy)
  ✔ `422 chave_invalida`, zero chamadas ao Proxy e **nenhum span de cliente** no trace
- [x] **3.3** Slice `Features/Pagamentos/ConsultarPagamento/`
  `verificar:` `GET /pagamentos/{id}` percorre a cadeia e retorna o status
  ✔ o id emitido pelo Proxy é consultável através do Core; id desconhecido → 404
- [x] **3.4** Span de negócio `ValidarChavePix` com atributo `pix.chave.tipo` — nunca a chave em si
  `verificar:` o span existe e nenhum atributo contém o valor da chave
  ✔ span presente com `pix.chave.tipo = Email`; teste varre todas as tags de todos os spans e falha se a chave aparecer
- [x] **3.5** Mapear falhas do Proxy conforme a tabela da skill `service-to-service`, preservando o motivo
  `verificar:` o motivo `saldo_insuficiente` gerado no Proxy chega íntegro na resposta do Core
  ✔ `saldo_insuficiente`, `fornecedor_timeout` e `fornecedor_indisponivel` chegam sem tradução
- [x] **3.6** Métricas `pagamentos.solicitados` e `pagamentos.duracao`
  `verificar:` presentes no dashboard, sem nenhuma tag de alta cardinalidade
  ✔ ambas publicadas, `status` separando `aprovado`/`recusado`; teste dedicado rejeita `correlation.id`, `pagamento.id` e derivados de chave

### Cadeia Core → Proxy verificada sob o AppHost

| Cenário | HTTP | status | motivo | ms |
|---|---|---|---|---|
| sucesso | 200 | aprovado | — | 434 |
| chave inválida | 422 | recusado | `chave_invalida` | 13 |
| saldo (gerado no Proxy) | 422 | recusado | `saldo_insuficiente` | 21 |
| timeout (gerado no Proxy) | 504 | erro | `fornecedor_timeout` | 6199 |
| indisponível + retries | 502 | erro | `fornecedor_indisponivel` | 8974 |

Os 6199 ms e 8974 ms são o backoff de retry **em produção** — confirmam que o retry exigido pelo PRD acontece de fato. `X-Correlation-Id` preservado em todas. `GET /pagamentos/{id}` → 200; id desconhecido → 404.

### Teste de mutação

| Alterado | Falhou |
|---|---|
| validação da chave sempre passa | `Chave_invalida_e_recusada_sem_chamar_o_proxy`, `Chave_invalida_nao_gera_span_de_cliente` |
| motivo do Proxy vira `erro_interno` | `Falha_do_proxy_preserva_o_motivo` |
| recusa do Proxy tratada como falha de infra | 3 testes |
| chave PIX na tag do span | `Nenhum_span_carrega_a_chave_pix` + 1 |
| métrica com tag por pagamento | `Metricas_nao_usam_tag_de_alta_cardinalidade` + 1 |

⚠ **armadilha do harness:** a primeira tentativa de mutação não compilou (`CS0165`), e falha de build não gera linha `FAIL` — o resultado parecia "nenhum teste pegou". Registrado na skill `telemetry-testing`: sempre conferir que o código mutado ainda compila antes de interpretar.

### Decisões

- **Motivo próprio para o Proxy inalcançável.** Falha de transporte até o Proxy vira `proxy_indisponivel`/`proxy_timeout`, distinto de `fornecedor_*` que o Proxy respondeu. Localizar o hop é o objetivo do projeto, e um motivo único para os dois casos apagaria essa distinção.
- **Retry mantido no `POST`, apesar de não ser idempotente.** O PRD exige "502 após retries" e a visibilidade das tentativas como spans irmãos. Em sistema real isso exigiria chave de idempotência — anotado como limite conhecido, não como acidente.
- **Backoff reduzido para 1 ms nos testes** via `ConfigureAll<HttpStandardResilienceOptions>`. Sem isso a suíte levava 24 s; agora 1 s. Dois testes fixam que o retry continua acontecendo e que 422 **não** é retentado.
- **O Core não guarda estado.** Quem emite o `pagamentoId` é o fornecedor, então a consulta atravessa a cadeia em vez de duplicar o razão.

## Fase 4 — BFF

Agents: `slice-builder`, `otel-instrumentation` · Skills: `vertical-slice`, `service-to-service`

Concluída em 2026-07-26 · **20 testes** em `tests/Pagamentos.Bff.Tests`

- [x] **4.1** Cliente tipado `IPagamentosCoreClient` apontando para `https+http://pagamentos-core`, com os dois handlers
  `verificar:` o trace mostra o span client do BFF ligado ao span server do Core
  ✔ o teste lê o `traceparent` que **chegou** ao Core e confere: `trace-id` igual ao do span de cliente e `parent-span-id` igual ao `SpanId` dele — ligação provada, não inferida
  ⚠ mesmo desvio da 3.1: os handlers vêm de `ConfigureHttpClientDefaults`, não por cliente
- [x] **4.2** Slice `Features/Pagamentos/CriarPagamento/` — validação de formato, chamada ao Core, adaptação da resposta
  `verificar:` `POST` no BFF percorre os 3 serviços e retorna aprovação
  ✔ `200 aprovado` atravessando BFF → Core → Proxy. Só formato aqui (`valor > 0`, chave presente); a regra da chave PIX é do Core
- [x] **4.3** Slice `Features/Pagamentos/ConsultarPagamento/`
  `verificar:` `GET` percorre a cadeia completa
  ✔ `GET /pagamentos/{id}` → 200 com o id emitido pelo Proxy; desconhecido → 404
- [x] **4.4** Confirmar o BFF como gerador natural do id, com log de entrada ancorando a investigação
  `verificar:` requisição sem header gera id novo; com header, preserva — ambos visíveis no log de entrada
  ✔ sem header gera (`55f2ae84…`); com header preserva; o id do log de entrada é **o mesmo** devolvido no header ao cliente. Dois testes fixam a existência desse log, inclusive quando a requisição é recusada no próprio BFF
- [x] **4.5** Propagar o motivo de recusa até o cliente, sem degradar para erro genérico
  `verificar:` o cenário de saldo insuficiente chega ao cliente como `422 saldo_insuficiente`
  ✔ `saldo_insuficiente`, `chave_invalida`, `fornecedor_timeout` e `fornecedor_indisponivel` chegam intactos ao cliente

### 🐛 Bug encontrado só no AppHost

`timeout` e `indisponivel` voltavam **`500` com stack trace, após 30 s**, em vez do motivo. A suíte não pegava porque o Core falso responde instantâneo. Diagnóstico pelo corpo da resposta real:

```
Polly.Timeout.TimeoutRejectedException: The operation didn't complete
within the allowed timeout of '00:00:30'
```

Duas causas, ambas corrigidas com teste de regressão antes da correção:

1. **Amplificação de retry.** O BFF retentava o Core (3×) enquanto o Core retentava o Proxy (3×), com backoff composto — estourava o `TotalRequestTimeout`. Retry agora acontece só na camada mais interna.
2. **`TimeoutRejectedException` não capturada.** É tipo da Polly e não deriva de `HttpRequestException` nem de `TaskCanceledException`, então escapava como 500 e apagava o motivo no último metro. Capturada junto com `BrokenCircuitException`.

Duas armadilhas silenciosas encontradas no caminho, ambas registradas na skill `service-to-service`:

- `MaxRetryAttempts = 0` **lança** na validação — o mínimo é 1. Desligar o retry é pelo predicado `ShouldHandle`.
- As opções chamam-se **`"-standard"`**, não `"{cliente}-standard"`: o handler vem de `ConfigureHttpClientDefaults`, cujo builder tem `Name` vazio. Passar o nome do cliente compila, roda e é ignorado sem aviso.

### Cadeia completa BFF → Core → Proxy, sob o AppHost

| Cenário | HTTP | status | motivo | ms |
|---|---|---|---|---|
| sucesso (3 serviços) | 200 | aprovado | — | 1311 |
| valor inválido (para no BFF) | 422 | recusado | `valor_invalido` | 30 |
| chave inválida (para no Core) | 422 | recusado | `chave_invalida` | 30 |
| saldo (gerado no Proxy) | 422 | recusado | `saldo_insuficiente` | 37 |
| timeout (gerado no Proxy) | 504 | erro | `fornecedor_timeout` | 9405 |
| indisponível (gerado no Proxy) | 502 | erro | `fornecedor_indisponivel` | 6720 |
| latência alta (Proxy) | 200 | aprovado | — | 3029 |

Sem header, o BFF gerou `55f2ae84…`. Consulta atravessando os 3 → 200.

### Teste de mutação

| Alterado | Falhou |
|---|---|
| motivo do Core degrada para genérico | `Motivo_atravessa_os_tres_servicos_sem_degradar` |
| recusa do Core perde o motivo | 2 testes |
| `request.Valor <= 0` → `< 0` | `Valor_nao_positivo_e_recusado_sem_chamar_o_core(0)` |
| remove o log de entrada | `Existe_log_de_entrada...`, `Recusa_de_formato_registra_log...` |

⚠ a mutação do log de entrada **escapou na primeira rodada** — a asserção original dizia "todo log da requisição tem o id", o que continua verdade sem o log de entrada. Os dois testes acima foram acrescentados para fixar a existência dele, que é o que a tarefa 4.4 realmente pede.

## Fase 5 — Testes

Agent: `slice-builder` · Skill: `telemetry-testing`

Esta fase é o que impede a correlação de regredir em silêncio.

- [ ] **5.1** Projetos de teste para os 3 serviços, espelhando as pastas de slice; `public partial class Program;` em cada serviço
  `verificar:` `dotnet test` compila e roda
- [ ] **5.2** Testes unitários das regras: validação de chave PIX, validação do formato do correlationId, decisão de aprovação
  `verificar:` `dotnet test` verde
- [ ] **5.3** Testes de integração por slice, com clientes downstream substituídos por stub
  `verificar:` nenhum teste depende de outro serviço no ar
- [ ] **5.4** Teste de telemetria: **todos** os spans carregam `correlation.id` (`OnlyContain`, não `First`)
  `verificar:` o teste falha se o `CorrelationIdSpanProcessor` for removido — confirmar removendo temporariamente
- [ ] **5.5** Teste de telemetria: todo `LogRecord` carrega `CorrelationId`
  `verificar:` o teste falha se `IncludeScopes` for desligado — confirmar
- [ ] **5.6** Teste de propagação outbound: `X-Correlation-Id`, `traceparent` e `baggage` na request de saída
  `verificar:` teste verde com a request capturada
- [ ] **5.7** Teste de continuidade de trace: `TraceId` do span server é igual ao do activity iniciado no teste
  `verificar:` o teste falha se o cliente for trocado por `new HttpClient()` — confirmar
- [ ] **5.8** Testes dos cenários de falha, incluindo a distinção entre `Unset` (recusa) e `Error` (falha)
  `verificar:` `dotnet test` verde nos 6 cenários

## Fase 6 — Validação E2E

Agent: `observability-reviewer` · Skill: `run-stack`

Aqui os critérios de aceite do PRD são verificados de verdade, no dashboard.

- [ ] **6.1** Trace único atravessando os 3 serviços, com 6 spans, para um pagamento bem-sucedido
  `verificar:` filtrar por `correlation.id` no dashboard e descrever a árvore observada
- [ ] **6.2** Os 6 cenários da seção 4 do PRD reproduzidos, cada um mostrando no dashboard qual serviço falhou e o `erro.motivo`
  `verificar:` percorrer um a um e registrar o resultado
- [ ] **6.3** Logs dos 3 serviços correlacionados em uma linha do tempo por `CorrelationId`
  `verificar:` filtrar nos structured logs do dashboard
- [ ] **6.4** Auditoria do `observability-reviewer`: PII, cardinalidade de métrica, semântica de erro, `new HttpClient` residual
  `verificar:` relatório sem achado de severidade alta
- [ ] **6.5** Conferir os 10 critérios de aceite do PRD, um a um
  `verificar:` todos passam; qualquer falha volta como tarefa nova nesta fase

## Fase 7 — Documentação

Agent: `spec-keeper`

- [ ] **7.1** `README.md`: o que é, como rodar, arquitetura, e o passo a passo de investigar um problema pelo `correlationId`
  `verificar:` alguém que não conhece o projeto sobe a stack e acha um trace seguindo só o README
- [ ] **7.2** Registrar no PRD as decisões que mudaram durante a implementação
  `verificar:` PRD e código não divergem
- [ ] **7.3** Fechar este arquivo com o status final e as tarefas descobertas ao longo do caminho
  `verificar:` nenhuma tarefa marcada sem evidência de verificação

---

## Tarefas descobertas

- [x] **D1** (descoberta na Fase 3) Endpoint de consulta no Proxy — `GET /fornecedor/pagamentos/{id}`
  `verificar:` pagamento aprovado é consultável; id desconhecido devolve 404
  ✔ a Fase 2 só previu o `POST`, mas a tarefa 3.3 exige que a consulta percorra a cadeia. O fornecedor é quem emite o `pagamentoId`, então é ele quem responde pela consulta — guardar o mesmo estado no Core criaria duas versões da verdade. 3 testes em `Pagamentos.Proxy.Tests`
