namespace MaxPlanner.Presentation.Common;

/// <summary>Starting UI work that nobody awaits (A-02). Use on the UI thread.</summary>
public static class UiTasks
{
    /// <summary>
    /// Lets <paramref name="task"/> run on without losing its failure: an exception is rethrown on the UI thread, where
    /// the app's unhandled-exception handler logs it. This is what an <c>AsyncRelayCommand</c> does with a failed run,
    /// so code-behind that must start work itself (keys, drag and drop, menus built in code, row templates) behaves
    /// like a bound command. Prefer a view-model command wherever a control can bind one.
    /// </summary>
    public static async void Forget(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        await task.ConfigureAwait(true);
    }
}
