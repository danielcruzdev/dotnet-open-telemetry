// A instrumentacao do ASP.NET Core escuta o DiagnosticSource do processo
// inteiro. Com classes em paralelo, spans de uma caem no exporter da outra.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
