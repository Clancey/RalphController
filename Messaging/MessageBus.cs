using System.Text.Json;
using System.Text.Json.Serialization;
using RalphController.Parallel;

namespace RalphController.Messaging;

/// <summary>
/// File-based message bus for inter-agent communication.
/// Each agent has a JSONL inbox file. Messages are appended atomically with file-locking.
/// </summary>
public class MessageBus : IDisposable
{
    private readonly string _mailboxDir;
    private readonly string _selfAgentId;
    private readonly string _selfInboxPath;
    private long _readCursor;
    private bool _disposed;
    private readonly object _cursorLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Create a message bus for a specific agent
    /// </summary>
    /// <param name="mailboxDir">Directory containing all agent inbox files</param>
    /// <param name="selfAgentId">This agent's ID (determines which inbox to read)</param>
    public MessageBus(string mailboxDir, string selfAgentId)
    {
        _mailboxDir = mailboxDir;
        _selfAgentId = selfAgentId;
        _selfInboxPath = Path.Combine(mailboxDir, $"{selfAgentId}.jsonl");

        if (!Directory.Exists(mailboxDir))
        {
            Directory.CreateDirectory(mailboxDir);
        }

        _readCursor = CountExistingLines(_selfInboxPath);
    }

    /// <summary>
    /// Send a message to a specific agent
    /// </summary>
    public void Send(string toAgentId, MessageType type, string content, Dictionary<string, string>? metadata = null)
    {
        var message = new Message
        {
            FromAgentId = _selfAgentId,
            ToAgentId = toAgentId,
            Type = type,
            Content = content,
            Metadata = metadata
        };

        AppendToInbox(toAgentId, message);
    }

    /// <summary>
    /// Send a pre-built message
    /// </summary>
    public void Send(Message message)
    {
        AppendToInbox(message.ToAgentId, message);
    }

    /// <summary>
    /// Broadcast a message to all agents (except self)
    /// </summary>
    /// <param name="content">Message content</param>
    /// <param name="knownAgentIds">List of known agent IDs to broadcast to</param>
    public void Broadcast(string content, IEnumerable<string> knownAgentIds)
    {
        var message = Message.BroadcastMessage(_selfAgentId, content);

        foreach (var agentId in knownAgentIds.Where(id => id != _selfAgentId))
        {
            try
            {
                AppendToInbox(agentId, message);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Poll for new messages (non-blocking). Returns empty list if no new messages.
    /// </summary>
    public IReadOnlyList<Message> Poll()
    {
        var messages = new List<Message>();

        lock (_cursorLock)
        {
            if (!File.Exists(_selfInboxPath))
            {
                return messages;
            }

            var lockPath = Path.Combine(_mailboxDir, $"{_selfAgentId}.lock");
            using var fileLock = FileLock.TryAcquire(lockPath, TimeSpan.FromSeconds(2));
            if (fileLock == null)
            {
                return messages;
            }

            try
            {
                var lines = File.ReadAllLines(_selfInboxPath);
                var newLineCount = lines.Length - (int)_readCursor;

                if (newLineCount <= 0)
                {
                    return messages;
                }

                for (var i = (int)_readCursor; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var msg = JsonSerializer.Deserialize<Message>(line, JsonOptions);
                        if (msg != null)
                        {
                            messages.Add(msg);
                        }
                    }
                    catch
                    {
                    }
                }

                _readCursor = lines.Length;
            }
            catch
            {
            }
        }

        return messages;
    }

    /// <summary>
    /// Wait for new messages with timeout (blocking)
    /// </summary>
    public async Task<IReadOnlyList<Message>> WaitForMessages(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromMilliseconds(200);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var messages = Poll();
            if (messages.Count > 0)
            {
                return messages;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return Array.Empty<Message>();
    }

    /// <summary>
    /// Wait for a specific message type with timeout
    /// </summary>
    public async Task<Message?> WaitForMessage(MessageType type, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromMilliseconds(200);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var messages = Poll();
            var match = messages.FirstOrDefault(m => m.Type == type);
            if (match != null)
            {
                return match;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Get the count of unread messages
    /// </summary>
    public int GetUnreadCount()
    {
        if (!File.Exists(_selfInboxPath))
        {
            return 0;
        }

        lock (_cursorLock)
        {
            var lines = File.ReadAllLines(_selfInboxPath);
            return Math.Max(0, lines.Length - (int)_readCursor);
        }
    }

    /// <summary>
    /// Get all agent IDs that have inbox files
    /// </summary>
    public IEnumerable<string> GetKnownAgentIds()
    {
        if (!Directory.Exists(_mailboxDir))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.GetFiles(_mailboxDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id != null)
            .Cast<string>();
    }

    private void AppendToInbox(string agentId, Message message)
    {
        var inboxPath = Path.Combine(_mailboxDir, $"{agentId}.jsonl");
        var lockPath = Path.Combine(_mailboxDir, $"{agentId}.lock");

        using var fileLock = FileLock.TryAcquire(lockPath, TimeSpan.FromSeconds(5));
        if (fileLock == null)
        {
            throw new InvalidOperationException($"Could not acquire lock for inbox {agentId}");
        }

        var json = JsonSerializer.Serialize(message, JsonOptions);
        File.AppendAllText(inboxPath, json + "\n");
    }

    private static long CountExistingLines(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            return File.ReadAllLines(filePath).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Clear all messages from this agent's inbox (cleanup utility)
    /// </summary>
    public void ClearInbox()
    {
        lock (_cursorLock)
        {
            if (File.Exists(_selfInboxPath))
            {
                File.Delete(_selfInboxPath);
            }

            _readCursor = 0;
        }
    }

    /// <summary>
    /// Create the lead's message bus
    /// </summary>
    public static MessageBus CreateForLead(string mailboxDir) =>
        new(mailboxDir, "lead");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
