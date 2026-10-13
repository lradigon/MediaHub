using MediaHub.Core.Releases;
using Xunit;

namespace MediaHub.Tests;

public class WatchConcurrencyTests
{
    [Theory]
    [InlineData(0, 2, 3072, 8)]
    [InlineData(0, 16, 8192, 32)]
    [InlineData(0, 8, 256, 2)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(5, 2, 3072, 5)]
    [InlineData(100, 2, 3072, 32)]
    public void CapacityRespectsCpuMemoryAndSafetyLimit(int configured, int processors, long memoryMiB, int expected)
    {
        Assert.Equal(expected, WatchConcurrency.Resolve(configured, processors, memoryMiB * 1024 * 1024));
    }
}