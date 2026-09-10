namespace DnaX.MCPFab;

/// <summary>
/// Startup banner writer handed to a server after the container is built.
/// </summary>
/// <remarks>
/// Every server logged an endpoint/transport/mode/content-root block and then its own posture
/// lines, computed from resolved services - which is why the banner is a callback taking
/// <see cref="IServiceProvider"/> rather than configuration. Servers with substantial banners
/// (Bambu enumerating printers, Portainer enumerating instances) keep that code in their own
/// repository and call into this; generalising those would pull domain shapes into MCPFab.
/// </remarks>
public interface IMcpFabBanner
{
    /// <summary>Writes a labelled line, for example <c>Read-only: true</c>.</summary>
    void Line(string label, string value);

    /// <summary>Writes an unlabelled detail line.</summary>
    void Detail(string text);

    /// <summary>
    /// Writes a warning line for a posture worth flagging at startup, such as a target with no
    /// credentials configured.
    /// </summary>
    void Warn(string text);
}
