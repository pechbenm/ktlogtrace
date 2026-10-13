using Microsoft.Extensions.Logging;

namespace TaskManager.Infrastructure;

/// <summary>
/// Базовый класс для ОЖИДАЕМЫХ ошибок: неверный ввод, нарушение правил. Это не сбой приложения,
/// поэтому такие исключения логируются как Warning, а текст ошибки показывается пользователю как есть.
/// </summary>
public abstract class ExpectedException : Exception
{
    protected ExpectedException(string message) : base(message) { }
}

/// <summary>Итог обработки исключения: что показать пользователю и насколько всё серьёзно.</summary>
/// <param name="ErrorId">Короткий код ошибки: он есть и в логе, и в трассировке, и в сообщении пользователю.</param>
public sealed record HandledError(string ErrorId, LogLevel Level, string UserMessage)
{
    /// <summary>Критическая (Fatal) ошибка: после неё работу приложения нужно завершить.</summary>
    public bool IsFatal => Level >= LogLevel.Critical;
}

/// <summary>
/// ЕДИНАЯ точка обработки исключений. Все catch в приложении вызывают <see cref="Handle"/>,
/// а необработанные исключения попадают сюда через <see cref="RegisterGlobalHandlers"/>.
/// Для каждого исключения пишется ОДНА запись лога с полями:
/// <list type="bullet">
/// <item><b>ErrorMessage</b> — Exception.Message;</item>
/// <item><b>StackTrace</b> — Exception.StackTrace;</item>
/// <item><b>Operation</b> — где перехвачено; <b>FailedOperation</b> и <b>TraceOperationId</b> — в какой трассируемой
/// операции возникло (с аргументами и номером в trace.log);</item>
/// <item><b>Severity</b> и уровень записи (@l) — Warning / Error / Fatal;</item>
/// <item><b>ErrorId</b>, <b>ErrorType</b> — код и тип исключения.</item>
/// </list>
/// Метод никогда не бросает исключений сам.
/// </summary>
public sealed class ExceptionHandler
{
    private readonly ILogger<ExceptionHandler> _logger;

    public ExceptionHandler(ILogger<ExceptionHandler> logger) => _logger = logger;

    /// <param name="exception">Перехваченное исключение.</param>
    /// <param name="operation">Контекст: что выполнялось в момент перехвата, например «Command:AddTask».</param>
    /// <param name="level">Принудительный уровень. Если не задан, определяется по типу исключения.</param>
    /// <param name="eventId">Принудительный идентификатор события. Если не задан, выбирается по уровню.</param>
    public HandledError Handle(
        Exception exception, string operation, LogLevel? level = null, EventId? eventId = null)
    {
        var errorId = Guid.NewGuid().ToString("N")[..8];
        var severity = level ?? Classify(exception);

        try
        {
            // Поля, которых нет в тексте сообщения, но которые должны попасть в JSON отдельными полями
            var context = new Dictionary<string, object>
            {
                ["ErrorType"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["StackTrace"] = exception.StackTrace ?? "(стек вызовов недоступен)",
                ["Severity"] = SeverityName(severity)
            };

            // Tracing.Operation помечает исключение самой внутренней операцией, где оно возникло
            if (exception.Data[Tracing.FailedOperationKey] is string failedOperation)
                context["FailedOperation"] = failedOperation;
            if (exception.Data[Tracing.FailedOperationIdKey] is int traceOperationId)
                context["TraceOperationId"] = traceOperationId;

            using (_logger.BeginScope(context))
            {
                _logger.Log(
                    severity,
                    eventId ?? EventFor(severity),
                    // объект исключения (с вложенными исключениями) прикладываем только к Error и выше:
                    // для Warning стек уже есть в поле StackTrace, а консоль не засоряется
                    severity >= LogLevel.Error ? exception : null,
                    "Ошибка #{ErrorId} в {Operation}: {ErrorMessage}",
                    errorId, operation, exception.Message);
            }
        }
        catch (Exception loggingFailure)
        {
            // Обработчик ошибок не имеет права падать сам: если логирование сломалось, пишем в stderr
            Console.Error.WriteLine(
                $"[ExceptionHandler] не удалось записать ошибку #{errorId}: {loggingFailure.Message}. " +
                $"Исходная ошибка: {exception}");
        }

        return new HandledError(errorId, severity, UserMessageFor(exception, severity, errorId));
    }

    /// <summary>
    /// Подключает последний рубеж: исключения, которые никто не перехватил.
    /// </summary>
    /// <param name="onFatal">Вызывается после записи Fatal-ошибки, перед аварийным завершением процесса
    /// (здесь нужно сбросить буферы логов).</param>
    public void RegisterGlobalHandlers(Action? onFatal = null)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception
                ?? new InvalidOperationException($"Выброшен объект, не являющийся исключением: {e.ExceptionObject}");

            Handle(exception, "AppDomain.UnhandledException", LogLevel.Critical, LogEvents.UnhandledException);
            onFatal?.Invoke();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle(e.Exception, "TaskScheduler.UnobservedTaskException", LogLevel.Error, LogEvents.UnobservedTaskException);
            e.SetObserved(); // не даём этому исключению завершить процесс
        };
    }

    /// <summary>Уровень по умолчанию, исходя из типа исключения.</summary>
    private static LogLevel Classify(Exception exception)
    {
        if (exception is ExpectedException)
            return LogLevel.Warning;

        if (exception is OutOfMemoryException or AccessViolationException)
            return LogLevel.Critical; // состояние процесса ненадёжно, продолжать нельзя

        return LogLevel.Error;
    }

    private static EventId EventFor(LogLevel level) => level switch
    {
        LogLevel.Critical => LogEvents.FatalError,
        LogLevel.Error => LogEvents.OperationFailed,
        _ => LogEvents.ExpectedError
    };

    // В .NET уровень называется Critical, в Serilog и в JSON-логе — Fatal. В поле Severity пишем «Fatal».
    private static string SeverityName(LogLevel level) => level switch
    {
        LogLevel.Critical => "Fatal",
        LogLevel.Error => "Error",
        LogLevel.Warning => "Warning",
        _ => level.ToString()
    };

    private static string UserMessageFor(Exception exception, LogLevel level, string errorId)
    {
        if (level >= LogLevel.Critical)
            return $"Критическая ошибка (код {errorId}). Приложение будет закрыто, подробности в логе.";

        if (level >= LogLevel.Error)
            return $"Произошла непредвиденная ошибка (код {errorId}). Подробности в логе.";

        return $"Ошибка: {exception.Message}"; // ожидаемая ошибка: текст безопасен для пользователя
    }
}
