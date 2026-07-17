namespace DnaX.Caching;

public sealed class DnaXCacheEntryOptions
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan StaleDuration { get; set; }

    public bool CacheNulls { get; set; } = true;

    public TimeSpan RefreshFailureCooldown { get; set; } = TimeSpan.FromMinutes(1);
}
