namespace MediaHub.Core.Releases;

public static class WatchConcurrency
{
    public const int Maximum = 32;

    public static int Resolve(int configured, int processors, long availableMemoryBytes)
    {
        if (configured > 0) return Math.Clamp(configured, 1, Maximum);
        var cpuLimit = Math.Clamp((long)processors * 4, 1, Maximum);
        var memoryLimit = Math.Clamp(availableMemoryBytes / (128L * 1024 * 1024), 1, Maximum);
        return (int)Math.Min(cpuLimit, memoryLimit);
    }
}