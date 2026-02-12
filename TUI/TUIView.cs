namespace RalphController.TUI;

/// <summary>
/// Active view in the Teams TUI.
/// Ctrl+T toggles TaskList; Enter/Escape switches between AgentList and AgentDetail.
/// </summary>
public enum TUIView
{
    /// <summary>
    /// Split view: agent list on the left, selected agent output on the right.
    /// Shift+Up/Down cycles selectedAgentIndex.
    /// </summary>
    AgentList,

    /// <summary>
    /// Full-screen scrollable output for the currently selected agent.
    /// Enter from AgentList enters this view; Escape returns to AgentList.
    /// </summary>
    AgentDetail,

    /// <summary>
    /// Table of all tasks with ID, Title, Status, Agent, Dependencies.
    /// Ctrl+T toggles this view on/off.
    /// </summary>
    TaskList
}
