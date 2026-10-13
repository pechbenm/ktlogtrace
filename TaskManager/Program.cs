using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using TaskManager;
using TaskManager.Data;
using TaskManager.Infrastructure;
using TaskManager.Services;

Console.OutputEncoding = Encoding.UTF8;

var traceToConsole = args.Contains("--trace");

// Идентификатор запуска
var runId = Guid.NewGuid().ToString("N")[..8];

//Логирование 
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.WithProperty("Application", "TaskManager")
    .Enrich.WithProperty("RunId", runId)
    .Enrich.WithProperty("ProcessId", Environment.ProcessId)
    .Enrich.WithProperty("Machine", Environment.MachineName)
    .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        new RenderedCompactJsonFormatter(),   // JSON + готовый текст сообщения в поле @m
        "logs/app-.json",
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger));
var logger = loggerFactory.CreateLogger<Program>();

//Трассировка 
Tracing.Initialize("logs/trace.log", traceToConsole, runId);

var exceptionHandler = new ExceptionHandler(loggerFactory.CreateLogger<ExceptionHandler>());
exceptionHandler.RegisterGlobalHandlers(onFatal: () =>
{
    Tracing.Shutdown();
    Log.CloseAndFlush();
});
 
var exitCode = 0;
try
{
    logger.LogInformation(LogEvents.AppStarted,
        "Приложение запущено трассировка в консоль: {TraceToConsole} (режим трассировки: dotnet run -- --trace)", traceToConsole);

    var storage = new InMemoryTaskRepository();
    var faultInjector = new FaultInjectingTaskRepository(storage); // нужен только демо-сценариям
    var service = new TaskService(faultInjector, loggerFactory.CreateLogger<TaskService>());
    var menu = new ConsoleMenu(service, exceptionHandler, loggerFactory.CreateLogger<ConsoleMenu>(), faultInjector.FailNextCall);

    exitCode = menu.Run();
}
catch (Exception ex)
{
    exceptionHandler.Handle(ex, "Program.Main", LogLevel.Critical);
    exitCode = 1;
}
finally
{
    logger.LogInformation(LogEvents.AppStopped, "Приложение завершено (код выхода: {ExitCode})", exitCode);
    Tracing.Shutdown();
    Log.CloseAndFlush();
}

return exitCode;
