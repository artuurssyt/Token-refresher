namespace LocaltsAccountManager.ViewModels;

public interface IUiDispatcher
{
    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> func);
}
