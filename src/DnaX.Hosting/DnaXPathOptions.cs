namespace DnaX.Hosting;

public sealed class DnaXPathOptions
{
    /// <summary>
    /// A rooted path, or a path relative to the host content root, used for mutable application data.
    /// Defaults to a <c>data</c> directory below the content root.
    /// </summary>
    public string WritableDataRoot { get; set; } = "data";
}
