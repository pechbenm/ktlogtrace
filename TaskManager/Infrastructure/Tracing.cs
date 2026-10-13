using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace TaskManager.Infrastructure;

/// <summary>
/// Простейшая трассировка поверх System.Diagnostics.TraceSource.
/// Для каждой операции фиксирует: начало (ENTER), промежуточные шаги (STEP) и конец (EXIT)
/// с затраченным временем и результатом: success / warning / error.
/// Логирование (ILogger) отвечает на вопрос «что произошло», трассировка — «как мы к этому пришли».
/// </summary>
public static class Tracing
{
    private static readonly TraceSource Source = new("TaskManager", SourceLevels.All);
    private static StepTraceListener? _fileListener;
    private static int _sequence; // сквозной номер операции: связывает ENTER и EXIT

    /// <summary>Ключи Exception.Data: в какой трассируемой операции возникло исключение (читает ExceptionHandler).</summary>
    public const string FailedOperationKey = "Trace.FailedOperation";
    public const string FailedOperationIdKey = "Trace.FailedOperationId";

    [ThreadStatic] private static int _depth; // текущая вложенность вызовов (для отступов)

    /// <summary>Подключает вывод трассировки в файл и (опционально) в консоль.</summary>
    public static void Initialize(string filePath, bool toConsole, string? runId = null)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Source.Listeners.Clear(); // убираем DefaultTraceListener (вывод в окно Debug)

        _fileListener = new StepTraceListener(new StreamWriter(filePath, append: true) { AutoFlush = true });
        Source.Listeners.Add(_fileListener);

        if (toConsole)
            Source.Listeners.Add(new StepTraceListener(Console.Out));

        Step($"=== Трассировка запущена (runId={runId ?? "-"}) ===");
    }

    public static void Shutdown()
    {
        Step("=== Трассировка завершена ===");
        if (_fileListener is null) return;
        Source.Listeners.Remove(_fileListener);
        _fileListener.Dispose();
        _fileListener = null;
    }

    /// <summary>
    /// Выполняет операцию внутри области трассировки и ГАРАНТИРУЕТ итоговый результат:
    /// <list type="bullet">
    /// <item>success — тело отработало без исключения (или вызвало <c>scope.Success(...)</c>);</item>
    /// <item>warning — тело вызвало <c>scope.Warn(...)</c> (штатный отказ: «не найдено», «пустое название»);</item>
    /// <item>error — из тела вылетело исключение, которое никто не пометил заранее; оно пробрасывается дальше.</item>
    /// </list>
    /// Имя вида «Класс.Метод» берётся автоматически из имени файла и вызывающего метода.
    /// </summary>
    public static T Operation<T>(
        string? details,
        Func<TraceScope, T> body,
        [CallerMemberName] string method = "",
        [CallerFilePath] string file = "")
    {
        using var scope = new TraceScope(NameOf(file, method), details);
        try
        {
            var result = body(scope);
            scope.Success(); // ничего не меняет, если результат уже задан (Warn/Success)
            return result;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);     // ничего не меняет, если результат уже задан (например, Warn перед throw)
            scope.Annotate(ex); // запоминаем самую внутреннюю операцию для ExceptionHandler
            throw;
        }
    }

    /// <summary>
    /// Область для ручного управления результатом (используйте, когда <see cref="Operation{T}"/> неудобна).
    /// Если результат не задан, в строке EXIT будет «done» — область закрыта, но результат не заявлен.
    /// </summary>
    public static TraceScope Scope(
        string? details = null,
        [CallerMemberName] string method = "",
        [CallerFilePath] string file = "")
        => new(NameOf(file, method), details);

    /// <summary>Промежуточный шаг внутри текущей области.</summary>
    public static void Step(string message) => Write(TraceEventType.Verbose, message);

    internal static int NextId() => Interlocked.Increment(ref _sequence);
    internal static void Enter() => _depth++;
    internal static void Leave() => _depth = Math.Max(0, _depth - 1);

    internal static void Write(TraceEventType type, string message)
        => Source.TraceEvent(type, 0, new string(' ', _depth * 2) + message);

    private static string NameOf(string file, string method)
        => $"{Path.GetFileNameWithoutExtension(file)}.{method}";
}

public enum TraceOutcome
{
    Success,
    Warning,
    Error
}

/// <summary>
/// Область трассировки. ENTER пишется при создании, EXIT — при Dispose, вместе с временем и результатом.
/// Правило: первый заданный результат окончательный, последующие вызовы игнорируются.
/// </summary>
public sealed class TraceScope : IDisposable
{
    private readonly int _id;
    private readonly string _name;
    private readonly string? _details;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private TraceOutcome? _outcome;
    private string? _message;

    internal TraceScope(string name, string? details)
    {
        _name = name;
        _details = details;
        _id = Tracing.NextId();
        Tracing.Write(TraceEventType.Start, $"→ #{_id} {name}({details})");
        Tracing.Enter();
    }

    /// <summary>Операция выполнена. <paramref name="result"/> — краткое описание итога (например, «Id=3»).</summary>
    public void Success(string? result = null) => Complete(TraceOutcome.Success, result);

    /// <summary>Штатный отказ: операция не выполнена по понятной причине, это не сбой.</summary>
    public void Warn(string reason) => Complete(TraceOutcome.Warning, reason);

    /// <summary>Сбой: операция не выполнена из-за ошибки.</summary>
    public void Fail(string reason) => Complete(TraceOutcome.Error, reason);

    public void Fail(Exception ex) => Complete(TraceOutcome.Error, $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// Записывает в исключение, в какой операции оно возникло. Первая (самая внутренняя) пометка окончательна:
    /// внешние операции, через которые исключение пролетает дальше, её не перезаписывают.
    /// </summary>
    internal void Annotate(Exception ex)
    {
        if (ex.Data.Contains(Tracing.FailedOperationKey)) return;

        ex.Data[Tracing.FailedOperationKey] = $"{_name}({_details})";
        ex.Data[Tracing.FailedOperationIdKey] = _id;
    }

    private void Complete(TraceOutcome outcome, string? message)
    {
        if (_outcome is not null) return;
        _outcome = outcome;
        _message = message;
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        Tracing.Leave();

        var (type, word) = _outcome switch
        {
            TraceOutcome.Success => (TraceEventType.Stop, "success"),
            TraceOutcome.Warning => (TraceEventType.Warning, "warning"),
            TraceOutcome.Error => (TraceEventType.Error, "error"),
            _ => (TraceEventType.Stop, "done")
        };

        var ms = _stopwatch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
        var tail = string.IsNullOrEmpty(_message) ? "" : $" — {_message}";
        Tracing.Write(type, $"← #{_id} {_name} [{word}, {ms} мс]{tail}");
    }
}

/// <summary>Формат строки трассировки: «время [МЕТКА] сообщение» (без служебного шума TraceSource).</summary>
internal sealed class StepTraceListener : TextWriterTraceListener
{
    public StepTraceListener(TextWriter writer) : base(writer) { }

    public override void TraceEvent(
        TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        var label = eventType switch
        {
            TraceEventType.Start => "ENTER",
            TraceEventType.Stop => "EXIT ",
            TraceEventType.Verbose => "STEP ",
            TraceEventType.Error => "FAIL ",
            TraceEventType.Warning => "WARN ",
            _ => eventType.ToString().ToUpperInvariant()
        };

        WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{label}] {message}");
    }
}
