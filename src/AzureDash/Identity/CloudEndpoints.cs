using Azure.Identity;
using Azure.ResourceManager;
using AzureDash.Configuration;

namespace AzureDash.Identity;

public sealed record CloudEndpoints(Uri AuthorityHost, ArmEnvironment Arm, string ArmScope)
{
    public static CloudEndpoints For(AzureCloud cloud) => cloud switch
    {
        AzureCloud.UsGov => new(AzureAuthorityHosts.AzureGovernment, ArmEnvironment.AzureGovernment, "https://management.usgovcloudapi.net/.default"),
        AzureCloud.China => new(AzureAuthorityHosts.AzureChina, ArmEnvironment.AzureChina, "https://management.chinacloudapi.cn/.default"),
        _ => new(AzureAuthorityHosts.AzurePublicCloud, ArmEnvironment.AzurePublicCloud, "https://management.azure.com/.default"),
    };
}
