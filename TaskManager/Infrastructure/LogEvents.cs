using Microsoft.Extensions.Logging;

namespace TaskManager.Infrastructure;

/// <summary>
/// Стабильные идентификаторы ключевых событий. В JSON-логе они попадают в поле EventId
/// ({"Id":1001,"Name":"TaskCreated"}), поэтому по ним удобно фильтровать и считать записи,
/// не завися от текста сообщения.
/// </summary>
public static class LogEvents
{
    // Жизненный цикл приложения: 1–99
    public static readonly EventId AppStarted = new(1, nameof(AppStarted));
    public static readonly EventId AppStopped = new(2, nameof(AppStopped));

    // Операции с задачами: 1000–1999
    public static readonly EventId TaskCreated = new(1001, nameof(TaskCreated));
    public static readonly EventId TaskDeleted = new(1002, nameof(TaskDeleted));
    public static readonly EventId TaskNotFound = new(1003, nameof(TaskNotFound));
    public static readonly EventId TasksListed = new(1004, nameof(TasksListed));

    // Пользовательский интерфейс: 2000–2999
    public static readonly EventId UnknownCommand = new(2001, nameof(UnknownCommand));
    public static readonly EventId InvalidInput = new(2002, nameof(InvalidInput));

    // Исключения (пишет только ExceptionHandler): 3000–3999
    public static readonly EventId ExpectedError = new(3001, nameof(ExpectedError));           // Warning: ожидаемая ошибка (ввод, правила)
    public static readonly EventId OperationFailed = new(3002, nameof(OperationFailed));       // Error: непредвиденный сбой операции
    public static readonly EventId FatalError = new(3003, nameof(FatalError));                 // Fatal: критический сбой, перехваченный в коде
    public static readonly EventId UnhandledException = new(3004, nameof(UnhandledException)); // Fatal: никто не перехватил (AppDomain)
    public static readonly EventId UnobservedTaskException = new(3005, nameof(UnobservedTaskException)); // Error: потерянное исключение Task
}
