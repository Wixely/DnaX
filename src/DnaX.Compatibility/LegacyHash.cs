namespace DnaX.Compatibility;

/// <summary>Golden-compatible hash functions from the original DNA library.</summary>
public static class LegacyHash
{
    public static ulong Knuth(params string?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Knuth(string.Concat(values.Select(static value => value ?? string.Empty)));
    }

    public static ulong Knuth(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ulong hash = 3_074_457_345_618_258_791UL;
        foreach (char character in value)
        {
            unchecked
            {
                hash += character;
                hash *= 3_074_457_345_618_258_799UL;
            }
        }

        return hash;
    }
}
