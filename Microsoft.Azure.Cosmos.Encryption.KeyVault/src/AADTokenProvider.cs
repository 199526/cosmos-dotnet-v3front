﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Encryption.KeyVault
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Documents;
    using Microsoft.IdentityModel.Clients.ActiveDirectory;

    /// <summary>
    /// Default implementation of <see cref="IAADTokenProvider"/>.
    /// </summary>
    internal sealed class AADTokenProvider : IAADTokenProvider
    {
        private readonly string defaultAuthority; // AAD Login URI
        private readonly string defaultResource;  // Key Vault Resource End Point

        private readonly ClientAssertionCertificate clientAssertionCertificate;

        private readonly TimeSpan retryInterval;
        private readonly int retryCount;

        private readonly TokenCache tokenCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="AADTokenProvider"/> class.
        /// </summary>
        public AADTokenProvider(
            string defaultAuthority,
            string defaultResource,
            ClientAssertionCertificate clientAssertionCertificate,
            TimeSpan retryInterval,
            int retryCount)
        {
            this.defaultAuthority = defaultAuthority;
            this.defaultResource = defaultResource;
            this.clientAssertionCertificate = clientAssertionCertificate;

            this.retryInterval = retryInterval;
            this.retryCount = retryCount;

            // todo: should this be shared across AADTokenProvider instances?
            this.tokenCache = new TokenCache();
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AuthenticationContext context = new AuthenticationContext(
                authority: this.defaultAuthority,
                validateAuthority: true,
                tokenCache: this.tokenCache);

            AuthenticationResult result = await BackoffRetryUtility<AuthenticationResult>.ExecuteAsync(
                                                () =>
                                                {
                                                    return context.AcquireTokenAsync(this.defaultResource, this.clientAssertionCertificate);
                                                },
                                                new AADExceptionRetryPolicy(this.retryInterval, this.retryCount),
                                                cancellationToken);

            return result.AccessToken;
        }
    }
}