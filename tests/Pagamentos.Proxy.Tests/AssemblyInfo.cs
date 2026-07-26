// A instrumentacao do ASP.NET Core escuta o DiagnosticSource do processo
// inteiro, nao de um host. Com classes de teste em paralelo, o span do
// servidor de uma cai no exporter em memoria da outra e as asercoes por
// span passam a depender de quem rodou junto.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
