using System.Text.Json.Serialization;

namespace RalphController.Messaging;

/// <summary>
/// Message type for inter-agent communication
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageType
{
    /// <summary>General communication</summary>
    Text,

    /// <summary>Progress report, task completion</summary>
    StatusUpdate,

    /// <summary>Lead asks agent to stop gracefully</summary>
    ShutdownRequest,

    /// <summary>Agent accepts or rejects shutdown</summary>
    ShutdownResponse,

    /// <summary>Agent submits implementation plan</summary>
    PlanSubmission,

    /// <summary>Lead approves or rejects plan with feedback</summary>
    PlanApproval,

    /// <summary>Lead explicitly assigns a task</summary>
    TaskAssignment,

    /// <summary>Broadcast to all teammates</summary>
    Broadcast
}

/// <summary>
/// Message for inter-agent communication. Stored as JSONL in agent inbox files.
/// </summary>
public record Message
{
    /// <summary>Unique message identifier (12-char hex)</summary>
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Sender agent ID</summary>
    public required string FromAgentId { get; init; }

    /// <summary>Recipient agent ID ("*" for broadcast, "lead" for orchestrator)</summary>
    public required string ToAgentId { get; init; }

    /// <summary>Message type</summary>
    public MessageType Type { get; init; }

    /// <summary>Human-readable content</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Structured data (e.g., taskId, planId, approval status)</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>Message timestamp (UTC)</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Create a simple text message
    /// </summary>
    public static Message TextMessage(string from, string to, string content) =>
        new() { FromAgentId = from, ToAgentId = to, Type = MessageType.Text, Content = content };

    /// <summary>
    /// Create a status update message
    /// </summary>
    public static Message StatusUpdateMessage(string from, string status, Dictionary<string, string>? metadata = null) =>
        new() { FromAgentId = from, ToAgentId = "lead", Type = MessageType.StatusUpdate, Content = status, Metadata = metadata };

    /// <summary>
    /// Create a shutdown request message
    /// </summary>
    public static Message ShutdownRequestMessage(string from, string to, string reason = "") =>
        new() { FromAgentId = from, ToAgentId = to, Type = MessageType.ShutdownRequest, Content = reason };

    /// <summary>
    /// Create a shutdown response message
    /// </summary>
    public static Message ShutdownResponseMessage(string from, bool accepted, string? reason = null) =>
        new()
        {
            FromAgentId = from,
            ToAgentId = "lead",
            Type = MessageType.ShutdownResponse,
            Content = accepted ? "Accepted" : "Rejected",
            Metadata = reason != null ? new Dictionary<string, string> { ["reason"] = reason } : null
        };

    /// <summary>
    /// Create a plan submission message
    /// </summary>
    public static Message PlanSubmissionMessage(string from, string plan, string? taskId = null) =>
        new()
        {
            FromAgentId = from,
            ToAgentId = "lead",
            Type = MessageType.PlanSubmission,
            Content = plan,
            Metadata = taskId != null ? new Dictionary<string, string> { ["taskId"] = taskId } : null
        };

    /// <summary>
    /// Create a plan approval message
    /// </summary>
    public static Message PlanApprovalMessage(string from, string to, bool approved, string? feedback = null) =>
        new()
        {
            FromAgentId = from,
            ToAgentId = to,
            Type = MessageType.PlanApproval,
            Content = approved ? "Approved" : "Rejected",
            Metadata = new Dictionary<string, string>
            {
                ["approved"] = approved.ToString().ToLower(),
                ["feedback"] = feedback ?? string.Empty
            }
        };

    /// <summary>
    /// Create a task assignment message
    /// </summary>
    public static Message TaskAssignmentMessage(string from, string to, string taskId, string taskDescription) =>
        new()
        {
            FromAgentId = from,
            ToAgentId = to,
            Type = MessageType.TaskAssignment,
            Content = taskDescription,
            Metadata = new Dictionary<string, string> { ["taskId"] = taskId }
        };

    /// <summary>
    /// Create a broadcast message
    /// </summary>
    public static Message BroadcastMessage(string from, string content) =>
        new() { FromAgentId = from, ToAgentId = "*", Type = MessageType.Broadcast, Content = content };
}
