var builder = DistributedApplication.CreateBuilder(args);

// Os nomes de recurso abaixo sao contrato: os clientes tipados os usam como
// "https+http://<nome>" para service discovery. Renomear aqui exige renomear la.
var proxy = builder.AddProject<Projects.Pagamentos_Proxy>("pagamentos-proxy");

var core = builder.AddProject<Projects.Pagamentos_Core>("pagamentos-core")
    .WithReference(proxy)
    .WaitFor(proxy);

builder.AddProject<Projects.Pagamentos_Bff>("pagamentos-bff")
    .WithReference(core)
    .WaitFor(core)
    .WithExternalHttpEndpoints();

builder.Build().Run();
