﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Encryption.KeyVault
{
    using System;
    using System.Net.Http;
    using System.Security.Cryptography.X509Certificates;

    internal sealed class KeyVaultAccessClientFactory : IKeyVaultAccessClientFactory
    {
        private readonly object singletonLock;
        private HttpClient httpClient;

        public KeyVaultAccessClientFactory()
        {
            this.singletonLock = new object();
        }

        public IKeyVaultAccessClient CreateKeyVaultAccessClient(
            string clientId,
            X509Certificate2 certificate,
            Uri defaultKeyVaultKeyUri = null,
            int aadRetryIntervalInSeconds = KeyVaultConstants.DefaultAadRetryIntervalInSeconds, 
            int aadRetryCount = KeyVaultConstants.DefaultAadRetryCount)
        {
            if (this.httpClient == null)
            {
                lock (this.singletonLock)
                {
                    if (this.httpClient == null)
                    {
                        this.httpClient = new HttpClient
                        {
                            Timeout = TimeSpan.FromSeconds(KeyVaultConstants.DefaultHttpClientTimeoutInSeconds)
                        };
                    }   
                }
            }

            return new KeyVaultAccessClient(
                clientId: clientId,
                certificate: certificate,
                httpClient: this.httpClient,
                aadRetryInterval: TimeSpan.FromSeconds(aadRetryIntervalInSeconds),
                aadRetryCount: aadRetryCount);
        }

        public void Dispose()
        {
            this.httpClient?.Dispose();
        }
    }
}
