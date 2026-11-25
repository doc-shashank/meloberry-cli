using System.Net;
using System.Net.Sockets;

namespace Meloberry.Infrastructure.Web;

public static class WebHostUtilities
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".ttf"] = "font/ttf"
    };

    public static string GetMimeType(string ext) =>
        MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";

    public static int FindFreeLoopbackPort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public static (int Port1, int Port2) FindFreeLoopbackPortPair()
    {
        using var tcp1 = new TcpListener(IPAddress.Loopback, 0);
        tcp1.Start();
        using var tcp2 = new TcpListener(IPAddress.Loopback, 0);
        tcp2.Start();
        return (((IPEndPoint)tcp1.LocalEndpoint).Port, ((IPEndPoint)tcp2.LocalEndpoint).Port);
    }
}
