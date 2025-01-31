using System.Text.Json;

namespace ServiceBus_Queue_Sender;

public class Config
{
    public static string ServiceBusConnectionString { get; set; }
    public static string QueueName { get; set; }

    static Config()
    {
        ServiceBusConnectionString = string.Empty;
        QueueName = string.Empty;

        var json = File.ReadAllText("appsettings/appsettings.int.json");
        var config = JsonSerializer.Deserialize<ConfigData>(json);
        if (config != null)
        {
            ServiceBusConnectionString = config.ServiceBusConnectionString ?? string.Empty;
            QueueName = config.QueueName ?? string.Empty;
        }
        else
        {
            throw new InvalidOperationException("Configuration data is null.");
        }
    }
}

public class ConfigData
{
    public string? ServiceBusConnectionString { get; set; }
    public string? QueueName { get; set; }
}