using TaskManager.Infrastructure;

namespace TaskManager.Services;

///Некорректные входные данные (например, пустое название задачи). Ожидаемая ошибка: логируется как Warning
public sealed class TaskValidationException : ExpectedException
{
    public TaskValidationException(string message) : base(message) { }
}
