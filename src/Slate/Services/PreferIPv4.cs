using System.Net;
using System.Net.Sockets;

namespace Slate.Services;

/// <summary>
/// A <see cref="SocketsHttpHandler.ConnectCallback"/> that opens the connection over IPv4
/// when the host has an IPv4 address, and only falls back to IPv6 when it has none or IPv4
/// cannot connect.
///
/// Azure DevOps is reachable on both, and Windows offers IPv6 first. On some networks the
/// IPv6 route to Azure DevOps accepts the connection and then resets it partway through
/// the TLS handshake - "The SSL connection could not be established" - on every attempt,
/// while IPv4 to the same service works and Graph and sign-in work over IPv6. The reset
/// arrives after the connection is made, too late for anything to fall back on its own.
/// The update download uses it too, so it behaves the same on the same network.
/// </summary>
internal static class PreferIPv4
{
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        Exception? last = null;

        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
