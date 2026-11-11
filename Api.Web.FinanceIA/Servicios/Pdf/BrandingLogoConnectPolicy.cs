// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Net;
using System.Net.Sockets;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The <see cref="SocketsHttpHandler.ConnectCallback"/> of the logo client. It resolves the host ITSELF, refuses
/// the connection when ANY resolved address is not public (<see cref="BrandingLogoAddressPolicy"/>), and then
/// connects to one of the addresses it just validated — so there is no window between the check and the connect
/// for a DNS answer to change (rebinding). TLS is layered on top by the handler with the URL's host name, so the
/// certificate is still checked against the host, not the IP.
/// </summary>
internal static class BrandingLogoConnectPolicy
{
    /// <summary>The system resolver.</summary>
    internal static readonly Func<string, CancellationToken, Task<IPAddress[]>> SystemResolver =
        static (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <summary>The callback the registered handler uses.</summary>
    internal static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) =>
        ConnectAsync(context.DnsEndPoint, SystemResolver, ct);

    /// <summary>
    /// Resolves <paramref name="endPoint"/> with <paramref name="resolve"/> (a literal IP is taken as is), refuses
    /// with <see cref="BrandingLogoBlockedAddressException"/> before opening any socket when an address is not
    /// public or there is none, and otherwise connects to the validated addresses in order.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(resolve);

        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out IPAddress? literal)
            ? [literal]
            : await resolve(endPoint.Host, ct).ConfigureAwait(false);

        if (addresses.Length == 0 || addresses.Any(a => !BrandingLogoAddressPolicy.IsAllowed(a)))
            throw new BrandingLogoBlockedAddressException(endPoint.Host);

        Exception? last = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
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

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
