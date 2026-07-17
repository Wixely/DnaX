namespace DnaX.Caching;

public sealed class DnaXCacheOptions
{
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan DefaultStaleDuration { get; set; }

    public bool CacheNullsByDefault { get; set; } = true;

    public TimeSpan DefaultRefreshFailureCooldown { get; set; } = TimeSpan.FromMinutes(1);
}
