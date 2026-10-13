using Microsoft.Extensions.Logging;
using TaskManager.Infrastructure;
using TaskManager.Services;

namespace TaskManager;

public sealed class ConsoleMenu
{
    private readonly TaskService _service;
    private readonly ExceptionHandler _exceptions;
    private readonly ILogger<ConsoleMenu> _logger;
    private readonly Action<Exception> _simulateStorageFailure; // только для демо-сценариев
    private int _exitCode;

    public ConsoleMenu(
        TaskService service,
        ExceptionHandler exceptions,
        ILogger<ConsoleMenu> logger,
        Action<Exception> simulateStorageFailure)
    {
        _service = service;
        _exceptions = exceptions;
        _logger = logger;
        _simulateStorageFailure = simulateStorageFailure;
    }

    /// <returns>Код выхода: 0 — штатно, 1 — завершение из-за критической (Fatal) ошибки.</returns>
    public int Run()
    {
        var running = true;
        while (running)
        {
            PrintMenu();
            running = Handle(Console.ReadLine()?.Trim());
        }

        return _exitCode;
    }

    private static void PrintMenu()
    {
        Console.WriteLine();
        Console.WriteLine("Менеджер задач");
        Console.WriteLine("1. Создать задачу");
        Console.WriteLine("2. Удалить задачу");
        Console.WriteLine("3. Показать список");
        Console.WriteLine("4. Демо-сценарий: success, warning, error");
        Console.WriteLine("5. Демо: критическая (Fatal) ошибка — приложение завершится");
        Console.WriteLine("6. Демо: необработанное исключение в потоке — аварийное завершение");
        Console.WriteLine("0. Выход");
        Console.Write("> ");
    }

    private bool Handle(string? choice)
    {
        // CommandId и Command добавляются в КАЖДУЮ запись лога, созданную при выполнении команды
        // (в том числе из TaskService и ExceptionHandler), — так можно собрать всю цепочку по одному идентификатору.
        // Тот же CommandId пишется в трассировку.
        var commandId = Guid.NewGuid().ToString("N")[..8];
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CommandId"] = commandId,
            ["Command"] = CommandName(choice)
        });
        using var scope = Tracing.Scope($"выбор=\"{choice}\", commandId={commandId}");

        try
        {
            switch (choice)
            {
                case "1": AddTask(); break;
                case "2": DeleteTask(); break;
                case "3": ShowTasks(); break;
                case "4": RunDemo(); break;
                case "5": RunFatalDemo(); break;
                case "6": RunUnhandledDemo(); break;
                case "0":
                case null: 
                    scope.Success("выход");
                    return false;
                default:
                    scope.Warn("неизвестный пункт меню");
                    _logger.LogWarning(LogEvents.UnknownCommand, "Неизвестный пункт меню: {Choice}", choice);
                    Console.WriteLine("Нет такого пункта.");
                    break;
            }
        }
        catch (Exception ex)
        {
         
            var error = _exceptions.Handle(ex, $"Command:{CommandName(choice)}");

            var summary = $"{ex.GetType().Name}, ErrorId={error.ErrorId}"; 
            if (error.Level >= LogLevel.Error) scope.Fail(summary); else scope.Warn(summary);

            Console.WriteLine(error.UserMessage);

            if (error.IsFatal)
            {
                _exitCode = 1;
                return false; 
            }
        }

        scope.Success(); 
        return true;
    }

    private static string CommandName(string? choice) => choice switch
    {
        "1" => "AddTask",
        "2" => "DeleteTask",
        "3" => "ShowTasks",
        "4" => "Demo",
        "5" => "FatalDemo",
        "6" => "UnhandledDemo",
        "0" or null => "Exit",
        _ => "Unknown"
    };

    private void AddTask()
    {
        Console.Write("Название задачи: ");
        var task = _service.Add(Console.ReadLine());
        Console.WriteLine($"Создана задача #{task.Id}: {task.Title}");
    }

    private void DeleteTask()
    {
        Console.Write("Id задачи для удаления: ");
        var input = Console.ReadLine();
        if (!int.TryParse(input, out var id))
        {
            Tracing.Step("некорректный Id — операция не выполнена");
            _logger.LogWarning(LogEvents.InvalidInput, "Введён некорректный Id задачи: {Input}", input);
            Console.WriteLine("Нужно ввести целое число.");
            return;
        }

        Console.WriteLine(_service.Remove(id) ? $"Задача #{id} удалена." : $"Задача #{id} не найдена.");
    }

    private void ShowTasks()
    {
        var tasks = _service.List();
        if (tasks.Count == 0)
        {
            Console.WriteLine("Список задач пуст.");
            return;
        }

        Console.WriteLine("ID  Создана           Название");
        foreach (var t in tasks)
            Console.WriteLine($"{t.Id,-3} {t.CreatedAt:dd.MM.yyyy HH:mm}  {t.Title}");
    }

    private void RunDemo()
    {
        Console.WriteLine("--- Демо-сценарий: success, warning, error ---");

        _service.Add("Купить молоко");   // success
        _service.Add("Написать отчёт");  // success
        ShowTasks();                     // List: success

        _service.Remove(1);              // success
        _service.Remove(999);            // warning: задача не найдена (не исключение, а штатный результат)

        try
        {
            _service.Add("   ");         // исключение TaskValidationException, уровень Warning
        }
        catch (Exception ex)
        {
            // Демо должно идти дальше, поэтому перехватывается здесь, но обрабатывает всё равно ExceptionHandler
            Console.WriteLine(_exceptions.Handle(ex, "Demo:EmptyTitle").UserMessage);
        }

        try
        {
            _simulateStorageFailure(new InvalidOperationException("Имитация сбоя хранилища (демо)."));
            _service.Add("Эта задача не сохранится"); // исключение хранилища, уровень Error
        }
        catch (Exception ex)
        {
            Console.WriteLine(_exceptions.Handle(ex, "Demo:StorageFailure").UserMessage);
        }

        ShowTasks();
        Console.WriteLine("Конец демо. Смотрите logs/app-*.json и logs/trace.log");
    }

    /// Критическая ошибка внутри команды: исключение долетает до Handle, ExceptionHandler присваивает
    /// уровень Fatal, приложение завершается с кодом 1.
    private void RunFatalDemo()
    {
        Console.WriteLine("Демо критической ошибки: приложение завершится");

        _simulateStorageFailure(new OutOfMemoryException("Имитация нехватки памяти (демо)."));
        _service.Add("Эта задача вызовет критическую ошибку");
    }

    /// Исключение в фоновом потоке не перехватывает никто, кроме глобального обработчика
    /// (ExceptionHandler.RegisterGlobalHandlers): он запишет Fatal, затем процесс аварийно завершится.
    private void RunUnhandledDemo()
    {
        Console.WriteLine("Демо необработанного исключения в потоке: аварийное завершение");

        var thread = new Thread(() => throw new InvalidOperationException("Необработанное исключение в фоновом потоке (демо)."));
        thread.Start();
        thread.Join();
    }
}
