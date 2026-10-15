-- key:         sql.inv.host_info
-- purpose:     Host OS platform, distribution, release
-- min_version: 14
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT host_platform AS HostPlatform, host_distribution AS HostDistribution, host_release AS HostRelease,
       host_service_pack_level AS HostServicePackLevel, host_sku AS HostSku
FROM sys.dm_os_host_info;
