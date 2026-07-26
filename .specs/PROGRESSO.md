# PROGRESSO

Divisão do projeto em tarefas verificáveis. Referência: [PRD.md](PRD.md).

**Regra:** uma tarefa só é marcada `[x]` quando a linha `verificar:` foi executada e passou. Build quebrado, teste falhando ou verificação pulada = tarefa não concluída. O agent `spec-keeper` mantém este arquivo.

**Iniciado em:** 2026-07-26
**Status atual:** Fase 0 concluída em 2026-07-26, com **1 bloqueio de ambiente** (ver 0.6)

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
- [ ] **0.6** 🔴 **BLOQUEIO** — Confiar no certificado de desenvolvimento HTTPS
  `verificar:` `dotnet dev-certs https --check --trust` reporta o certificado como confiável, e o dashboard mostra traces após uma requisição
  **Situação:** existem 2 certificados dev na máquina, **nenhum confiável**. O endpoint OTLP do dashboard é HTTPS, então todo export falha no handshake TLS — em silêncio. Os serviços sobem e respondem normal; o dashboard fica vazio. Confirmado pelo self-diagnostics do OTel:
  `AuthenticationException: The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot` em `TraceService/Export` e `LogsService/Export`.
  **Ação:** o usuário precisa rodar `dotnet dev-certs https --trust` e aceitar o diálogo do Windows — é interativo, não dá para automatizar. Detalhado na skill `run-stack`.

### Notas da Fase 0

- Templates do Aspire não vinham instalados; instalado `Aspire.ProjectTemplates@13.4.6`. Necessário só para *criar* projetos, não para rodar.
- O perfil de launch `http` do AppHost **não** funciona sem `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` — o AppHost recusa `applicationUrl` não-HTTPS. Confiar no certificado é o caminho correto, não contornar.
- `.gitignore` criado em 2026-07-26, após o `git init`: 366 arquivos → 42 versionados (324 eram `bin/`+`obj/`). `.claude/` e `.specs/` entram no repo de propósito; só `.claude/settings.local.json` fica de fora.

## Fase 1 — Telemetria compartilhada

Agent: `otel-instrumentation` · Skills: `correlation-id`, `otel-conventions`

O contrato completo em um só lugar, aplicado igualmente pelos 3 serviços. Nada aqui é opcional — um passo faltando em um serviço quebra a correlação daquele hop em diante, sem erro.

- [ ] **1.1** Criar `Shared.Observability` com a constante do header, a chave de Baggage e a validação do id (até 64 chars, `[A-Za-z0-9._-]`)
  `verificar:` teste unitário — id válido passa, id com aspas/espaço/longo demais é rejeitado
- [ ] **1.2** `CorrelationIdMiddleware`: lê ou gera o id, seta no Baggage, abre o `BeginScope`, devolve o header via `OnStarting`
  `verificar:` teste de integração — response traz o header; id enviado é preservado; id inválido é substituído
- [ ] **1.3** `CorrelationIdSpanProcessor` (`BaseProcessor<Activity>`) copiando o id do Baggage para tag em `OnStart`
  `verificar:` teste com exporter in-memory — **todos** os spans têm `correlation.id`, não só o raiz
- [ ] **1.4** Ativar `IncludeScopes = true` no logging OTel do `ServiceDefaults`
  `verificar:` teste com exporter in-memory de logs — todo `LogRecord` tem o atributo `CorrelationId`
- [ ] **1.5** `CorrelationIdHandler` (`DelegatingHandler`) gravando `X-Correlation-Id` na requisição de saída
  `verificar:` teste com handler capturando a request — `X-Correlation-Id`, `traceparent` e `baggage` presentes
- [ ] **1.6** Extensão única `AddCorrelation()` que registra middleware, processor e handler; aplicada nos 3 serviços
  `verificar:` grep confirma a chamada nos 3 `Program.cs`, cada um com uma linha só
- [ ] **1.7** Definir `Telemetry.Source` e `Telemetry.Meter` por serviço, registrados via `.AddSource()`/`.AddMeter()`
  `verificar:` um span manual de teste aparece no exporter — se não aparecer, o source não foi registrado

