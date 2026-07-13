using Plenipo.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("plenipo");
    config.AddCommand<InitCommand>("init")
        .WithDescription("Configure a Plenipo host: AI provider, knowledge pipeline, channels, storage, auth.")
        .WithExample("init", "--path", "./src/MyProduct.Host")
        .WithExample("init", "--non-interactive", "--ai-provider", "Mock", "--rag", "--embedding-provider", "Mock");
});
return app.Run(args);
