using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace cmc_notifier
{
    public static class CMCNewListingsNotifier
    {
        [FunctionName("CMCNewListingsNotifier")]
        public static async Task Run([TimerTrigger("0 */10 * * * *")]TimerInfo myTimer, ILogger log)
        {
            var result = (await GetLatestListingsAsync());
            var tokensToSend = result.Data.Where(x => x.Date_Added > DateTime.UtcNow.AddMinutes(-120)).ToList();
            if (tokensToSend.Any())
            {
                await SendSMSMessagesAsync(tokensToSend, log);
            }
            else
            {
                log.LogInformation("No new tokens to be notified of.");
            }
        }

        public static async Task SendSMSMessagesAsync(List<Cryptocurrency> tokensToSend, ILogger log)
        {
            var accountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID");
            var authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN");
            TwilioClient.Init(accountSid, authToken);

            var recipients = Environment.GetEnvironmentVariable("SMS_RECEIVERS").Split(';').ToList();

            var previousNotifications = await GetPreviousNotificationsAsync();

            if (previousNotifications.Any())
            {
                tokensToSend = tokensToSend.Where(x => previousNotifications.All(y => y.Slug != x.Slug)).ToList();
            }

            if(!tokensToSend.Any())
            {
                log.LogInformation("Notifications have been sent for all new tokens");
                return;
            }

            var blobsToSave = previousNotifications.Where(x => x.Date_Added > DateTime.UtcNow.AddMinutes(-200)).Concat(tokensToSend.Select(y => new BlobToken
            {
                Slug = y.Slug,
                Date_Added = y.Date_Added
            })).ToList();

            await WriteNotificationsToBlobAsync(blobsToSave);

            foreach (var token in tokensToSend)
            {
                var message = BuildNotificationMessage(token);
                foreach(var recipient in recipients)
                {
                    var messageResult = await MessageResource.CreateAsync(
                        body: message,
                        from: new Twilio.Types.PhoneNumber($"{Environment.GetEnvironmentVariable("SMS_SENDER")}"),
                        to: new Twilio.Types.PhoneNumber($"{recipient}")
                    );

                    log.LogInformation($"SMS message processed for token {token.Slug} & recipient {recipient} with status {messageResult.Status.ToString()}");
                    if (messageResult.ErrorMessage != null)
                    {
                        log.LogError($"SMS message failed with status code: {messageResult.ErrorCode} - {messageResult.ErrorMessage}");
                    }
                }
                
            }
        }

        public static string BuildNotificationMessage(Cryptocurrency token)
        {
            var message = string.Empty;
            message += "Hi! A new token has recently been added to CoinMarketCap.\n";
            message += $"Token: {token.Name} ({token.Symbol})\n";
            if (token.Platform != null)
            {
                message += $"Platform: {token.Platform.Name}\n";
            }
            else
            {
                message += "Platform: Not specified\n";
            }

            message += $"Price: ${token.Quote.USD.Price}\n";
            message += $"24h Change: {token.Quote.USD.Percent_Change_24h}%\n";
            message += $"Visit www.coinmarketcap.com/currencies/{token.Slug} for more information.";
            return message;
        }

        public static async Task<ListingLatestResponse> GetLatestListingsAsync()
        {
            string cmcApiKey = Environment.GetEnvironmentVariable("CMC_APIKEY");
            var client = new HttpClient { BaseAddress = new Uri("https://pro-api.coinmarketcap.com") };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("X-CMC_PRO_API_KEY", cmcApiKey);

            var response = await client.GetAsync("/v1/cryptocurrency/listings/latest?sort=date_added&sort_dir=desc&limit=30");

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsAsync<ListingLatestResponse>();
                return result;
            }

            throw new Exception($"Failed to call CoinMarketCap API: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        public static async Task WriteNotificationsToBlobAsync(List<BlobToken> blobs)
        {
            var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var containerName = Environment.GetEnvironmentVariable("AZURE_BLOB_CONTAINER_NAME");

            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(containerName);

            var json = JsonConvert.SerializeObject(blobs);

            CloudBlockBlob blob = container.GetBlockBlobReference("notifications");
            blob.Properties.ContentType = "application/json";

            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                await blob.UploadFromStreamAsync(stream);
            }
        }

        public static async Task<List<BlobToken>> GetPreviousNotificationsAsync()
        {
            string storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var containerName = Environment.GetEnvironmentVariable("AZURE_BLOB_CONTAINER_NAME");

            // Retrieve the blob container reference
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(containerName);

            // Retrieve the blob content as a byte array
            CloudBlockBlob blob = container.GetBlockBlobReference("notifications");
            if (await blob.ExistsAsync())
            {
                byte[] data = new byte[blob.Properties.Length];
                await blob.DownloadToByteArrayAsync(data, 0);

                // Convert the byte array to a string and deserialize the JSON data
                string jsonData = System.Text.Encoding.UTF8.GetString(data);
                return JsonConvert.DeserializeObject<List<BlobToken>>(jsonData);
            }
            return new List<BlobToken>();
        }

        public class BlobToken
        {
            public DateTime Date_Added { get; set; }
            public string Slug { get; set; }
        }

        public class ListingLatestResponse
        {
            public List<Cryptocurrency> Data { get; set; }
        }

        public class Cryptocurrency
        {
            public string Name { get; set; }
            public string Symbol { get; set; }
            public string Slug { get; set; }
            public DateTime Date_Added { get; set; }
            public Quote Quote { get; set; }
            public Platform Platform { get; set; }
        }

        public class Platform
        {
            public string Name { get; set; }
        }

        public class Quote
        {
            public USDQuote USD { get; set; }
        }

        public class USDQuote
        {
            public decimal Price { get; set; }
            public decimal Percent_Change_24h { get; set; }
            public decimal Percent_Change_1h { get; set; }
        }
    }
}
