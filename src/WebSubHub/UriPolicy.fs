namespace WebSubHub

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

module UriPolicy =
    let rec isPrivateAddress (address: IPAddress) =
        if address.AddressFamily = AddressFamily.InterNetworkV6 then
            if address.IsIPv4MappedToIPv6 then
                isPrivateAddress (address.MapToIPv4())
            else
                let bytes = address.GetAddressBytes()
                let uniqueLocal = (bytes[0] &&& 0xfeuy) = 0xfcuy
                let documentationRange = bytes[0] = 0x20uy && bytes[1] = 0x01uy && bytes[2] = 0x0duy && bytes[3] = 0xb8uy

                address.Equals(IPAddress.IPv6Any)
                || IPAddress.IsLoopback(address)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || uniqueLocal
                || documentationRange
        else
            let bytes = address.GetAddressBytes()
            let a, b = int bytes[0], int bytes[1]

            a = 0
            || a = 10
            || a = 127
            || a >= 224
            || (a = 169 && b = 254)
            || (a = 172 && b >= 16 && b <= 31)
            || (a = 192 && b = 168)
            || (a = 100 && b >= 64 && b <= 127)
            || (a = 192 && b = 0)
            || (a = 198 && (b = 18 || b = 19))

    let isSafeAsync (allowLocal: bool) (uri: Uri) (cancellationToken: CancellationToken) : Task<bool> =
        task {
            let validScheme = uri.Scheme = Uri.UriSchemeHttps || uri.Scheme = Uri.UriSchemeHttp
            let noCredentials = String.IsNullOrEmpty(uri.UserInfo)
            let localHost = uri.IsLoopback || uri.DnsSafeHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)

            if not validScheme || not noCredentials then
                return false
            elif allowLocal && localHost then
                return true
            else
                let! addresses =
                    match IPAddress.TryParse(uri.DnsSafeHost) with
                    | true, address -> Task.FromResult([| address |])
                    | _ -> Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken)

                return addresses.Length > 0 && addresses |> Array.forall (isPrivateAddress >> not)
        }
