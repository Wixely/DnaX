using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DnaX.MCPFab;

/// <summary>
/// Serves <c>/favicon.ico</c> and brands the console window, replacing the ~4 KB
/// <c>McpSharpIcon.cs</c> that was identical modulo namespace in eighteen servers.
/// </summary>
/// <remarks>
/// MCPFab embeds the shared icon, so a server no longer needs its own
/// <c>EmbeddedResource</c> entry. A server that embeds <c>MCPSharp.wmcp.ico</c> still wins, which
/// keeps the existing servers working unchanged, and a server can supply bytes directly to use
/// something else entirely.
/// </remarks>
public static class McpFabIcon
{
    private const string McpFabResourceName = "DnaX.MCPFab.wmcp.ico";

    /// <summary>Resource name the existing MCPSharp servers embed their icon under.</summary>
    private const string LegacyResourceName = "MCPSharp.wmcp.ico";

    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int LrDefaultColor = 0;
    private const int WmSetIcon = 0x0080;
    private const uint ResourceVersion = 0x00030000;

    private static readonly Lazy<byte[]?> DefaultIcon = new(LoadDefaultIcon);

    /// <summary>
    /// The icon MCPFab will use when a server supplies none: the server's own
    /// <c>MCPSharp.wmcp.ico</c> if it embeds one, otherwise MCPFab's copy.
    /// </summary>
    public static byte[]? Default => DefaultIcon.Value;

    /// <summary>
    /// Maps <c>/favicon.ico</c>. Does nothing when no icon is available, because a missing
    /// favicon is cosmetic and must never stop a server from starting.
    /// </summary>
    public static void MapFavicon(this IEndpointRouteBuilder endpoints, byte[]? icon = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        byte[]? bytes = icon ?? Default;
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        // An explicit RequestDelegate rather than MapGet(Delegate): the delegate overloads
        // reflect over parameters and the return type, which is trim-unsafe.
        endpoints.MapMethods("/favicon.ico", ["GET"], (RequestDelegate)(context =>
        {
            context.Response.ContentType = "image/x-icon";
            return context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        }));
    }

    /// <summary>
    /// Brands the console window on Windows. No-op elsewhere, when there is no console (a
    /// Windows Service), or when no icon is available.
    /// </summary>
    public static void ApplyConsoleWindowIcon(byte[]? icon = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        byte[]? bytes = icon ?? Default;
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        try
        {
            ApplyWindowsConsoleIcon(bytes);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Branding is decoration. A malformed icon or a host without user32 must not take
            // the server down on startup, which the original implementation would have done.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsConsoleIcon(byte[] bytes)
    {
        IntPtr window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            return;
        }

        SetConsoleIcon(window, bytes, IconSmall, 16);
        SetConsoleIcon(window, bytes, IconBig, 32);
    }

    private static byte[]? LoadDefaultIcon()
    {
        // The server's own icon first, so a repo that still embeds one keeps its branding.
        if (Assembly.GetEntryAssembly() is { } entry && TryRead(entry, LegacyResourceName) is { } fromEntry)
        {
            return fromEntry;
        }

        return TryRead(typeof(McpFabIcon).Assembly, McpFabResourceName);
    }

    private static byte[]? TryRead(Assembly assembly, string resourceName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static void SetConsoleIcon(IntPtr window, byte[] ico, int iconSize, int desiredSize)
    {
        byte[] image = SelectIconImage(ico, desiredSize);
        IntPtr icon = CreateIconFromResourceEx(
            image, (uint)image.Length, true, ResourceVersion, desiredSize, desiredSize, LrDefaultColor);

        if (icon != IntPtr.Zero)
        {
            SendMessage(window, WmSetIcon, iconSize, icon);
        }
    }

    /// <summary>
    /// Picks the image closest to <paramref name="desiredSize"/>, preferring higher colour depth.
    /// </summary>
    /// <remarks>
    /// An ICO is a container and <c>CreateIconFromResourceEx</c> wants a single image, so the
    /// directory has to be walked by hand.
    /// </remarks>
    private static byte[] SelectIconImage(byte[] ico, int desiredSize)
    {
        if (ico.Length < 6 || ReadUInt16(ico, 2) != 1)
        {
            throw new InvalidOperationException("The MCPFab icon is not a valid ICO file.");
        }

        ushort count = ReadUInt16(ico, 4);
        int bestOffset = 0;
        int bestLength = 0;
        int bestScore = int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (i * 16);
            if (entry + 16 > ico.Length)
            {
                break;
            }

            // A zero width or height byte means 256 in the ICO format.
            int width = ico[entry] == 0 ? 256 : ico[entry];
            int height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            ushort bitCount = ReadUInt16(ico, entry + 6);
            int length = (int)ReadUInt32(ico, entry + 8);
            int offset = (int)ReadUInt32(ico, entry + 12);
            int score = Math.Abs(width - desiredSize) + Math.Abs(height - desiredSize) - bitCount;

            if (offset >= 0 && length > 0 && offset + length <= ico.Length && score < bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                bestLength = length;
            }
        }

        return bestLength > 0
            ? ico.AsSpan(bestOffset, bestLength).ToArray()
            : throw new InvalidOperationException("The MCPFab icon contains no icon images.");
    }

    private static ushort ReadUInt16(byte[] value, int startIndex) =>
        (ushort)(value[startIndex] | (value[startIndex + 1] << 8));

    private static uint ReadUInt32(byte[] value, int startIndex) =>
        (uint)(value[startIndex] | (value[startIndex + 1] << 8) | (value[startIndex + 2] << 16) | (value[startIndex + 3] << 24));

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(
        byte[] pbIconBits, uint cbIconBits, bool fIcon, uint dwVersion, int cxDesired, int cyDesired, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
}
