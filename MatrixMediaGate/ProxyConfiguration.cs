namespace MatrixMediaGate;

public class ProxyConfiguration {
    //bind to config
    public ProxyConfiguration(IConfiguration configuration, IHostEnvironment env)
    {
        configuration.GetRequiredSection("ProxyConfiguration").Bind(this);
        DumpPath ??= Path.Combine("data", env.EnvironmentName, "dumps");
        Directory.CreateDirectory(DumpPath);
    }
    
    public required string Upstream { get; set; }
    public required string Host { get; set; }
    public required List<string> TrustedServers { get; set; }
    public required bool DumpFailedRequests { get; set; }
    public required string? DumpPath { get; set; }
}