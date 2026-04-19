namespace RetroBat.Domain.Events;

public class EventEnvelope
{
    public string Type { get; set; } = string.Empty;
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string NodeId { get; set; } = "cab-01";
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public object? Payload { get; set; }
}

public class MameOutputEvent
{
    public string Source { get; set; } = "mame.network";
    public int Port { get; set; } = 8000;
    public string MachineName { get; set; } = string.Empty;
    public List<MameSignal> Signals { get; set; } = new();
}

public class MameSignal
{
    public string Key { get; set; } = string.Empty;
    public int Value { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
}
