// A instrumentacao do ASP.NET Core escuta o DiagnosticSource do processo
// inteiro. Com classes em paralelo, o span de servidor de uma cai no
// exporter em memoria da outra.
//
// Isto passou despercebido enquanto existia uma unica classe com asercoes
// sobre span: dentro da mesma classe o xUnit ja roda em sequencia. A
// segunda classe expos o problema de imediato.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
