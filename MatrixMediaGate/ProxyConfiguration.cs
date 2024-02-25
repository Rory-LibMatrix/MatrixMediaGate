namespace MatrixMediaGate;

public class ProxyConfiguration {
    //bind to config
    public ProxyConfiguration(IConfiguration configuration)
    {
        configuration.GetRequiredSection("ProxyConfiguration").Bind(this);
    }
    
    public required string Upstream { get; set; }
    public required string Host { get; set; }
    public required List<string> TrustedServers { get; set; }
    public bool ForceHost { get; set; }
}