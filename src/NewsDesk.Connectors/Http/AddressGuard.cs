using System.Net;
using System.Net.Sockets;

namespace NewsDesk.Connectors.Http;

/// <summary>Blocks loopback, link-local, private, and other non-public network targets.</summary>
public static class AddressGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                  // "this" network
                     || b[0] == 10                              // private
                     || b[0] == 127                             // loopback
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
                     || (b[0] == 169 && b[1] == 254)            // link-local
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // private
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0) // IETF protocol assignments
                     || (b[0] == 192 && b[1] == 168)            // private
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                     || b[0] >= 224);                           // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal) return false;
            var b = address.GetAddressBytes();
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // documentation
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) // NAT64: check embedded IPv4
                return IsPublic(new IPAddress(b[12..16]));
            return true;
        }

        return false;
    }
}
