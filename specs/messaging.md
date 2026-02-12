# Messaging: Inter-Agent Communication

## Status
Draft

## Summary
Add a file-based messaging system so agents can communicate with each other and the lead. Supports direct messages, broadcasts, and structured message types (shutdown requests, plan approvals, status updates).

## Reference
From Claude Code docs:
> Teammates message each other directly. When teammates send messages, they're delivered automatically to recipients.
> - **message**: send a message to one specific teammate
> - **broadcast**: send to all teammates simultaneously. Use sparingly, as costs scale with team size.

## Current State

**What exists:** Nothing. Agents have zero inter-agent communication. The lead fires off agents and waits for them to finish. No way for agents to share findings, request help, or coordinate.

**What's broken:** Without messaging, agents can't:
- Report findings to each other (research tasks)
- Request plan approval from lead
- Receive shutdown requests
- Challenge each other's approaches (debate mode)
- Ask lead for clarification

## Target State

### Message Types

| Type | From | To | Purpose |
|------|------|----|---------|
| `Text` | Any | Any | General communication |
| `StatusUpdate` | Agent | Lead | Progress report, task completion |
| `ShutdownRequest` | Lead | Agent | Ask agent to stop gracefully |
| `ShutdownResponse` | Agent | Lead | Accept or reject shutdown |
| `PlanSubmission` | Agent | Lead | Agent submits implementation plan |
| `PlanApproval` | Lead | Agent | Approve or reject with feedback |
| `TaskAssignment` | Lead | Agent | Explicitly assign a task |
| `Broadcast` | Any | All | Message all teammates |

### Storage

Per-agent inbox as append-only JSONL:

```
~/.ralph/teams/{team}/mailbox/
  lead.jsonl
  agent-1.jsonl
  agent-2.jsonl
  ...
```

Each line is a JSON `Message` object. Agents read from their own inbox file. File-lock for concurrent writes.

### Delivery Model

- **Synchronous write**: Sender appends to recipient's inbox file
- **Polling read**: Recipient polls inbox on interval (500ms) or between task steps
- **No acknowledgement**: Fire-and-forget delivery. Recipient processes when ready.
- **Ordering**: Messages ordered by timestamp within each inbox

## Technical Requirements

1. **Create `Message` model** with all fields below.

2. **Create `MessageBus` class** - Handles sending (append to recipient file) and receiving (read new lines from own file).

3. **File-lock writes** - Use same `FileLock` from task-system to prevent concurrent writes to same inbox.

4. **Read cursor tracking** - Each agent tracks how many lines it's read from its inbox. On poll, only reads new lines.

5. **Broadcast** - Sends message to all agent inbox files except sender's.

6. **Message processing in agent loop** - Between tasks and during idle, agent calls `ProcessPendingMessages()` which reads inbox and handles each message type.

7. **Lead auto-receives** - TeamOrchestrator polls lead's inbox and processes teammate messages automatically (status updates, plan submissions).

## Architecture

### Message Model

```csharp
public record Message
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string FromAgentId { get; init; }
    public string ToAgentId { get; init; }     // "*" for broadcast
    public MessageType Type { get; init; }
    public string Content { get; init; }        // Human-readable text
    public Dictionary<string, string>? Metadata { get; init; }  // Structured data
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public enum MessageType
{
    Text,
    StatusUpdate,
    ShutdownRequest,
    ShutdownResponse,
    PlanSubmission,
    PlanApproval,
    TaskAssignment,
    Broadcast
}
```

### MessageBus

```csharp
public class MessageBus : IDisposable
{
    private readonly string _mailboxDir;    // ~/.ralph/teams/{team}/mailbox/
    private readonly string _selfAgentId;
    private long _readCursor;               // Lines already read from own inbox

    /// Send a message to a specific agent
    public void Send(string toAgentId, MessageType type, string content,
                     Dictionary<string, string>? metadata = null);

    /// Send to all agents except self
    public void Broadcast(string content, MessageType type = MessageType.Broadcast);

    /// Read new messages from own inbox (non-blocking)
    public IReadOnlyList<Message> Poll();

    /// Read new messages, block until at least one arrives or timeout
    public async Task<IReadOnlyList<Message>> WaitForMessages(
        TimeSpan timeout, CancellationToken ct);

    /// Read new messages of a specific type, block until match or timeout
    public async Task<Message?> WaitForMessage(
        MessageType type, TimeSpan timeout, CancellationToken ct);
}
```

### Integration Points

**TeamAgent.RunLoopAsync:**
```csharp
// Between tasks:
var messages = _messageBus.Poll();
foreach (var msg in messages)
{
    switch (msg.Type)
    {
        case MessageType.ShutdownRequest:
            if (State == AgentState.Idle)
                AcceptShutdown();
            else
                DeferShutdown("Finishing current task");
            break;
        case MessageType.PlanApproval:
            _planApprovalResult = msg;
            break;
        case MessageType.TaskAssignment:
            _assignedTaskId = msg.Metadata?["taskId"];
            break;
        case MessageType.Text:
            // Include in agent's next AI prompt as context
            _pendingContext.Add(msg);
            break;
    }
}
```

**TeamOrchestrator:**
```csharp
// Lead's monitoring loop:
while (teamActive)
{
    var messages = _leadBus.Poll();
    foreach (var msg in messages)
    {
        switch (msg.Type)
        {
            case MessageType.StatusUpdate:
                UpdateAgentProgress(msg);
                break;
            case MessageType.PlanSubmission:
                var approval = await ReviewPlan(msg);
                _leadBus.Send(msg.FromAgentId, MessageType.PlanApproval, approval);
                break;
            case MessageType.ShutdownResponse:
                HandleShutdownResponse(msg);
                break;
        }
    }
    await Task.Delay(500);
}
```

## Acceptance Criteria

- [ ] Agents can send direct messages to any other agent by ID
- [ ] Lead can broadcast to all agents
- [ ] Messages persist as JSONL files (crash-safe)
- [ ] Concurrent writes to same inbox don't corrupt data (file-lock)
- [ ] Agent polls inbox between tasks and during idle
- [ ] ShutdownRequest/Response protocol works end-to-end
- [ ] PlanSubmission/Approval protocol works end-to-end
- [ ] Lead receives StatusUpdate messages automatically
- [ ] Messages include timestamp and are ordered
- [ ] Inbox files cleaned up on team cleanup

## Files to Modify/Create

| Action | File |
|--------|------|
| Create | `Messaging/Message.cs` |
| Create | `Messaging/MessageBus.cs` |
| Modify | `TeamAgent.cs` (add message processing) |
| Modify | `TeamController.cs` / `TeamOrchestrator.cs` (lead message loop) |

## Dependencies
- [task-system.md](task-system.md) - `FileLock` utility shared between task store and mailbox
