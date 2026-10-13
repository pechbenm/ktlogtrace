using TaskManager.Infrastructure;
using TaskManager.Models;

namespace TaskManager.Data;

/// Хранилище задач в памяти (данные живут, пока работает приложение).
public sealed class InMemoryTaskRepository : ITaskRepository
{
    private readonly List<TaskItem> _items = new();
    private int _nextId = 1;

    public TaskItem Add(string title) =>
        Tracing.Operation($"title=\"{title}\"", scope =>
        {
            var item = new TaskItem(_nextId++, title, DateTime.Now);
            _items.Add(item);

            Tracing.Step($"запись добавлена в коллекцию, всего={_items.Count}");
            scope.Success($"Id={item.Id}");
            return item;
        });

    public bool Remove(int id) =>
        Tracing.Operation($"id={id}", scope =>
        {
            var index = _items.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                scope.Warn("запись не найдена");
                return false;
            }

            _items.RemoveAt(index);
            Tracing.Step($"запись удалена (индекс {index}), осталось={_items.Count}");
            scope.Success($"Id={id}");
            return true;
        });

    public IReadOnlyList<TaskItem> List() =>
        Tracing.Operation(null, scope =>
        {
            var snapshot = _items.ToArray(); // копия, чтобы снаружи нельзя было изменить коллекцию
            scope.Success($"записей: {snapshot.Length}");
            return snapshot;
        });
}
