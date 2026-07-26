using System.Runtime.CompilerServices;

// A decisao do fornecedor e regra de negocio que se sustenta sozinha, entao
// merece teste unitario direto em vez de so via HTTP. O tipo continua
// internal: expor publicamente so para testar aumentaria a superficie do
// pacote sem necessidade.
[assembly: InternalsVisibleTo("Pagamentos.Proxy.Tests")]