## Fase 2 — Proxy

Agents: `slice-builder`, `otel-instrumentation` · Skills: `vertical-slice`, `structured-logging`

- [ ] **2.1** Slice `Features/Fornecedor/ProcessarPagamento/` — recebe do Core e chama o parceiro simulado
  `verificar:` `POST` direto no Proxy retorna aprovação para valor < 1000
- [ ] **2.2** Simulador do parceiro externo, com span próprio nomeado `ChamarFornecedor` e atributo `fornecedor.nome`
  `verificar:` o trace do Proxy mostra o span do parceiro aninhado no span do servidor
- [ ] **2.3** Injeção determinística de falhas conforme a tabela da seção 4 do PRD (999.99 timeout, 999.98 indisponível, 999.97 lento, >= 1000 saldo insuficiente)
  `verificar:` os 4 gatilhos reproduzem o desfecho esperado, repetidamente
- [ ] **2.4** Semântica de erro: `502`/`504` marcam o span como `Error` com `erro.motivo`; `422` de saldo deixa o span `Unset`
  `verificar:` no dashboard, o cenário de saldo insuficiente **não** aparece como erro; o de indisponibilidade aparece
- [ ] **2.5** Métrica `fornecedor.chamadas` com tag `resultado`
  `verificar:` a métrica aparece no dashboard com valores distintos por resultado

## Fase 3 — Core

Agents: `slice-builder`, `otel-instrumentation` · Skills: `vertical-slice`, `service-to-service`

- [ ] **3.1** Cliente tipado `IFornecedorProxyClient` com `CorrelationIdHandler` e `AddStandardResilienceHandler`, apontando para `https+http://pagamentos-proxy`
  `verificar:` sem porta hardcoded no código; a chamada resolve com o AppHost rodando
- [ ] **3.2** Slice `Features/Pagamentos/CriarPagamento/` — valida a chave PIX, chama o Proxy, traduz o resultado
  `verificar:` chave inválida retorna `422 chave_invalida` **sem** chamar o Proxy (confirmar no trace: nenhum span do Proxy)
- [ ] **3.3** Slice `Features/Pagamentos/ConsultarPagamento/`
  `verificar:` `GET /pagamentos/{id}` percorre a cadeia e retorna o status
- [ ] **3.4** Span de negócio `ValidarChavePix` com atributo `pix.chave.tipo` — nunca a chave em si
  `verificar:` o span existe e nenhum atributo contém o valor da chave
- [ ] **3.5** Mapear falhas do Proxy conforme a tabela da skill `service-to-service`, preservando o motivo
  `verificar:` o motivo `saldo_insuficiente` gerado no Proxy chega íntegro na resposta do Core
- [ ] **3.6** Métricas `pagamentos.solicitados` e `pagamentos.duracao`
  `verificar:` presentes no dashboard, sem nenhuma tag de alta cardinalidade

## Fase 4 — BFF

Agents: `slice-builder`, `otel-instrumentation` · Skills: `vertical-slice`, `service-to-service`

- [ ] **4.1** Cliente tipado `IPagamentosCoreClient` apontando para `https+http://pagamentos-core`, com os dois handlers
  `verificar:` o trace mostra o span client do BFF ligado ao span server do Core
- [ ] **4.2** Slice `Features/Pagamentos/CriarPagamento/` — validação de formato, chamada ao Core, adaptação da resposta
  `verificar:` `POST` no BFF percorre os 3 serviços e retorna aprovação
- [ ] **4.3** Slice `Features/Pagamentos/ConsultarPagamento/`
  `verificar:` `GET` percorre a cadeia completa
- [ ] **4.4** Confirmar o BFF como gerador natural do id, com log de entrada ancorando a investigação
  `verificar:` requisição sem header gera id novo; com header, preserva — ambos visíveis no log de entrada
- [ ] **4.5** Propagar o motivo de recusa até o cliente, sem degradar para erro genérico
  `verificar:` o cenário de saldo insuficiente chega ao cliente como `422 saldo_insuficiente`

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

_Adicionar aqui o que a implementação revelar e o plano não previu. Cada uma com sua linha `verificar:`._
