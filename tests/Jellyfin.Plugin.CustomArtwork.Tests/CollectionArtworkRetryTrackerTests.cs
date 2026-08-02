namespace Jellyfin.Plugin.CustomArtwork.Tests;

public sealed class CollectionArtworkRetryTrackerTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(20, 60)]
    public void RetryDelayUsesBoundedExponentialBackoff(int attempts, int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            CollectionArtworkRetryTracker.RetryDelay(attempts));
    }
}
