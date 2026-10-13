using TaskManager.Models;

namespace TaskManager.Data;

public interface ITaskRepository
{
    TaskItem Add(string title);
    bool Remove(int id);
    IReadOnlyList<TaskItem> List();
}
