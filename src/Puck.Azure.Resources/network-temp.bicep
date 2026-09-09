param location string = resourceGroup().location
param virtualNetworkResourceId string = '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/virtualNetworks/bytrcvnetp000'

module dnsResolver 'br/public:avm/res/network/dns-resolver:0.5.6' = {
  params: {
    enableTelemetry: false
    inboundEndpoints: [
      {
        name: 'default'
        privateIpAllocationMethod: 'Dynamic'
        subnetResourceId: '${virtualNetworkResourceId}/subnets/DnsResolverSubnet'
      }
    ]
    location: location
    name: 'bytrcdnsprp000'
    virtualNetworkResourceId: virtualNetworkResourceId
  }
}
module puplicIpAddress 'br/public:avm/res/network/public-ip-address:0.12.0' = {
  params: {
    availabilityZones: [
      1
      2
      3
    ]
    enableTelemetry: false
    location: location
    lock: {
      kind: 'None'
    }
    name: 'bytrcpipp000'
    publicIPAddressVersion: 'IPv4'
    publicIPAllocationMethod: 'Static'
    skuName: 'Standard'
    skuTier: 'Regional'
  }
}
module vpnGateway 'br/public:avm/res/network/virtual-network-gateway:0.10.1' = {
  params: {
    allowRemoteVnetTraffic: false
    allowVirtualWanTraffic: false
    clientRevokedCertThumbprint: null
    clientRootCertData: 'MIIC5zCCAc+gAwIBAgIQPaNKUHuMwKRDFRWTk58EhTANBgkqhkiG9w0BAQsFADAW MRQwEgYDVQQDDAtQMlNSb290Q2VydDAeFw0yNjA0MjUxOTU5MTBaFw0yODA0MjUy MDA5MDhaMBYxFDASBgNVBAMMC1AyU1Jvb3RDZXJ0MIIBIjANBgkqhkiG9w0BAQEF AAOCAQ8AMIIBCgKCAQEA18gv6FcJ2A/h9uTEX7Zyqvu/43bDtUNiQMdMtEdje3jA atjKdNdsMnBi0C1TClErASQizx5Z+ACkF3qR6ZWw9COJ6mZaA6F4F9L7nZLDGpZ1 3V7lpfNhbKyq4BxZRu3t52yyW2pDC4bi554oLSNw6BSsN46K9RcOAOy6tKsIhliL Ewi5BknD8zrsmvWQIuBvLsZo50Btn7qSTqR/JYwSyUcHuYQrL89lBZ2E2GPPmvOj m6SHozXAlmWvZKT/oCM2z95EO3+YpZmdjpA6LnHKsvURq6370ybzlsz7/PuHNBXo UD8alA9Ag/E/DmEemky9iHNSBPalWf7B9D6b4lretQIDAQABozEwLzAOBgNVHQ8B Af8EBAMCAgQwHQYDVR0OBBYEFMo7shKnDjeGWc1UlOgB4vml1HgOMA0GCSqGSIb3 DQEBCwUAA4IBAQAzuNWW6OJyENpaj9nFbBxjDtmvocNR1fWpvGq1fKpDuD9M7lIW ArhgUac7hDqiqPf2zMTxZQ2oLeTzyaM3s/m336fK4HsLWrbGKYDkouwm1R3B1mxE 15PKcABln9+VLJB/X09ku4DL0pi73cse6VFqxmhdL/zo124DGsAD550Yx8pdt3kF hfMeXFuuW9dMHVtcQmjeT+boTKuA1gArqMDl7OemAxP49N+cdDn2p2Xx+YeQQhyf ZKPDKzu8+FsPCDzsE6KZ458xNG4S2Xd4ZRYt6sQiMHF/es+pLDWNp0ycR6pairkC j0QBcn492yUtpXY/d59HuK2+c1rvpeaWVDU3'
    clusterSettings: {
      clusterMode: 'activePassiveNoBgp'
    }
    domainNameLabelScope: 'NoReuse'
    enableTelemetry: false
    existingPrimaryPublicIPResourceId: puplicIpAddress.outputs.resourceId
    gatewayType: 'Vpn'
    location: location
    lock: {
      kind: 'None'
    }
    name: 'bytrcvgwp000'
    skuName: 'Basic'
    virtualNetworkResourceId: virtualNetworkResourceId
    vpnClientAddressPoolPrefix: '172.23.231.0/24'
    vpnGatewayGeneration: 'Generation2'
    vpnType: 'RouteBased'
  }
}

