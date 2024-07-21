using AutoLockerApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

try
{
    var hostBuilder = Host.CreateDefaultBuilder(args);
    hostBuilder.ConfigureServices((context, services) =>
    {
        services.AddSingleton<WindowsHelper>();
        services.AddSingleton<BluetoothHelper>();

        services.AddHostedService<LockerService>();
    });

    var app = hostBuilder.Build();
    await app.RunAsync();
}
catch (Exception e)
{
    Console.WriteLine(e.ToString());
}