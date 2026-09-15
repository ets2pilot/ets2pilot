namespace Etssd.Bridge.Sensing;

internal static class DedicatedThread
{
    /// <summary>在新的后台线程上运行 body，返回的 Task 随 body 完成或抛出。</summary>
    public static Task RunAsync(string name, Action body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            Name = name,
            IsBackground = true,
        };
        thread.Start();
        return done.Task;
    }
}
