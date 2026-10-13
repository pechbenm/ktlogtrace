using Microsoft.Extensions.Logging;
using TaskManager.Data;
using TaskManager.Infrastructure;
using TaskManager.Models;

namespace TaskManager.Services;

/// Основные операции: Add, Remove, List. Каждая обёрнута в Tracing.Operation:
/// в трассировке будут начало, конец, время и результат (success / warning / error).
public sealed class TaskService
{
    private readonly ITaskRepository _repository;
    private readonly ILogger<TaskService> _logger;

    public TaskService(ITaskRepository repository, ILogger<TaskService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public TaskItem Add(string? title) =>
        Tracing.Operation($"title=\"{title}\"", scope =>
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                scope.Warn("пустое название"); // штатный отказ: warning, а не error
                // не логируется здесь: исключение попадёт в ExceptionHandler, который запишет его один раз
                throw new TaskValidationException("Название задачи не может быть пустым.");
            }

            Tracing.Step("валидация пройдена, вызываю репозиторий");
            var task = _repository.Add(title.Trim());

            // {TaskId} и {Title} попадут в JSON как отдельные поля, а не только в текст сообщения
            _logger.LogInformation(LogEvents.TaskCreated, "Создана задача #{TaskId}: {Title}", task.Id, task.Title);
            scope.Success($"Id={task.Id}");
            return task;
        });

    public bool Remove(int id) =>
        Tracing.Operation($"id={id}", scope =>
        {
            var removed = _repository.Remove(id);
            if (removed)
            {
                _logger.LogInformation(LogEvents.TaskDeleted, "Удалена задача #{TaskId}", id);
                scope.Success($"Id={id}");
            }
            else
            {
                scope.Warn("задача не найдена");
                _logger.LogWarning(LogEvents.TaskNotFound, "Задача #{TaskId} не найдена, удалять нечего", id);
            }

            return removed;
        });

    public IReadOnlyList<TaskItem> List() =>
        Tracing.Operation(null, scope =>
        {
            var tasks = _repository.List();
            _logger.LogDebug(LogEvents.TasksListed, "Получен список задач, количество: {Count}", tasks.Count); // только в JSON-файл
            scope.Success($"записей: {tasks.Count}");
            return tasks;
        });
}
