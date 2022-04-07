using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Keda.CosmosDb.Scaler.Demo.Shared;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keda.CosmosDb.Scaler.Demo.OrderProcessor
{
    internal sealed class Worker : BackgroundService
    {
        private readonly CosmosDbConfig _cosmosDbConfig;
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient;
        private readonly Uri _apiUri;

        private ChangeFeedProcessor _processor;

        public Worker(IConfiguration configuration, ILogger<Worker> logger)
        {
            _ = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _ = logger ?? throw new ArgumentNullException(nameof(logger));

            _cosmosDbConfig = CosmosDbConfig.Create(configuration);
            _logger = logger;
            _httpClient = new HttpClient();
            _apiUri = new Uri(configuration["HttpApplicationApiUrl"]);
        }

        public override async Task StartAsync(CancellationToken stoppingToken)
        {
            Database leaseDatabase = await new CosmosClient(_cosmosDbConfig.LeaseConnection)
                .CreateDatabaseIfNotExistsAsync(_cosmosDbConfig.LeaseDatabaseId);

            Container leaseContainer = await leaseDatabase
                .CreateContainerIfNotExistsAsync(
                    new ContainerProperties(_cosmosDbConfig.LeaseContainerId, partitionKeyPath: "/id"),
                    throughput: 400);

            // Change feed processor instance name should be unique for each container application.
            string instanceName = $"Instance-{Dns.GetHostName()}";

            _processor = new CosmosClient(_cosmosDbConfig.Connection)
                .GetContainer(_cosmosDbConfig.DatabaseId, _cosmosDbConfig.ContainerId)
                .GetChangeFeedProcessorBuilder<Order>(_cosmosDbConfig.ProcessorName, ProcessOrdersAsync)
                .WithInstanceName(instanceName)
                .WithLeaseContainer(leaseContainer)
                .Build();

            await _processor.StartAsync();
            _logger.LogInformation($"Started change feed processor instance {instanceName}");
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            await _processor.StopAsync();
            _logger.LogInformation("Stopped change feed processor");

            await base.StopAsync(stoppingToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return Task.CompletedTask;
        }

        private async Task ProcessOrdersAsync(IReadOnlyCollection<Order> orders, CancellationToken cancellationToken)
        {
            _logger.LogInformation(orders.Count + " orders received");

            foreach (Order order in orders)
            {
                _logger.LogInformation($"Processing order {order.Id} - {order.Amount} unit(s) of {order.Article} bought by {order.Customer.FirstName} {order.Customer.LastName}");

                NameValueCollection query = System.Web.HttpUtility.ParseQueryString(string.Empty);
                query.Add("quantity", $"{order.Amount}");
                query.Add("price", $"{(10 - order.Amount) * 2}"); // Article prices are not stored in container.
                query.Add("customer_name", $"{order.Customer.FirstName} {order.Customer.LastName}");
                query.Add("article_name", order.Article);

                Uri requestUri = new UriBuilder(_apiUri) { Query = query.ToString() }.Uri;
                _logger.LogInformation($"Sending request: {requestUri}.");

                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(requestUri);
                    response.EnsureSuccessStatusCode();
                    string content = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation($"Received response: {content}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Request failed with exception: {ex.Message}");
                    throw;
                }
            }
        }
    }
}
