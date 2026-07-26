import http from 'k6/http';
import { check, sleep } from 'k6';

// Gerador de trafego para encher o dashboard com traces e logs de verdade.
// Nao e um teste de performance: nao ha threshold que reprove a execucao.
// O criterio de sucesso e o que aparece no painel, nao o p95.
//
//   docker run --rm -i -v "${PWD}/loadtest:/scripts" grafana/k6 \
//     run /scripts/pagamentos.js -e BFF_URL=https://host.docker.internal:7048

const CENARIO = __ENV.CENARIO || 'mix';

// Porta fixa no launchSettings.json do BFF. Sobrescreva com -e BFF_URL.
const BFF_URL = __ENV.BFF_URL || 'https://localhost:7048';

const CENARIOS = {
    fumaca: { executor: 'constant-vus', vus: 1, duration: '30s', exec: 'fumaca' },
    mix: { executor: 'constant-vus', vus: 10, duration: '2m', exec: 'mix' },
    falhas: { executor: 'constant-vus', vus: 5, duration: '1m', exec: 'falhas' },
};

if (!CENARIOS[CENARIO]) {
    throw new Error(
        `Cenario desconhecido: "${CENARIO}". Use um de: ${Object.keys(CENARIOS).join(', ')}.`);
}

export const options = {
    // O dev cert do ASP.NET Core nao e confiavel de dentro do container.
    // Sem isto, toda requisicao morre no handshake TLS.
    insecureSkipTLSVerify: true,
    scenarios: { [CENARIO]: CENARIOS[CENARIO] },
};

// Os desfechos sao deterministicos por valor (ver FornecedorSimulado).
// Cada caso declara o status que o BFF deve devolver, e o check cobra isso:
// e o que prova que o gerador exercita o que diz exercitar.
const CASOS = {
    aprovado: { valor: () => entre(1, 900), esperado: 200 },
    saldo_insuficiente: { valor: () => entre(1000, 9000), esperado: 422 },
    fornecedor_timeout: { valor: () => 999.99, esperado: 504 },
    fornecedor_indisponivel: { valor: () => 999.98, esperado: 502 },
    chave_invalida: { valor: () => entre(1, 900), chave: () => 'chave-invalida', esperado: 422 },
    valor_invalido: { valor: () => 0, esperado: 422 },
};

// So desfechos de negocio (200 e 422). Nenhuma falha de infra aqui, e isso
// e deliberado: o AddStandardResilienceHandler abre o circuito com 10% de
// 5xx numa janela de 30s, e cada retry conta como tentativa. Com 5% de
// timeout + 5% de indisponivel a razao real passa de 30%, o circuito abre e
// TODA requisicao vira 502 core_indisponivel — inclusive as aprovadas.
// Falha de infra pertence ao cenario "falhas", cuja vazao fica abaixo das
// 100 amostras minimas que o breaker exige para agir.
const PESOS_MIX = {
    aprovado: 70,
    saldo_insuficiente: 15,
    chave_invalida: 10,
    valor_invalido: 5,
};

const PESOS_FALHAS = {
    fornecedor_timeout: 30,
    fornecedor_indisponivel: 30,
    saldo_insuficiente: 20,
    chave_invalida: 20,
};

// Os cinco tipos que o Core classifica. Variar o tipo da chave da variedade
// ao atributo chave.tipo dos spans — a chave em si nunca vai para a telemetria.
const CHAVES = [
    () => `usuario${entreInteiro(1, 999)}@exemplo.com`,
    () => '12345678901',
    () => '12345678000190',
    () => '+5511987654321',
    () => uuid(),
];

export function fumaca() {
    executar('aprovado');
    sleep(0.5);
}

export function mix() {
    const caso = sortear(PESOS_MIX);
    const resposta = executar(caso);

    // A rota de consulta so recebe trafego aqui. Sem isto ela nunca aparece
    // no dashboard, e o painel fica com uma unica forma de trace.
    if (caso === 'aprovado' && resposta.status === 200) {
        consultar(resposta);
    }

    sleep(entre(0.3, 1));
}

export function falhas() {
    executar(sortear(PESOS_FALHAS));
    sleep(entre(0.3, 1));
}

export function teardown() {
    console.log(
        `\n  Filtre por "k6-${CENARIO}" em Rastreamentos no dashboard para ver estes traces.\n`);
}

function executar(nomeDoCaso) {
    const caso = CASOS[nomeDoCaso];
    const chave = caso.chave ? caso.chave() : escolher(CHAVES)();

    // Um id por iteracao: e o que torna uma requisicao desta carga
    // encontravel no meio das outras.
    const correlationId = `k6-${CENARIO}-${__VU}-${__ITER}`;

    const resposta = http.post(
        `${BFF_URL}/pagamentos`,
        JSON.stringify({ chavePix: chave, valor: caso.valor(), descricao: `carga ${nomeDoCaso}` }),
        {
            headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': correlationId },
            tags: { caso: nomeDoCaso },
        });

    check(resposta, {
        [`${nomeDoCaso}: status ${caso.esperado}`]: r => r.status === caso.esperado,
        [`${nomeDoCaso}: devolve X-Correlation-Id`]:
            r => r.headers['X-Correlation-Id'] === correlationId,
    });

    return resposta;
}

function consultar(respostaDaCriacao) {
    const pagamentoId = respostaDaCriacao.json('pagamentoId');

    const resposta = http.get(`${BFF_URL}/pagamentos/${pagamentoId}`, {
        headers: { 'X-Correlation-Id': respostaDaCriacao.headers['X-Correlation-Id'] },
        tags: { caso: 'consulta' },
    });

    check(resposta, { 'consulta: status 200': r => r.status === 200 });
}

function sortear(pesos) {
    const total = Object.values(pesos).reduce((soma, peso) => soma + peso, 0);
    let ponto = Math.random() * total;

    for (const [nome, peso] of Object.entries(pesos)) {
        ponto -= peso;
        if (ponto <= 0) return nome;
    }

    return Object.keys(pesos)[0];
}

function escolher(lista) {
    return lista[Math.floor(Math.random() * lista.length)];
}

function entre(minimo, maximo) {
    return Math.round((Math.random() * (maximo - minimo) + minimo) * 100) / 100;
}

function entreInteiro(minimo, maximo) {
    return Math.floor(Math.random() * (maximo - minimo + 1)) + minimo;
}

/// Formato "D" (8-4-4-4-12), o unico que o Core aceita como chave aleatoria.
function uuid() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, caractere => {
        const aleatorio = Math.random() * 16 | 0;
        const valor = caractere === 'x' ? aleatorio : (aleatorio & 0x3 | 0x8);
        return valor.toString(16);
    });
}
