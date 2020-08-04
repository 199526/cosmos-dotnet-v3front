//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;
    using Newtonsoft.Json;

    // Marking it as non-sealed in order to unit test it using Moq framework
    internal class GatewayStoreModel : IStoreModel, IDisposable
    {
        private readonly GlobalEndpointManager endpointManager;
        private readonly ISessionContainer sessionContainer;
        private readonly ConsistencyLevel defaultConsistencyLevel;

        private GatewayStoreClient gatewayStoreClient;

        public GatewayStoreModel(
            GlobalEndpointManager endpointManager,
            ISessionContainer sessionContainer,
            ConsistencyLevel defaultConsistencyLevel,
            GatewayStoreClient gatewayStoreClient)
        {
            this.endpointManager = endpointManager;
            this.sessionContainer = sessionContainer;
            this.defaultConsistencyLevel = defaultConsistencyLevel;

            this.gatewayStoreClient = gatewayStoreClient;
        }

        public virtual async Task<DocumentServiceResponse> ProcessMessageAsync(DocumentServiceRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            this.ApplySessionToken(request);

            DocumentServiceResponse response;
            try
            {
                Uri physicalAddress = GatewayStoreClient.IsFeedRequest(request.OperationType) ? this.GetFeedUri(request) : this.GetEntityUri(request);
                response = await this.gatewayStoreClient.InvokeAsync(request, request.ResourceType, physicalAddress, cancellationToken);
            }
            catch (DocumentClientException exception)
            {
                if ((!ReplicatedResourceClient.IsMasterResource(request.ResourceType)) &&
                    (exception.StatusCode == HttpStatusCode.PreconditionFailed || exception.StatusCode == HttpStatusCode.Conflict
                    || (exception.StatusCode == HttpStatusCode.NotFound && exception.GetSubStatus() != SubStatusCodes.ReadSessionNotAvailable)))
                {
                    this.CaptureSessionToken(exception.StatusCode, exception.GetSubStatus(), request, exception.Headers);
                }

                throw;
            }

            this.CaptureSessionToken(response.StatusCode, response.SubStatusCode, request, response.Headers);
            return response;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void CaptureSessionToken(
            HttpStatusCode? statusCode,
            SubStatusCodes subStatusCode,
            DocumentServiceRequest request,
            INameValueCollection responseHeaders)
        {
            // Exceptionless can try to capture session token from CompleteResponse
            if (request.IsValidStatusCodeForExceptionlessRetry((int)statusCode, subStatusCode))
            {
                // Not capturing on master resources
                if (ReplicatedResourceClient.IsMasterResource(request.ResourceType))
                {
                    return;
                }

                // Only capturing on 409, 412, 404 && !1002
                if (statusCode != HttpStatusCode.PreconditionFailed
                    && statusCode != HttpStatusCode.Conflict
                        && (statusCode != HttpStatusCode.NotFound || subStatusCode == SubStatusCodes.ReadSessionNotAvailable))
                {
                    return;
                }
            }

            if (request.ResourceType == ResourceType.Collection && request.OperationType == OperationType.Delete)
            {
                string resourceId;

                if (request.IsNameBased)
                {
                    resourceId = responseHeaders[HttpConstants.HttpHeaders.OwnerId];
                }
                else
                {
                    resourceId = request.ResourceId;
                }

                this.sessionContainer.ClearTokenByResourceId(resourceId);
            }
            else
            {
                this.sessionContainer.SetSessionToken(request, responseHeaders);
            }
        }

        private void ApplySessionToken(DocumentServiceRequest request)
        {
            if (request.Headers != null &&
                !string.IsNullOrEmpty(request.Headers[HttpConstants.HttpHeaders.SessionToken]))
            {
                if (ReplicatedResourceClient.IsMasterResource(request.ResourceType))
                {
                    request.Headers.Remove(HttpConstants.HttpHeaders.SessionToken);
                }
                return; //User is explicitly controlling the session.
            }

            if (request.Headers != null &&
                 request.OperationType == OperationType.QueryPlan)
            {
                {
                    string isPlanOnlyString = request.Headers[HttpConstants.HttpHeaders.IsQueryPlanRequest];
                    bool isPlanOnly = false;
                    if (bool.TryParse(isPlanOnlyString, out isPlanOnly) && isPlanOnly)
                    {
                        return; // for query plan session token is not needed
                    }
                }
            }

            string requestConsistencyLevel = request.Headers[HttpConstants.HttpHeaders.ConsistencyLevel];

            bool sessionConsistency =
                this.defaultConsistencyLevel == ConsistencyLevel.Session ||
                (!string.IsNullOrEmpty(requestConsistencyLevel)
                    && string.Equals(requestConsistencyLevel, ConsistencyLevel.Session.ToString(), StringComparison.OrdinalIgnoreCase));

            if (!sessionConsistency || ReplicatedResourceClient.IsMasterResource(request.ResourceType))
            {
                return; // Only apply the session token in case of session consistency and when resource is not a master resource
            }

            //Apply the ambient session.
            string sessionToken = this.sessionContainer.ResolveGlobalSessionToken(request);

            if (!string.IsNullOrEmpty(sessionToken))
            {
                request.Headers[HttpConstants.HttpHeaders.SessionToken] = sessionToken;
            }
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.gatewayStoreClient != null)
                {
                    try
                    {
                        this.gatewayStoreClient.Dispose();
                    }
                    catch (Exception exception)
                    {
                        DefaultTrace.TraceWarning("Exception {0} thrown during dispose of HttpClient, this could happen if there are inflight request during the dispose of client",
                            exception);
                    }

                    this.gatewayStoreClient = null;
                }
            }
        }

        private Uri GetEntityUri(DocumentServiceRequest entity)
        {
            string contentLocation = entity.Headers[HttpConstants.HttpHeaders.ContentLocation];

            if (!string.IsNullOrEmpty(contentLocation))
            {
                return new Uri(this.endpointManager.ResolveServiceEndpoint(entity), new Uri(contentLocation).AbsolutePath);
            }

            return new Uri(this.endpointManager.ResolveServiceEndpoint(entity), PathsHelper.GeneratePath(entity.ResourceType, entity, false));
        }

        private Uri GetFeedUri(DocumentServiceRequest request)
        {
            return new Uri(this.endpointManager.ResolveServiceEndpoint(request), PathsHelper.GeneratePath(request.ResourceType, request, true));
        }
    }
}
