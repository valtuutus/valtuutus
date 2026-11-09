namespace Valtuutus.Tests.Containers;

/// <summary>
/// An exclusive lock shared by every process on the machine, held as an open lock file.
/// </summary>
internal static class CrossProcessLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static async Task<IAsyncDisposable> AcquireAsync(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}.lock");
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
    }
}
