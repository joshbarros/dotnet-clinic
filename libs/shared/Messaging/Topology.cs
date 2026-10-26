using RabbitMQ.Client;

namespace Clinic.Shared.Messaging;

public static class Topology
{
    public const string Exchange = "clinic.work";
    public const string DeadLetterExchange = "clinic.dlx";
    public const string RequeueExchange = "clinic.requeue";
    public const string KafkaTopic = "clinic.events";

    public const int RetryDelayMs = 2000;
    public const int MaxAttempts = 3;

    public const string ReserveKey = "slots.reserve";
    public const string ReleaseKey = "slots.release";
    public const string HeldKey = "slots.held";
    public const string ShortKey = "slots.short";
    public const string ReleasedKey = "slots.released";
    public const string IssuedKey = "letter.issued";
    public const string RejectedKey = "letter.rejected";

    public const string SchedulingQueue = "scheduling.commands";
    public const string ReferralsQueue = "referrals.work";
    public const string AppointmentsQueue = "appointments.events";

    public static async Task DeclareAsync(IChannel channel, string queue, string[] routingKeys, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RequeueExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync($"{queue}.dlq", DeadLetterExchange, queue, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.retry", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMs,
                ["x-dead-letter-exchange"] = RequeueExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, RequeueExchange, queue, cancellationToken: ct);
        foreach (var key in routingKeys)
            await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}
