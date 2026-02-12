using System.Collections.Concurrent;

namespace RalphController.TUI;

/// <summary>
/// Thread-safe, per-agent output buffer with a configurable maximum line count.
/// Each agent's output is stored independently so the TUI can display
/// the selected agent's log without mixing output from other agents.
/// </summary>
public sealed class AgentOutputBuffer
{
    private readonly ConcurrentDictionary<string, AgentLines> _buffers = new();
    private readonly int _maxLines;

    /// <summary>
    /// Raised when a line is appended to any agent's buffer.
    /// The string argument is the agent ID that received new output.
    /// Subscribers must be thread-safe.
    /// </summary>
    public event Action<string>? OnOutputReceived;

    /// <summary>
    /// Create an output buffer manager.
    /// </summary>
    /// <param name="maxLinesPerAgent">Maximum lines to retain per agent (ring buffer).</param>
    public AgentOutputBuffer(int maxLinesPerAgent = 500)
    {
        _maxLines = maxLinesPerAgent;
    }

    /// <summary>
    /// Append a line of output for the given agent.
    /// </summary>
    public void Append(string agentId, string line)
    {
        var buffer = _buffers.GetOrAdd(agentId, _ => new AgentLines(_maxLines));
        buffer.Add(line);
        OnOutputReceived?.Invoke(agentId);
    }

    /// <summary>
    /// Get the most recent lines for an agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="maxLines">Maximum number of lines to return (0 = all available).</param>
    /// <returns>Lines in chronological order (oldest first).</returns>
    public IReadOnlyList<string> GetLines(string agentId, int maxLines = 0)
    {
        if (!_buffers.TryGetValue(agentId, out var buffer))
            return Array.Empty<string>();

        return buffer.GetTail(maxLines);
    }

    /// <summary>
    /// Get the total number of buffered lines for an agent.
    /// </summary>
    public int GetLineCount(string agentId)
    {
        return _buffers.TryGetValue(agentId, out var buffer) ? buffer.Count : 0;
    }

    /// <summary>
    /// Get all agent IDs that have output.
    /// </summary>
    public IReadOnlyCollection<string> GetAgentIds()
    {
        return _buffers.Keys.ToList();
    }

    /// <summary>
    /// Clear all output for all agents.
    /// </summary>
    public void Clear()
    {
        _buffers.Clear();
    }

    /// <summary>
    /// Clear output for a specific agent.
    /// </summary>
    public void Clear(string agentId)
    {
        _buffers.TryRemove(agentId, out _);
    }

    /// <summary>
    /// Thread-safe ring buffer of lines for a single agent.
    /// </summary>
    private sealed class AgentLines
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = new();
        private readonly int _maxLines;

        public AgentLines(int maxLines)
        {
            _maxLines = maxLines;
        }

        public int Count
        {
            get
            {
                lock (_lock)
                    return _lines.Count;
            }
        }

        public void Add(string line)
        {
            lock (_lock)
            {
                _lines.Add(line);

                // Trim from the front when exceeding max
                if (_lines.Count > _maxLines)
                {
                    var excess = _lines.Count - _maxLines;
                    _lines.RemoveRange(0, excess);
                }
            }
        }

        public IReadOnlyList<string> GetTail(int count)
        {
            lock (_lock)
            {
                if (count <= 0 || count >= _lines.Count)
                    return _lines.ToList();

                return _lines.GetRange(_lines.Count - count, count);
            }
        }
    }
}
