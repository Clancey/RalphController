using RalphController.Models;
using RalphController.Workflow;
using Spectre.Console;

namespace RalphController;

/// <summary>
/// Wizard steps for setup flow
/// </summary>
public enum WizardStep
{
    ModeSelection,
    OllamaUrl,
    ModelDiscovery,
    ModelSelection,
    PrimaryModel,
    RoleSetup,
    WorkflowConfig,
    ProviderSelection,
    ProviderModelSelection,
    IterationMode,
    Complete
}

/// <summary>
/// State container for wizard choices
/// </summary>
public class WizardState
{
    public bool UseMultiModel { get; set; }
    public string? OllamaUrl { get; set; }
    public bool HasOllama { get; set; }
    public List<ModelSpec> AvailableModels { get; set; } = new();
    public List<ModelSpec> SelectedModels { get; set; } = new();
    public ModelSpec? PrimaryModel { get; set; }
    public string RoleSetupChoice { get; set; } = "Auto-assign";
    public Dictionary<AgentRole, ModelSpec> RoleAssignments { get; set; } = new();
    public WorkflowType SelectedWorkflow { get; set; } = WorkflowType.Verification;
    public CollaborationConfig? Collaboration { get; set; }
    public AIProvider Provider { get; set; } = AIProvider.Claude;
    public string? SelectedModel { get; set; }
    public bool ContinuousMode { get; set; } = true;

    // Provider-specific models
    public string? ClaudeModel { get; set; }
    public string? CodexModel { get; set; }
    public string? GeminiModel { get; set; }
    public string? CursorModel { get; set; }
    public string? CopilotModel { get; set; }
    public string? OpenCodeModel { get; set; }
    public string? OllamaModel { get; set; }
}

/// <summary>
/// Interactive setup wizard with back navigation
/// </summary>
public class SetupWizard
{
    private const string BackOption = "← Back";
    private readonly ProjectSettings _projectSettings;
    private readonly GlobalSettings _globalSettings;
    private readonly bool _freshMode;
    private readonly Func<Task<List<string>>> _getClaudeModels;
    private readonly Func<Task<List<string>>> _getCodexModels;
    private readonly Func<Task<List<string>>> _getCopilotModels;
    private readonly Func<Task<List<string>>> _getGeminiModels;
    private readonly Func<Task<List<string>>> _getCursorModels;
    private readonly Func<Task<List<string>>> _getOpenCodeModels;
    private readonly Func<string, Task<List<string>>> _getOllamaModels;
    private readonly Func<AIProvider, bool> _isProviderInstalled;

    public WizardState State { get; } = new();

    public SetupWizard(
        ProjectSettings projectSettings,
        GlobalSettings globalSettings,
        bool freshMode,
        Func<Task<List<string>>> getClaudeModels,
        Func<Task<List<string>>> getCodexModels,
        Func<Task<List<string>>> getCopilotModels,
        Func<Task<List<string>>> getGeminiModels,
        Func<Task<List<string>>> getCursorModels,
        Func<Task<List<string>>> getOpenCodeModels,
        Func<string, Task<List<string>>> getOllamaModels,
        Func<AIProvider, bool> isProviderInstalled)
    {
        _projectSettings = projectSettings;
        _globalSettings = globalSettings;
        _freshMode = freshMode;
        _getClaudeModels = getClaudeModels;
        _getCodexModels = getCodexModels;
        _getCopilotModels = getCopilotModels;
        _getGeminiModels = getGeminiModels;
        _getCursorModels = getCursorModels;
        _getOpenCodeModels = getOpenCodeModels;
        _getOllamaModels = getOllamaModels;
        _isProviderInstalled = isProviderInstalled;

        // Initialize from saved settings
        State.OllamaUrl = globalSettings.LastOllamaUrl ?? "http://localhost:11434";
        State.HasOllama = !string.IsNullOrEmpty(globalSettings.LastOllamaUrl);
    }

    /// <summary>
    /// Run the wizard and return the final state
    /// </summary>
    public async Task<WizardState> RunAsync()
    {
        var currentStep = WizardStep.ModeSelection;
        var stepHistory = new Stack<WizardStep>();

        while (currentStep != WizardStep.Complete)
        {
            var (nextStep, wentBack) = await ExecuteStepAsync(currentStep, stepHistory);

            if (wentBack && stepHistory.Count > 0)
            {
                currentStep = stepHistory.Pop();
            }
            else if (!wentBack)
            {
                stepHistory.Push(currentStep);
                currentStep = nextStep;
            }
        }

        return State;
    }

