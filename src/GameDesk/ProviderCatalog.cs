using System.IO.Compression;
using System.Net;

namespace GameDesk;

internal sealed class ProviderCatalog
{
    private readonly (IPNetwork Network, string Label)[] prefixes;

    private ProviderCatalog((IPNetwork, string)[] prefixes) => this.prefixes = prefixes;

    public static ProviderCatalog Load()
    {
        using var resource = typeof(ProviderCatalog).Assembly.GetManifestResourceStream("GameDesk.Providers");
        if (resource is null)
            return new([]);
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var prefixes = new List<(IPNetwork, string)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int tab = line.IndexOf('\t');
            if (tab > 0 && IPNetwork.TryParse(line.AsSpan(0, tab), out var network))
                prefixes.Add((network, line[(tab + 1)..]));
        }
        // The source table uses the same longest-prefix order as eFootball Net.
        return new(prefixes.ToArray());
    }

    public string Resolve(string endpoint)
    {
        if (!IPEndPoint.TryParse(endpoint, out var target))
            return "Unknown provider";
        var address = target.Address.IsIPv4MappedToIPv6 ? target.Address.MapToIPv4() : target.Address;
        var bytes = address.GetAddressBytes();
        bool privateAddress = IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || (bytes.Length == 16
            ? (bytes[0] & 0xfe) == 0xfc
            : bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254));
        if (privateAddress)
            return "Local / private";
        foreach (var (network, label) in prefixes)
        {
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address))
                return label;
        }
        return "Unknown provider";
    }
}
