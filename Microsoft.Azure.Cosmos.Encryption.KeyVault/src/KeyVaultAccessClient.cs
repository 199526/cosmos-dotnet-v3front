﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Encryption.KeyVault
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Documents;
    using Microsoft.IdentityModel.Clients.ActiveDirectory;
    using Newtonsoft.Json;

    /// <summary>
    /// Implementation of <see cref="IKeyVaultAccessClient"/> that uses the
    /// <see cref="KeyVaultAccessClient"/> client.
    /// </summary>
    internal sealed class KeyVaultAccessClient : IKeyVaultAccessClient
    {
        private readonly TimeSpan aadRetryInterval;
        private readonly int aadRetryCount;

        private readonly ClientAssertionCertificate clientAssertionCertificate;
        private readonly AsyncCache<Uri, IAADTokenProvider> aadTokenProviderByKeyUri;

        private readonly HttpClient httpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="KeyVaultAccessClient"/> class.
        /// </summary>
        /// <param name="clientId">AAD client id or service principle id.</param>
        /// <param name="certificate">Authorization Certificate to authorize with AAD.</param>
        internal KeyVaultAccessClient(
            string clientId,
            X509Certificate2 certificate,
            HttpClient httpClient,
            TimeSpan aadRetryInterval,
            int aadRetryCount)
        {
            this.clientAssertionCertificate = new ClientAssertionCertificate(clientId, certificate);
            this.aadTokenProviderByKeyUri = new AsyncCache<Uri, IAADTokenProvider>();
            this.httpClient = httpClient;

            this.aadRetryInterval = aadRetryInterval;
            this.aadRetryCount = aadRetryCount;
        }

        /// <summary>
        /// Unwrap the encrypted Key.
        /// Only supports encrypted bytes in base64 format.
        /// </summary>
        /// <param name="keyVaultKeyUri">Sample Format: https://{keyvault-name}.vault.azure.net/keys/{key-name}/{key-version}, the /{key-version} is optional.</param>
        /// <param name="bytesInBase64">encrypted bytes encoded to base64 string. </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result including KeyIdentifier and decrypted bytes in base64 string format, can be convert to bytes using Convert.FromBase64String().</returns>
        public async Task<KeyVaultUnwrapResult> UnwrapKeyAsync(
            string bytesInBase64,
            Uri keyVaultKeyUri,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return (KeyVaultUnwrapResult)await this.WrapOrUnWrapKeyAsync(
                keyVaultKeyUri: keyVaultKeyUri,
                bytesInBase64: bytesInBase64,
                shouldWrapKey: false,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Wrap the Key with latest Key version. 
        /// Only supports bytes in base64 format.
        /// </summary>
        /// <param name="bytesInBase64">bytes encoded to base64 string. E.g. Convert.ToBase64String(bytes) .</param>
        /// <param name="keyVaultKeyUri">Sample Format: https://{keyvault-name}.vault.azure.net/keys/{key-name}/{key-version}, the /{key-version} is optional.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result including KeyIdentifier and encrypted bytes in base64 string format.</returns>
        public async Task<KeyVaultWrapResult> WrapKeyAsync(
            string bytesInBase64,
            Uri keyVaultKeyUri,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return (KeyVaultWrapResult)await this.WrapOrUnWrapKeyAsync(
                keyVaultKeyUri: keyVaultKeyUri,
                bytesInBase64: bytesInBase64,
                shouldWrapKey: true,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Wrap/Unwrap the plain/encrypted Key.
        /// Currently supports encrypted bytes in base64 format.
        /// </summary>
        /// <param name="bytesInBase64">encrypted bytes encoded to base64 string. </param>
        /// <param name="keyVaultKeyUri">Sample Format: https://{keyvault-name}.vault.azure.net/keys/{key-name}/{key-version}, the /{key-version} is optional.</param>
        /// <param name="shouldWrapKey"> Choose Wrap or Unwrap.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result including Wrapped or Unwrapped bytes in Base64 string format & KeyIdentifier.</returns>
        private async Task<object> WrapOrUnWrapKeyAsync(
            Uri keyVaultKeyUri,
            string bytesInBase64,
            bool shouldWrapKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!KeyVaultAccessClient.ValidateKeyVaultKeyUrl(keyVaultKeyUri))
            {
                throw new KeyVaultAccessException(HttpStatusCode.BadRequest, KeyVaultErrorCode.InvalidKeyVaultKeyURI, "Invalid KeyVaultKeyURI");
            }

            if (!KeyVaultAccessClient.ValidateBase64Encoding(bytesInBase64))
            {
                throw new KeyVaultAccessException(HttpStatusCode.BadRequest, KeyVaultErrorCode.InvalidInputBytes, "The Input is not a valid base 64 string");
            }

            string accessToken = await this.GetAadAccessTokenAsync(keyVaultKeyUri, cancellationToken);

            using (HttpResponseMessage response = await this.InternalWrapUnwrapAsync(keyVaultKeyUri, bytesInBase64, accessToken, shouldWrapKey, cancellationToken))
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();
                jsonResponse = string.IsNullOrEmpty(jsonResponse) ? string.Empty : jsonResponse;

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    DefaultTrace.TraceInformation("WrapOrUnWrapKeyAsync: Receive HttpStatusCode {0}, KeyVaultErrorCode {1}, Errors: {2}. ", response.StatusCode, KeyVaultErrorCode.KeyVaultAuthenticationFailure, jsonResponse);
                    throw new KeyVaultAccessException(
                        response.StatusCode,
                        KeyVaultErrorCode.KeyVaultAuthenticationFailure,
                        jsonResponse);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    DefaultTrace.TraceInformation("WrapOrUnWrapKeyAsync: Receive HttpStatusCode {0}, KeyVaultErrorCode {1}, Errors: {2}. ", response.StatusCode, KeyVaultErrorCode.KeyVaultKeyNotFound, jsonResponse);
                    throw new KeyVaultAccessException(
                        response.StatusCode,
                        KeyVaultErrorCode.KeyVaultKeyNotFound,
                        jsonResponse);
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    DefaultTrace.TraceInformation("WrapOrUnWrapKeyAsync: Receive HttpStatusCode {0}, KeyVaultErrorCode {1}, Errors: {2}. ", response.StatusCode, KeyVaultErrorCode.KeyVaultWrapUnwrapFailure, jsonResponse);
                    throw new KeyVaultAccessException(
                        response.StatusCode,
                        KeyVaultErrorCode.KeyVaultWrapUnwrapFailure,
                        jsonResponse);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    DefaultTrace.TraceInformation("WrapOrUnWrapKeyAsync: Receive HttpStatusCode {0}, KeyVaultErrorCode {1}, Errors: {2}. ", response.StatusCode, KeyVaultErrorCode.InternalServerError, jsonResponse);
                    throw new KeyVaultAccessException(
                        response.StatusCode,
                        KeyVaultErrorCode.InternalServerError,
                        jsonResponse);
                }

                DefaultTrace.TraceInformation("WrapOrUnWrapKeyAsync succeed.");

                InternalWrapUnwrapResponse internalWrapUnwrapResponse = JsonConvert.DeserializeObject<InternalWrapUnwrapResponse>(jsonResponse);

                string responseBytesInBase64 = KeyVaultAccessClient.ConvertBase64UrlToBase64String(internalWrapUnwrapResponse.Value);
                Uri responseKeyVaultKeyUri = new Uri(internalWrapUnwrapResponse.Kid);

                if (shouldWrapKey)
                {
                    return new KeyVaultWrapResult(
                        wrappedKeyBytesInBase64: responseBytesInBase64,
                        keyVaultKeyUri: responseKeyVaultKeyUri);
                }
                else
                {
                    return new KeyVaultUnwrapResult(
                        unwrappedKeyBytesInBase64: responseBytesInBase64,
                        keyVaultKeyUri: responseKeyVaultKeyUri);
                }
            }
        }

        /// <summary>
        /// Helper Method for WrapOrUnWrapKeyAsync.
        /// </summary>
        private async Task<HttpResponseMessage> InternalWrapUnwrapAsync(
            Uri keyVaultKeyUri,
            string bytesInBase64,
            string accessToken,
            bool wrapKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string keyvaultUri = keyVaultKeyUri + (wrapKey ? KeyVaultConstants.WrapKeySegment : KeyVaultConstants.UnwrapKeySegment);

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, keyvaultUri + "?" + KeyVaultConstants.ApiVersionQueryParameters))
            {
                request.Headers.Add(HttpConstants.HttpHeaders.Accept, RuntimeConstants.MediaTypes.Json);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                InternalWrapUnwrapRequest keyVaultRequest = new InternalWrapUnwrapRequest
                {
                    Alg = KeyVaultConstants.RsaOaep,
                    Value = bytesInBase64.TrimEnd('=').Replace('+', '-').Replace('/', '_') // Format base 64 encoded string for http transfer
                };

                request.Content = new StringContent(
                    JsonConvert.SerializeObject(keyVaultRequest),
                    Encoding.UTF8,
                    RuntimeConstants.MediaTypes.Json);

                string correlationId = Guid.NewGuid().ToString();
                DefaultTrace.TraceInformation("InternalWrapUnwrapAsync: request correlationId {0}.", correlationId);

                // InternalWrapUnwrapAsync
                request.Headers.Add(
                    KeyVaultConstants.CorrelationId,
                    correlationId);

                try
                {
                    return await BackoffRetryUtility<HttpResponseMessage>.ExecuteAsync(
                                            () =>
                                            {
                                                return this.httpClient.SendAsync(request, cancellationToken);
                                            },
                                            new WebExceptionRetryPolicy(),
                                            cancellationToken);
                }
                catch (Exception ex)
                {
                    DefaultTrace.TraceInformation("InternalWrapUnwrapAsync: caught exception while trying to send http request: {0}.", ex.ToString());
                    throw new KeyVaultAccessException(
                        HttpStatusCode.ServiceUnavailable,
                        KeyVaultErrorCode.KeyVaultServiceUnavailable,
                        ex.ToString());
                }
            }
        }

        /// <summary>
        /// Obtain the AAD Token to be later used to access KeyVault.
        /// </summary>
        /// <returns>AAD Bearer Token. </returns>
        private async Task<string> GetAadAccessTokenAsync(
            Uri keyVaultKeyUri, 
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IAADTokenProvider aadTokenProvider = await this.aadTokenProviderByKeyUri.GetAsync(
                key: keyVaultKeyUri,
                obsoleteValue: null,
                singleValueInitFunc: async () =>
                {
                    (string aadLoginUrl, string keyVaultResourceEndpoint) = await this.InitializeLoginUrlAndResourceEndpointAsync(keyVaultKeyUri, cancellationToken);

                    return new AADTokenProvider(
                        aadLoginUrl,
                        keyVaultResourceEndpoint,
                        this.clientAssertionCertificate,
                        this.aadRetryInterval,
                        this.aadRetryCount);
                },
                cancellationToken: cancellationToken);

            try
            {
                return await aadTokenProvider.GetAccessTokenAsync(cancellationToken);
            }
            catch (AdalException ex)
            {
                DefaultTrace.TraceInformation("GetAadAccessTokenAsync: caught exception while trying to acquire token: {0}.", ex.ToString());
                throw new KeyVaultAccessException(HttpStatusCode.ServiceUnavailable, KeyVaultErrorCode.AadServiceUnavailable, ex.ToString());
            }
        }

        /// <summary>
        /// Initialize the LoginUrl and ResourceEndpoint.
        /// The SDK will send GET request to the key vault url in order to retrieve the AAD authority and resource.
        /// </summary>
        private async Task<(string aadLoginUrl, string keyVaultResourceEndpoint)> InitializeLoginUrlAndResourceEndpointAsync(Uri keyVaultKeyUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, keyVaultKeyUri + "?" + KeyVaultConstants.ApiVersionQueryParameters))
            {
                string correlationId = Guid.NewGuid().ToString();
                DefaultTrace.TraceInformation("InitializeLoginUrlAndResourceEndpointAsync: request correlationId {0}.", correlationId);

                request.Headers.Add(
                    KeyVaultConstants.CorrelationId,
                    correlationId);

                try
                {
                    using (HttpResponseMessage response = await BackoffRetryUtility<HttpResponseMessage>.ExecuteAsync(
                                            () =>
                                            {
                                                return this.httpClient.SendAsync(request, cancellationToken);
                                            },
                                            new WebExceptionRetryPolicy(),
                                            cancellationToken))
                    {

                        if (response.StatusCode != HttpStatusCode.Unauthorized)
                        {
                            DefaultTrace.TraceInformation("InitializeLoginUrlAndResourceEndpointAsync: Receive HttpStatusCode {0}, KeyVaultErrorCode {1}, The Status Code for the first try should be Unauthorized.", response.StatusCode, KeyVaultErrorCode.AadClientCredentialsGrantFailure);
                            throw new KeyVaultAccessException(
                                response.StatusCode,
                                KeyVaultErrorCode.AadClientCredentialsGrantFailure,
                                "The Status Code for the first try should be Unauthorized.");
                        }

                        // authenticationHeaderValue Sample:
                        // Bearer authorization="https://login.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47", resource="https://vault.azure.net"
                        AuthenticationHeaderValue authenticationHeaderValue = response.Headers.WwwAuthenticate.Single();

                        string[] source = authenticationHeaderValue.Parameter.Split('=', ',');

                        // Sample aadLoginUrl: https://login.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47
                        string aadLoginUrl = source.ElementAt(1).Trim('"');

                        // Sample keyVaultResourceEndpoint: https://vault.azure.net
                        string keyVaultResourceEndpoint = source.ElementAt(3).Trim('"');

                        return (aadLoginUrl, keyVaultResourceEndpoint);
                    }
                }
                catch (Exception ex)
                {
                    DefaultTrace.TraceInformation("InitializeLoginUrlAndResourceEndpointAsync: caught exception while trying to send http request: {0}.", ex.ToString());
                    throw new KeyVaultAccessException(
                        HttpStatusCode.ServiceUnavailable,
                        KeyVaultErrorCode.KeyVaultServiceUnavailable,
                        ex.ToString());
                }
            }
        }

        /// <summary>
        /// Convert base64 url to base64 string.
        /// </summary>
        private static string ConvertBase64UrlToBase64String(string str)
        {
            string base64EncodedValue = str.Replace('-', '+').Replace('_', '/');

            int count = 3 - ((str.Length + 3) % 4);

            for (int ich = 0; ich < count; ich++)
            {
                base64EncodedValue += "=";
            }

            return base64EncodedValue;
        }

        private static bool ValidateKeyVaultKeyUrl(Uri keyVaultKeyUri)
        {
            string[] segments = keyVaultKeyUri.Segments;
            return (segments.Length == 3 || segments.Length == 4) &&
                string.Equals(segments[1], KeyVaultConstants.KeysSegment, StringComparison.InvariantCultureIgnoreCase);
        }

        private static bool ValidateBase64Encoding(string bytesInBase64)
        {
            if (bytesInBase64 == null || bytesInBase64.Length % 4 != 0)
            {
                return false;
            }

            try
            {
                Convert.FromBase64String(bytesInBase64);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static Uri GetKeyVaultKeyUrlWithNoKeyVersion(Uri keyVaultKeyUri)
        {
            string[] segments = keyVaultKeyUri.Segments;
            if (segments.Length == 3)
            {
                return keyVaultKeyUri;
            }

            string[] newSegments = keyVaultKeyUri.Segments.Take(keyVaultKeyUri.Segments.Length - 1).ToArray();
            newSegments[newSegments.Length - 1] = newSegments[newSegments.Length - 1].TrimEnd('/');

            UriBuilder uriBuilder = new UriBuilder(keyVaultKeyUri);
            uriBuilder.Path = string.Concat(newSegments);
            return uriBuilder.Uri;
        }
    }
}
