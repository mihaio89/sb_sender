using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace ServiceBus_Queue_Sender;

class Program
{
    // Connection string to the Service Bus namespace
private static readonly string ServiceBusConnectionString = Config.ServiceBusConnectionString;
private static readonly string QueueName = Config.QueueName;

    static async Task Main(string[] args)
    {
        try
        {
            // Example SessionId (can be dynamic)
            string sessionId = "42";

            // Send a message to the Service Bus queue with a SessionId
            string filePath = @"messages\consumer1.json";
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                return;
            }

            string messageContent = await File.ReadAllTextAsync(filePath);

            // Send the message to the Service Bus queue
            await SendMessageAsync(messageContent, sessionId);
            Console.WriteLine($"{DateTime.Now}: message with sessionId {sessionId} sent successfully");
        }
        catch (Exception ex)    
        {
            // If an error occurs, log it and indicate failure
            Console.WriteLine($"Error sending message: {ex.Message}");
        }
    }

    private static async Task SendMessageAsync(string messageContent, string sessionId)
    {
        // Create a ServiceBusClient to connect to the namespace
        await using var client = new ServiceBusClient(ServiceBusConnectionString);

        // Create a sender for the queue
        ServiceBusSender sender = client.CreateSender(QueueName);

        try
        {
            // Create a message and set the SessionId
            ServiceBusMessage message = new ServiceBusMessage(messageContent)
            {
                SessionId = sessionId
            };

            // Send the message to the queue
            await sender.SendMessageAsync(message);
            //Console.WriteLine($"Message sent: {messageContent} with SessionId: {sessionId}");
           // Console.WriteLine($"Message with SessionId: {sessionId}");
        }
        finally
        {
            // Dispose the sender
            await sender.DisposeAsync();
        }
    }
}