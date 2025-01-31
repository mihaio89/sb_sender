using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using ModernCommerce.Connector.RestAPIProcessor.Brokers.Messages;
using ModernCommerce.Connector.RestAPIProcessor.Models;
using ModernCommerce.Connector.RestAPIProcessor.Brokers.Storages;
using ModernCommerce.Connector.PartnerContracts.RMCA.BillableItemCreate;
using ModernCommerce.Connector.Models.Commercial;
using ModernCommerce.Connector.Brokers;
using ModernCommerce.Connector.RestAPIProcessor.Models.RMCACreateBillableItems;
using ModernCommerce.Connector.TokenCore;
using ModernCommerce.Connector.Constants;
using ModernCommerce.Connector.Models.Commercial.Kusto;
using ModernCommerce.Connector.Extensions;

namespace ModernCommerce.Connector.RestAPIProcessor.Services.Foundation
{
    public class RMCACreateBillableItemsProcessor : RestAPIProcessor
    {
        public RMCACreateBillableItemsProcessorRequest createBitProcessorRequest;
        private readonly IConfiguration configuration;
        private readonly IMessageBroker MessageBroker;
        private readonly IStorageBroker StorageBroker;
        private readonly INCECommercialLogger logger;
        private readonly RMCACreateBillableItemsConfig createBitConfig;
        private bool hasNoDependentBillableItems;
        private bool isDependentBillableRequest;

        public RMCACreateBillableItemsProcessor(
            RestAPIProcessorRequest restAPIProcessorRequest,
            IMessageBroker messageBroker, 
            IConfiguration configuration, 
            IStorageBroker storageBroker,
            INCECommercialLogger logger,
            ITokenManager tokenManager) : base(tokenManager)
        {
            this.createBitProcessorRequest = JsonConvert.DeserializeObject<RMCACreateBillableItemsProcessorRequest>(restAPIProcessorRequest.RequestBody);
            this.configuration = configuration;
            this.MessageBroker = messageBroker;
            this.StorageBroker = storageBroker;
            this.logger = logger;
            this.createBitConfig = configuration.GetSection(configuration["RestAPIProcessorsListAppConfigKey"]).Get<RMCACreateBillableItemsConfig>();
            this.hasNoDependentBillableItems = false;
            this.isDependentBillableRequest = false;
        }

        public override Task PreProcess()
        {
            logger.LogInformation("Bit Creation request received for document ID :" + createBitProcessorRequest.DocumentId);
            string correlationId = Guid.NewGuid().ToString("N");
            // For Daily Invoicing and ISV
            hasNoDependentBillableItems = createBitProcessorRequest.DependentBillableItems == null && createBitProcessorRequest.DocumentBitsTracker == null ? true : false;

            string payload = CreatePayload(correlationId);
            RestAPIRequest restApiRequest = CreateRestAPIRequest(payload, correlationId);
            restApiRequests.Add(restApiRequest);
            return Task.CompletedTask;
        }

