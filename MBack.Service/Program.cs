using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;

// ★変更: 誰でもアクセスできる共通の場所(AppData/Local/MBack/Logs)にする
string logFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
    "MBack", 
    "Logs");

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "MBackService";
    })
    .UseSerilog((context, services, configuration) => configuration
        // 保存先を logFolder に設定
        .WriteTo.File(Path.Combine(logFolder, "log-.txt"), 
            rollingInterval: RollingInterval.Day,
            encoding: System.Text.Encoding.UTF8)
        .WriteTo.Console())
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<MBack.Service.Worker>();
    });

var host = builder.Build();
host.Run();