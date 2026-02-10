using Microsoft.Extensions.Hosting;
using Serilog;
using System.IO;
using System;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "MBackService";
    })
    .UseSerilog((context, services, configuration) => configuration
        // ログの保存先を「実行ファイルがあるフォルダ/Logs」に固定
        .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "Logs", "log-.txt"), 
            rollingInterval: RollingInterval.Day,
            encoding: System.Text.Encoding.UTF8)
        .WriteTo.Console())
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<MBack.Service.Worker>();
    });

var host = builder.Build();
host.Run();