        private string CreatePayload(string correlationId)
        {
            JObject outputJson = new JObject();
            outputJson.TryAdd("i_ext_ref32", correlationId);

            if (createBitProcessorRequest.BillableItems != null)
            {
                foreach (var item in createBitProcessorRequest.BillableItems)
                {
                    MergeArray(outputJson, item, "it_bit_it");
                    MergeArray(outputJson, item, "it_bit_tx");
                    MergeArray(outputJson, item, "it_bit_py");
                }
            }
            if (createBitProcessorRequest.DependentBillableItems != null && createBitProcessorRequest.BillableItems == null)
            {
                isDependentBillableRequest = true;
                BillableItems dependentBillableItems = createBitProcessorRequest.DependentBillableItems;
                MergeArray(outputJson, dependentBillableItems, "it_bit_it");
                MergeArray(outputJson, dependentBillableItems, "it_bit_tx");
                MergeArray(outputJson, dependentBillableItems, "it_bit_py");
            }
            return outputJson.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void MergeArray(JObject output, object source, string propertyName)
        {
            var obj = JObject.FromObject(source)[propertyName];
            if (obj != null && obj.Type == JTokenType.Array)
            {
                JArray newArray = (JArray)obj;
                if (newArray != null && newArray.HasValues)
                {
                    if (!output.TryGetValue(propertyName, out var existingArray))
                    {
                        output.Add(propertyName, new JArray());
                        existingArray = output[propertyName];
                    }
                    ((JArray)existingArray).Merge(newArray);
                }
            }
        }

        private RestAPIRequest CreateRestAPIRequest(string payload, string correlationId)
        {
            Dictionary<string, string> additionalHeaders = new Dictionary<string, string>
            {
                { "Ocp-Apim-Subscription-Key", createBitConfig.SubscriptionKey },
                { "X-CorrelationId", correlationId }
            };
            RestAPIRequest restApiRequest = new RestAPIRequest
            {
                Uri = createBitConfig.RestAPIRequest.Uri,
                Body = payload,
                Method = createBitConfig.RestAPIRequest.Method,
                TokenConfig = createBitConfig.RestAPIRequest.TokenConfig,
                AdditionalHeaders = additionalHeaders
            };
            return restApiRequest;
        }

        public override async Task PostProcess()
        {
            if (restApiProcessingResult.SuccessRequestsResponses.Count > 0)
            {
                // Increase cosmos count only for invoice with dependent billable items
                if (!isDependentBillableRequest && !hasNoDependentBillableItems)
                {
                    createBitProcessorRequest.DocumentBitsTracker.HasBeenSentToRMCA = true;
                    await StorageBroker.UpsertDocumentBitsTracker(createBitProcessorRequest.DocumentBitsTracker);
                    if (await StorageBroker.AreAllBillableItemsSentToSAP(createBitProcessorRequest.DocumentBitsTracker))
                    {
                        await SendDependentBillableRequestToProcessor(createBitProcessorRequest);
                    }
                }
                if (isDependentBillableRequest || hasNoDependentBillableItems)
                {
                    // Create log based on document header bit creation response
                    if (restApiProcessingResult.SuccessRequestsResponses.Count > 0)
                    {
                        await LogCreateBitAPIResults(restApiProcessingResult);
                    }
                }
            }
            if (restApiProcessingResult.FailedRequestsResponses.Count > 0)
            {
                await HandleFailedRequest(restApiProcessingResult.FailedRequestsResponses);
            }
        }

        private async Task LogCreateBitAPIResults(RestAPIProcessingResult restApiProcessingResult)
        {
            //SuccessRequestsResponses list size will be always 1 since we are sending one sap request at a time
            // This one sap request can have max 500 billable items
            foreach (RestAPIRequestResponse reqRes in restApiProcessingResult.SuccessRequestsResponses)
            {
                var responseContent = await reqRes.Response.Content.ReadAsStringAsync();
                MD01Return MD01responseContent = JsonConvert.DeserializeObject<MD01Return>(responseContent);
                var documentStatus = GetStatus(MD01responseContent.ET_RETURN);
                var responseToLog = new RestAPIProcessingResult.RestAPIRequestResponse
                {
                    Request = reqRes.Request,
                    Response = reqRes.Response,
                    ResponseObject = MD01responseContent,
                    DocumentStatus = documentStatus,
                    TimeTaken = reqRes.TimeTaken
                };
                if (documentStatus != "Success") // error
                {
                    LogResult(responseToLog, "Bits creation failed");
                }
                else // success
                {
                    LogResult(responseToLog, "Bits creation successful");
                }
            }
        }

        private void LogResult(RestAPIProcessingResult.RestAPIRequestResponse response, string logMessage)
        {
            logger.LogSnapShotData(new CommercialDataExplorerEvent()
            {
                DocumentCategory = createBitProcessorRequest.DocumentCategory,
                DocumentId = createBitProcessorRequest.DocumentId,
                DocumentProcessor = "CreateBillableItems",
                DocumentStatus = response.DocumentStatus,
                EventProperties = new { Request = createBitProcessorRequest, ETReturn = response.ResponseObject, TimeTakeninMs = response.TimeTaken },
            });
            logger.LogInformation($"{logMessage} for document id: {createBitProcessorRequest.DocumentId}, BillableItemsCount: {createBitProcessorRequest.BillableItemsCount}");
        }

        private async Task SendDependentBillableRequestToProcessor(RMCACreateBillableItemsProcessorRequest createBitProcessorRequest)
        {
            // send the done bits
            logger.LogInformation("Sending DependentBillable request to processor for documentId: " + createBitProcessorRequest.DocumentId);
            RMCACreateBillableItemsProcessorRequest doneBitRequest = new RMCACreateBillableItemsProcessorRequest()
            {
                BillableItems = null,
                CorrelationId = createBitProcessorRequest.CorrelationId,
                DependentBillableItems = createBitProcessorRequest.DependentBillableItems,
                DocumentId = createBitProcessorRequest.DocumentId,
                DocumentCategory = createBitProcessorRequest.DocumentCategory
            };
            RestAPIProcessorRequest dependentBitRestApiRequest = new RestAPIProcessorRequest()
            {
                RequestBody = JsonConvert.SerializeObject(doneBitRequest),
                RequestType = RestProcessors.RMCACreateBillableItems
            };
            string sessionId = FinancialHelpers.GenerateHashValue(createBitProcessorRequest.DocumentId, 100).ToString();
            await MessageBroker.SendToRestAPIRequestQueue(dependentBitRestApiRequest, 0, "createbillableitems", sessionId);
        }

        public static string GetStatus(ET_RETURN[] et)
        {
            switch (true)
            {
                case bool hasDuplicate when
                    et.Any(element =>
                        (element.ID == "FKKBIX2" && (element.NUMBER == 7 || element.NUMBER == 4)) ||
                        (element.ID == "ZCONS_MSG" && element.NUMBER == 616)):
                    return "Duplicate";
                case bool hasError when et.Any(element => element.TYPE == "E" || element.TYPE == "A" || element.TYPE == "X"):
                    return "Failed";
                default:
                    return "Success";
            }
        }

        private async Task HandleFailedRequest(List<RestAPIRequestResponse> failedRequestsResponses)
        {
            foreach (RestAPIRequestResponse requestResponse in failedRequestsResponses)
            {
                // check if it has retry after
                double retryAfter = 0;
                if (requestResponse.Response.Headers.TryGetValues("Retry-After", out IEnumerable<string> values))
                {
                    retryAfter = CalculateRetryAfter(values);
                }

                if (retryAfter > 0)
                {
                    RestAPIProcessorRequest restApiRequest = new RestAPIProcessorRequest()
                    {
                        RequestBody = requestResponse.Request.Body,
                        RequestType = RestProcessors.RMCACreateBillableItems
                    };
                    string sessionId = FinancialHelpers.GenerateHashValue(createBitProcessorRequest.DocumentId, 100).ToString();
                    await MessageBroker.SendToRestAPIRequestQueue(restApiRequest, retryAfter, "createbillableitems", sessionId);
                    logger.LogSnapShotData(new CommercialDataExplorerEvent()
                    {
                        DocumentCategory = createBitProcessorRequest.DocumentCategory,
                        DocumentId = createBitProcessorRequest.DocumentId,
                        DocumentProcessor = "CreateBillableItems",
                        DocumentStatus = "Retried"
                    });
                }
                else
                {
                    var responseContent = await requestResponse?.Response?.Content?.ReadAsStringAsync();
                    var errorMessage = $"Error processing Create Billable Items for DocumentId: {createBitProcessorRequest.DocumentId}, StatusCode: {requestResponse.Response.StatusCode}. Response: {responseContent

}";
                    logger.LogError(errorMessage);
                }
            }
        }

        private static double CalculateRetryAfter(IEnumerable<string> values)
        {
            return values != null && values.Count() > 0 ? Convert.ToDouble(values.First()) : 0;
        }
    }
}