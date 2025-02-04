using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace ServiceBus_Queue_Sender
{
    class Program
    {
        private const string sessionId = "42";
        private static readonly string ServiceBusConnectionString = Config.ServiceBusConnectionString;
        private static readonly string QueueName = Config.QueueName;

        static async Task Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: ServiceBus_Queue_Sender <messageFileName> [queueName]");
                    return;
                }

                string messageFileName = args[0];
                string queueName = args.Length > 1 ? args[1] : QueueName;
                
                Console.WriteLine($"using Queue: {queueName}");

                string filePath = $@"messages\{messageFileName}";
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"File not found: {filePath}");
                    return;
                }

                string messageContent = await File.ReadAllTextAsync(filePath);
                await SendMessageAsync(queueName, messageContent, sessionId);
                Console.WriteLine($"{DateTime.Now}: Message {messageFileName} sent successfully to Queue {queueName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message: {ex.Message}");
            }
        }

        private static async Task SendMessageAsync(string queueName, string messageContent, string sessionId)
        {
            await using var client = new ServiceBusClient(ServiceBusConnectionString);
            ServiceBusSender sender = client.CreateSender(queueName);
            
            try
            {
                ServiceBusMessage message = new ServiceBusMessage(messageContent)
                {
                    SessionId = sessionId
                };
                await sender.SendMessageAsync(message);
            }
            finally
            {
                await sender.DisposeAsync();
            }
        }
    }
}
