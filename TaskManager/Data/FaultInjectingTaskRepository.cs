using TaskManager.Infrastructure;
using TaskManager.Models;

namespace TaskManager.Data;

/// Декоратор только для демонстрации: по запросу «ломает» следующий вызов хранилища заданным исключением,
/// чтобы можно было увидеть обработку Error и Fatal.

public sealed class FaultInjectingTaskRepository : ITaskRepository
{
    private readonly ITaskRepository _inner;
    private Exception? _pending;

    public FaultInjectingTaskRepository(ITaskRepository inner) => _inner = inner;

    ///Следующий вызов любого метода хранилища завершится этим исключением.
    public void FailNextCall(Exception exception) => _pending = exception;

    public TaskItem Add(string title)
    {
        ThrowIfRequested();
        return _inner.Add(title);
    }

    public bool Remove(int id)
    {
        ThrowIfRequested();
        return _inner.Remove(id);
    }

    public IReadOnlyList<TaskItem> List()
    {
        ThrowIfRequested();
        return _inner.List();
    }

    private void ThrowIfRequested()
    {
        if (_pending is null) return;

        var exception = _pending;
        _pending = null;
        Tracing.Step($"имитация сбоя хранилища: {exception.GetType().Name}");
        throw exception;
    }
}
