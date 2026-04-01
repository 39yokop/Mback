using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.IO;

// ★変更なし: ログの保存先は誰でもアクセスできる共通の場所(AppData/Local/MBack/Logs)
string logFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MBack",
    "Logs");

// ログフォルダが存在しない場合は作成しておく（初回起動時のエラー防止）
if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "MBackService";
    })
    .UseSerilog((context, services, configuration) => configuration
        // ログファイルを日ごとにローテーションして保存する
        .WriteTo.File(
            Path.Combine(logFolder, "log-.txt"),
            rollingInterval: RollingInterval.Day,
            encoding: System.Text.Encoding.UTF8)
        .WriteTo.Console())
    .ConfigureServices((hostContext, services) =>
    {
        // ★★★ 最重要修正 ★★★
        // BackgroundService(Worker)の中で例外が発生しても、
        // サービスホスト全体を道連れにして停止させない設定。
        // デフォルトは StopHost なので、HistoryLoggerのDLLエラー1発で
        // バックアップ全体が止まっていた。これで本業(バックアップ)は継続される。
        services.Configure<HostOptions>(opts =>
        {
            opts.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.Ignore;
        });

        // Workerサービスを登録する
        services.AddHostedService<MBack.Service.Worker>();
    });

var host = builder.Build();
host.Run();