    private async Task<(WizardStep nextStep, bool wentBack)> ExecuteStepAsync(WizardStep step, Stack<WizardStep> history)
    {
        return step switch
        {
            WizardStep.ModeSelection => await ModeSelectionStepAsync(history.Count > 0),
            WizardStep.OllamaUrl => await OllamaUrlStepAsync(),
            WizardStep.ModelDiscovery => await ModelDiscoveryStepAsync(),
            WizardStep.ModelSelection => await ModelSelectionStepAsync(),
            WizardStep.PrimaryModel => await PrimaryModelStepAsync(),
            WizardStep.RoleSetup => await RoleSetupStepAsync(),
            WizardStep.WorkflowConfig => await WorkflowConfigStepAsync(),
            WizardStep.ProviderSelection => await ProviderSelectionStepAsync(),
            WizardStep.ProviderModelSelection => await ProviderModelSelectionStepAsync(),
            WizardStep.IterationMode => await IterationModeStepAsync(),
            _ => (WizardStep.Complete, false)
        };
    }

    private async Task<(WizardStep, bool)> ModeSelectionStepAsync(bool canGoBack)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            "[white]Multi-model collaboration allows you to use different AI models for different roles:\n" +
            "• [cyan]Planner[/] - designs implementation strategy\n" +
            "• [cyan]Challenger[/] - finds edge cases and issues\n" +
            "• [cyan]Reviewer[/] - verifies code quality\n" +
            "• [cyan]Implementer[/] - writes the actual code\n\n" +
            "You can select models from [yellow]any provider[/] and assign them to optimal roles.[/]")
            .Header("[yellow]Multi-Model Collaboration[/]")
            .BorderColor(Color.Blue));

        var choices = new List<string>
        {
            "Multi-model collaboration (recommended) - use multiple models for different roles",
            "Single model only - use just one model for everything"
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("\n[yellow]How would you like to work?[/]")
                .AddChoices(choices));

        State.UseMultiModel = choice.StartsWith("Multi-model");

        if (State.UseMultiModel)
        {
            return (WizardStep.OllamaUrl, false);
        }
        else
        {
            AnsiConsole.MarkupLine("\n[green]✓ Single model mode[/]");
            return (WizardStep.ProviderSelection, false);
        }
    }

    private Task<(WizardStep, bool)> OllamaUrlStepAsync()
    {
        AnsiConsole.WriteLine();

        var choices = new List<string>
        {
            "Yes, I have a local Ollama/LM Studio server",
            "No, skip local models",
            BackOption
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Do you have a local Ollama or LM Studio server?[/]")
                .AddChoices(choices));

        if (choice == BackOption)
            return Task.FromResult((WizardStep.ModeSelection, true));

        State.HasOllama = choice.StartsWith("Yes");

        if (State.HasOllama)
        {
            State.OllamaUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter Ollama/LM Studio URL:[/]")
                    .DefaultValue(State.OllamaUrl ?? "http://localhost:11434"));
            _globalSettings.LastOllamaUrl = State.OllamaUrl;
        }
        else
        {
            State.OllamaUrl = null;
        }

        return Task.FromResult((WizardStep.ModelDiscovery, false));
    }

    private async Task<(WizardStep, bool)> ModelDiscoveryStepAsync()
    {
        AnsiConsole.MarkupLine("\n[dim]Discovering models from installed providers...[/]");

        State.AvailableModels.Clear();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Discovering models...", async ctx =>
            {
                // Claude
                if (_isProviderInstalled(AIProvider.Claude))
                {
                    ctx.Status("Checking Claude...");
                    try
                    {
                        var models = await _getClaudeModels();
                        foreach (var m in models)
                            State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.Claude, Model = m });
                    }
                    catch { }
                }

                // Codex
                if (_isProviderInstalled(AIProvider.Codex))
                {
                    ctx.Status("Checking Codex...");
                    try
                    {
                        var models = await _getCodexModels();
                        foreach (var m in models)
                            State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.Codex, Model = m });
                    }
                    catch { }
                }

                // Copilot
                if (_isProviderInstalled(AIProvider.Copilot))
                {
                    ctx.Status("Checking Copilot...");
                    try
                    {
                        var models = await _getCopilotModels();
                        foreach (var m in models)
                            State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.Copilot, Model = m });
                    }
                    catch { }
                }

                // Gemini
                if (_isProviderInstalled(AIProvider.Gemini))
                {
                    ctx.Status("Checking Gemini...");
                    try
                    {
                        var models = await _getGeminiModels();
                        foreach (var m in models)
                            State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.Gemini, Model = m });
                    }
                    catch { }
                }

                // Cursor
                if (_isProviderInstalled(AIProvider.Cursor))
                {
                    ctx.Status("Checking Cursor...");
                    try
                    {
                        var models = await _getCursorModels();
                        foreach (var m in models)
                            State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.Cursor, Model = m });
                    }
                    catch { }
                }

                // OpenCode
                ctx.Status("Checking OpenCode...");
                try
                {
                    var models = await _getOpenCodeModels();
                    foreach (var m in models)
                        State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.OpenCode, Model = m });
                }
                catch { }

                // Ollama
                if (State.HasOllama && !string.IsNullOrEmpty(State.OllamaUrl))
                {
                    ctx.Status("Checking Ollama...");
                    try
                    {
                        var models = await _getOllamaModels(State.OllamaUrl);
                        foreach (var m in models)
                            State.AvailableModels.Add(new ModelSpec { Provider = AIProvider.Ollama, Model = m, BaseUrl = State.OllamaUrl });
                    }
                    catch { }
                }
            });

        if (State.AvailableModels.Count < 2)
        {
            AnsiConsole.MarkupLine("[yellow]Not enough models found for multi-model collaboration.[/]");
            AnsiConsole.MarkupLine("[yellow]Falling back to single model mode.[/]");
            State.UseMultiModel = false;
            return (WizardStep.ProviderSelection, false);
        }

        AnsiConsole.MarkupLine($"[green]Found {State.AvailableModels.Count} models across providers.[/]");
        return (WizardStep.ModelSelection, false);
    }

    private Task<(WizardStep, bool)> ModelSelectionStepAsync()
    {
        AnsiConsole.WriteLine();

        // Build multi-select with provider groups
        var multiSelectPrompt = new MultiSelectionPrompt<string>()
            .Title("[yellow]Select models to include in your collaboration pool:[/]")
            .PageSize(30)
            .Required()
            .InstructionsText("[grey](Press <space> to toggle, <enter> to confirm - select at least 2)[/]");

        // Add back option first
        multiSelectPrompt.AddChoice(BackOption);

        var providerGroups = State.AvailableModels
            .GroupBy(m => m.Provider)
            .OrderBy(g => g.Key.ToString());

        foreach (var group in providerGroups)
        {
            var models = group.Select(m => $"{m.Provider}: {m.Model}").ToList();
            if (models.Count > 0)
                multiSelectPrompt.AddChoiceGroup(group.Key.ToString(), models);
        }

        var selectedNames = AnsiConsole.Prompt(multiSelectPrompt);

        if (selectedNames.Contains(BackOption))
            return Task.FromResult((WizardStep.OllamaUrl, true));

        // Map back to ModelSpecs
        State.SelectedModels = selectedNames
            .Where(n => n != BackOption)
            .Select(name =>
            {
                var parts = name.Split(": ", 2);
                if (parts.Length == 2 && Enum.TryParse<AIProvider>(parts[0], out var prov))
                {
                    return State.AvailableModels.FirstOrDefault(m =>
                        m.Provider == prov && m.Model == parts[1]);
                }
                return null;
            })
            .Where(m => m != null)
            .Cast<ModelSpec>()
            .ToList();

        if (State.SelectedModels.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Please select at least 2 models.[/]");
            return Task.FromResult((WizardStep.ModelSelection, false));
        }

        AnsiConsole.MarkupLine($"\n[green]✓ Selected {State.SelectedModels.Count} models[/]");
        return Task.FromResult((WizardStep.PrimaryModel, false));
    }

    private Task<(WizardStep, bool)> PrimaryModelStepAsync()
    {
        AnsiConsole.WriteLine();

        var choices = State.SelectedModels
            .Select(m => $"{m.Provider}: {m.Model}")
            .Prepend(BackOption)
            .ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select the primary model (used for main implementation):[/]")
                .PageSize(15)
                .AddChoices(choices));

        if (choice == BackOption)
            return Task.FromResult((WizardStep.ModelSelection, true));

        var parts = choice.Split(": ", 2);
        if (parts.Length == 2 && Enum.TryParse<AIProvider>(parts[0], out var prov))
        {
            State.PrimaryModel = State.SelectedModels.FirstOrDefault(m =>
                m.Provider == prov && m.Model == parts[1]);
        }

        if (State.PrimaryModel != null)
        {
            AnsiConsole.MarkupLine($"\n[green]✓ Primary model: {State.PrimaryModel.DisplayName}[/]");

            // Update provider-specific model settings
            switch (State.PrimaryModel.Provider)
            {
                case AIProvider.Claude:
                    State.ClaudeModel = State.PrimaryModel.Model;
                    State.Provider = AIProvider.Claude;
                    break;
                case AIProvider.Codex:
                    State.CodexModel = State.PrimaryModel.Model;
                    State.Provider = AIProvider.Codex;
                    break;
                case AIProvider.Copilot:
                    State.CopilotModel = State.PrimaryModel.Model;
                    State.Provider = AIProvider.Copilot;
                    break;
                case AIProvider.Gemini:
                    State.GeminiModel = State.PrimaryModel.Model;
                    State.Provider = AIProvider.Gemini;
                    break;
                case AIProvider.Cursor:
                    State.CursorModel = State.PrimaryModel.Model;
                    State.Provider = AIProvider.Cursor;
                    break;
                case AIProvider.OpenCode:
                    State.OpenCodeModel = State.PrimaryModel.Model;
                    State.Provider = AIProvider.OpenCode;
                    break;
                case AIProvider.Ollama:
                    State.OllamaModel = State.PrimaryModel.Model;
                    State.OllamaUrl = State.PrimaryModel.BaseUrl;
                    State.Provider = AIProvider.Ollama;
                    break;
            }
        }

        return Task.FromResult((WizardStep.RoleSetup, false));
    }

    private Task<(WizardStep, bool)> RoleSetupStepAsync()
    {
        AnsiConsole.WriteLine();

        var choices = new List<string>
        {
            "Auto-assign roles based on model strengths (recommended)",
            "Manually assign models to each role",
            "Use all models in round-robin rotation",
            BackOption
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]How would you like to assign models to roles?[/]")
                .AddChoices(choices));

        if (choice == BackOption)
            return Task.FromResult((WizardStep.PrimaryModel, true));

        State.RoleSetupChoice = choice;

        // Set up collaboration config based on choice
        State.Collaboration = new CollaborationConfig { Enabled = true };

        if (choice.StartsWith("Auto-assign"))
        {
            AutoAssignRoles();
            AnsiConsole.MarkupLine("\n[green]✓ Roles auto-assigned[/]");
        }
        else if (choice.StartsWith("Manually"))
        {
            ManuallyAssignRoles();
        }
        else
        {
            // Round-robin - all models for all roles
            foreach (var role in Enum.GetValues<AgentRole>())
            {
                State.Collaboration.Agents[role] = State.SelectedModels
                    .Select(m => new AgentConfig { Model = m })
                    .ToList();
            }
            AnsiConsole.MarkupLine("\n[green]✓ All models assigned to all roles (round-robin)[/]");
        }

        return Task.FromResult((WizardStep.WorkflowConfig, false));
    }

    private void AutoAssignRoles()
    {
        // Simple heuristic: assign based on model name patterns
        var implementers = State.SelectedModels.Where(m =>
            m.Model?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("coder", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var thinkers = State.SelectedModels.Where(m =>
            m.Model?.Contains("opus", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("pro", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("70b", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("80b", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var fast = State.SelectedModels.Where(m =>
            m.Model?.Contains("flash", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("mini", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("haiku", StringComparison.OrdinalIgnoreCase) == true ||
            m.Model?.Contains("sonnet", StringComparison.OrdinalIgnoreCase) == true).ToList();

        // Fallback to primary model for any empty categories
        var fallback = State.PrimaryModel ?? State.SelectedModels.First();

        if (implementers.Count == 0) implementers.Add(fallback);
        if (thinkers.Count == 0) thinkers.Add(fallback);
        if (fast.Count == 0) fast.Add(fallback);

        State.Collaboration!.Agents[AgentRole.Implementer] = implementers.Select(m => new AgentConfig { Model = m }).ToList();
        State.Collaboration.Agents[AgentRole.Planner] = thinkers.Select(m => new AgentConfig { Model = m }).ToList();
        State.Collaboration.Agents[AgentRole.Challenger] = thinkers.Select(m => new AgentConfig { Model = m }).ToList();
        State.Collaboration.Agents[AgentRole.Reviewer] = fast.Select(m => new AgentConfig { Model = m }).ToList();
        State.Collaboration.Agents[AgentRole.Synthesizer] = thinkers.Take(1).Select(m => new AgentConfig { Model = m }).ToList();
    }

    private void ManuallyAssignRoles()
    {
        var roles = new[] { AgentRole.Planner, AgentRole.Challenger, AgentRole.Reviewer, AgentRole.Implementer };

        foreach (var role in roles)
        {
            var modelChoices = State.SelectedModels.Select(m => $"{m.Provider}: {m.Model}").ToList();

            var selected = AnsiConsole.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title($"[yellow]Select models for [cyan]{role}[/] role:[/]")
                    .PageSize(15)
                    .Required()
                    .AddChoices(modelChoices));

            var roleModels = selected
                .Select(name =>
                {
                    var parts = name.Split(": ", 2);
                    if (parts.Length == 2 && Enum.TryParse<AIProvider>(parts[0], out var prov))
                        return State.SelectedModels.FirstOrDefault(m => m.Provider == prov && m.Model == parts[1]);
                    return null;
                })
                .Where(m => m != null)
                .Cast<ModelSpec>()
                .ToList();

            State.Collaboration!.Agents[role] = roleModels.Select(m => new AgentConfig { Model = m }).ToList();
        }
    }

    private Task<(WizardStep, bool)> WorkflowConfigStepAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            "[white]Choose a workflow for [cyan]final verification[/] when the task appears complete.\n\n" +
            "[dim]Note: Every iteration already uses collaborative task workflow:\n" +
            "Planner → Implementer → Reviewer (with optional Challenger)[/]\n\n" +
            "The workflow below determines how the [yellow]final quality check[/] is performed.[/]")
            .Header("[blue]Final Verification Workflow[/]")
            .Padding(1, 1));

        var choices = new List<string>
        {
            "Verification (Recommended) - Parallel reviewers + challenger edge-case finding",
            "Review - Code review with severity levels (Critical/High/Medium/Low)",
            "Consensus - Multiple models vote, synthesizer combines opinions",
            "Spec - Design specification with challenger (for planning features)",
            BackOption
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select final verification workflow:[/]")
                .AddChoices(choices));

        if (choice == BackOption)
            return Task.FromResult((WizardStep.RoleSetup, true));

        State.SelectedWorkflow = choice switch
        {
            var c when c.StartsWith("Verification") => WorkflowType.Verification,
            var c when c.StartsWith("Review") => WorkflowType.Review,
            var c when c.StartsWith("Consensus") => WorkflowType.Consensus,
            var c when c.StartsWith("Spec") => WorkflowType.Spec,
            _ => WorkflowType.Verification
        };

        State.Collaboration!.DefaultWorkflow = State.SelectedWorkflow;

        // Configure workflow-specific settings
        ConfigureWorkflowSettings();

        AnsiConsole.MarkupLine($"\n[green]✓ Final verification workflow: {State.SelectedWorkflow}[/]");
        return Task.FromResult((WizardStep.IterationMode, false));
    }

    private void ConfigureWorkflowSettings()
    {
        switch (State.SelectedWorkflow)
        {
            case WorkflowType.Verification:
                if (AnsiConsole.Confirm("Enable [cyan]verification workflow[/]?", true))
                {
                    State.Collaboration!.Verification = new VerificationWorkflowConfig
                    {
                        Enabled = true,
                        ReviewerCount = Math.Min(State.SelectedModels.Count, 3),
                        EnableChallenger = AnsiConsole.Confirm("Include challenger?", true),
                        ParallelExecution = AnsiConsole.Confirm("Run in parallel?", true)
                    };
                }
                break;

            case WorkflowType.Review:
                State.Collaboration!.Review = new ReviewWorkflowConfig
                {
                    ReviewerCount = Math.Min(State.SelectedModels.Count, 3),
                    ExpertValidation = true,
                    IncludeSuggestions = true
                };
                break;

            case WorkflowType.Spec:
                State.Collaboration!.Spec = new SpecWorkflowConfig
                {
                    EnableChallenger = true,
                    MaxRefinements = 2
                };
                break;

            case WorkflowType.Consensus:
                State.Collaboration!.Consensus = new ConsensusWorkflowConfig
                {
                    BlindedAnalysis = true,
                    EnableSynthesis = true,
                    Participants = State.SelectedModels.Take(4)
                        .Select(m => new ConsensusParticipant { Model = m })
                        .ToList()
                };
                break;
        }
    }

    private async Task<(WizardStep, bool)> ProviderSelectionStepAsync()
    {
        AnsiConsole.WriteLine();

        // Get available providers
        var availableProviders = new List<AIProvider>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking available providers...", async _ =>
            {
                foreach (AIProvider prov in Enum.GetValues<AIProvider>())
                {
                    if (prov == AIProvider.Ollama || await IsProviderInstalledAsync(prov))
                        availableProviders.Add(prov);
                }
            });

        if (availableProviders.Count == 0)
            availableProviders.Add(AIProvider.Claude); // Fallback

        var choices = availableProviders.Select(p => p.ToString()).Prepend(BackOption).ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select AI provider:[/]")
                .AddChoices(choices));

        if (choice == BackOption)
            return (WizardStep.ModeSelection, true);

        State.Provider = Enum.Parse<AIProvider>(choice);
        AnsiConsole.MarkupLine($"\n[green]✓ Provider: {State.Provider}[/]");

        return (WizardStep.ProviderModelSelection, false);
    }

    private async Task<(WizardStep, bool)> ProviderModelSelectionStepAsync()
    {
        AnsiConsole.WriteLine();

        var models = await GetModelsForProviderAsync(State.Provider);

        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No models found. Using default.[/]");
            return (WizardStep.IterationMode, false);
        }

        var choices = models.Prepend(BackOption).ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[yellow]Select {State.Provider} model:[/]")
                .PageSize(20)
                .AddChoices(choices));

        if (choice == BackOption)
            return (WizardStep.ProviderSelection, true);

        State.SelectedModel = choice;

        // Update provider-specific model
        switch (State.Provider)
        {
            case AIProvider.Claude: State.ClaudeModel = choice; break;
            case AIProvider.Codex: State.CodexModel = choice; break;
            case AIProvider.Copilot: State.CopilotModel = choice; break;
            case AIProvider.Gemini: State.GeminiModel = choice; break;
            case AIProvider.Cursor: State.CursorModel = choice; break;
            case AIProvider.OpenCode: State.OpenCodeModel = choice; break;
            case AIProvider.Ollama: State.OllamaModel = choice; break;
        }

        AnsiConsole.MarkupLine($"\n[green]✓ Model: {choice}[/]");
        return (WizardStep.IterationMode, false);
    }

    private Task<(WizardStep, bool)> IterationModeStepAsync()
    {
        AnsiConsole.WriteLine();

        var choices = new List<string>
        {
            "Continuous - keep iterating with AI feedback",
            "Until complete - stop when AI signals completion",
            BackOption
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select iteration mode:[/]")
                .AddChoices(choices));

        if (choice == BackOption)
        {
            // Go back to the right step based on mode
            return Task.FromResult((State.UseMultiModel ? WizardStep.WorkflowConfig : WizardStep.ProviderModelSelection, true));
        }

        State.ContinuousMode = choice.StartsWith("Continuous");
        AnsiConsole.MarkupLine($"\n[green]✓ Mode: {(State.ContinuousMode ? "Continuous" : "Until complete")}[/]");

        return Task.FromResult((WizardStep.Complete, false));
    }

    private async Task<List<string>> GetModelsForProviderAsync(AIProvider provider)
    {
        try
        {
            return provider switch
            {
                AIProvider.Claude => await _getClaudeModels(),
                AIProvider.Codex => await _getCodexModels(),
                AIProvider.Copilot => await _getCopilotModels(),
                AIProvider.Gemini => await _getGeminiModels(),
                AIProvider.Cursor => await _getCursorModels(),
                AIProvider.OpenCode => await _getOpenCodeModels(),
                AIProvider.Ollama when !string.IsNullOrEmpty(State.OllamaUrl) => await _getOllamaModels(State.OllamaUrl),
                _ => new List<string>()
            };
        }
        catch
        {
            return new List<string>();
        }
    }

    private static Task<bool> IsProviderInstalledAsync(AIProvider provider)
    {
        var command = provider switch
        {
            AIProvider.Claude => "claude",
            AIProvider.Codex => "codex",
            AIProvider.Copilot => "copilot",
            AIProvider.Gemini => "gemini",
            AIProvider.Cursor => "cursor",
            AIProvider.OpenCode => "opencode",
            AIProvider.Ollama => null,
            _ => null
        };

        if (command == null) return Task.FromResult(true);

        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c where {command}")
                : new System.Diagnostics.ProcessStartInfo("/bin/bash", $"-c \"which {command}\"");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var process = new System.Diagnostics.Process { StartInfo = psi };
            process.Start();
            process.WaitForExit(2000);

            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
