# Docker Installation Enhancement Spec

## Overview

This spec focuses on enhancing AspirePipelines with automatic Docker installation capabilities to ensure Docker is available on target servers before deployment.

## Current State

AspirePipelines currently only **verifies** that Docker is installed on the target server (see [Current Implementation](#current-implementation) in appendix).

**Problem**: If Docker is missing, deployment fails completely. User must manually install Docker on the target server.

## Proposed Enhancement

Add automatic Docker installation with user consent, following these principles:

1. **User Control**: Always prompt before installing Docker or making system changes
2. **Safety First**: Validate system compatibility and check existing state before installation
3. **Idempotency**: Operations should be safe to run multiple times without side effects
4. **Irreversible Operations**: Always confirm destructive or system-altering actions
5. **Graceful Fallback**: If auto-install fails, provide clear manual instructions
6. **Cross-Platform**: Support common Linux distributions

## Implementation Plan

### 1. Enhanced Docker Detection
The system will perform comprehensive Docker status detection including:
- Check if Docker binary is installed and accessible
- Verify Docker daemon is running and accessible  
- Detect permission issues that prevent Docker access
- Assess system compatibility for Docker installation

Implementation: See [Docker Detection Implementation](#docker-detection-implementation) in appendix.

### 2. Operating System Detection
Detect the target system's Linux distribution and version to determine:
- Whether Docker installation is supported
- Which installation method to use (get.docker.com script vs package manager)
- System-specific configuration requirements

Implementation: See [OS Detection Implementation](#os-detection-implementation) in appendix.

### 3. User Consent and Installation Strategy
Present users with contextual options based on their system state and privileges:
- **Docker not installed**: Offer automatic installation or manual setup
- **Docker installed but not operational**: Offer permission fixes or daemon restart
- **Multiple installation strategies**: Root with deployment user, sudo user, manual setup

Implementation: See [User Consent Implementation](#user-consent-implementation) in appendix.

### 4. Automatic Docker Installation (Idempotent)
Execute Docker installation with comprehensive safety checks:
- Pre-installation validation (disk space, internet connectivity, conflicts)
- Idempotent operations (safe to run multiple times)
- User permission configuration
- Service management (start/enable Docker daemon)
- Post-installation verification

Implementation: See [Docker Installation Implementation](#docker-installation-implementation) in appendix.

### 5. Pre-Execution Summary and Confirmation
Before executing any system modifications, present a comprehensive summary:
- **System Analysis**: OS, Docker status, user privileges
- **Planned Actions**: Every command with duration estimates and reversibility
- **Risk Assessment**: Identified risks with mitigation strategies  
- **Verification Plan**: Post-installation checks
- **Rollback Plan**: Cleanup procedures for failure scenarios

Implementation: See [Summary Implementation](#summary-implementation) in appendix.

### 6. Integration with Existing Pipeline
Update the `PrepareRemoteEnvironment` method to:
- Check Docker installation status comprehensively
- Prompt for installation if needed
- Handle different Docker states (missing, installed but not operational)
- Maintain compatibility with existing deployment flow

Implementation: See [Pipeline Integration](#pipeline-integration) in appendix.

## Supporting Data Structures

The implementation requires several data structures to manage installation state and user decisions. See [Data Structures](#data-structures) in appendix for complete definitions.

Key structures include:
- `DockerInstallationStatus`: Tracks Docker installation and operational state
- `SystemInfo`: Contains OS detection and compatibility information  
- `InstallationDecision`: Represents user choices and installation strategy
- `ExecutionPlan`: Comprehensive plan with actions, risks, and verification steps
- `UserPrivileges`: Current user's permission level and capabilities

## Benefits

1. **Improved User Experience**: No more failed deployments due to missing Docker
2. **Automation**: Reduces manual setup steps for new servers
3. **Safety**: User consent required before making system changes
4. **Flexibility**: Fallback to manual installation if auto-install isn't desired
5. **Compatibility**: Works with most common Linux distributions

## SSH User Strategy & Security Considerations

### The Challenge
Installing Docker requires root privileges, but deploying applications as root is a security anti-pattern. We need to balance automation with security best practices.

### Proposed Approach: Multi-User Strategy

#### Option 1: Root Installation + Application User (Recommended)
```
1. Connect as root (or sudo user) for Docker installation
2. Create dedicated deployment user
3. Add deployment user to docker group  
4. Reconnect as deployment user for application deployment
```

#### Option 2: Sudo User with Docker Installation
```
1. Connect as sudo-enabled user
2. Install Docker with sudo
3. Add current user to docker group
4. Continue deployment as same user
```

#### Option 3: Manual Setup (Existing Behavior)
```
1. User manually installs Docker
2. User manually creates deployment user
3. Pipeline connects as deployment user
```

### Implementation Plan

#### Enhanced SSH Context
```csharp
public class SSHConnectionContext
{
    // Current connection
    public required string TargetHost { get; set; }
    public required string SshUsername { get; set; }
    public string? SshPassword { get; set; }
    public string? SshKeyPath { get; set; }
    public required string SshPort { get; set; }
    public required string RemoteDeployPath { get; set; }
    
    // Installation privileges
    public bool HasRootAccess { get; set; }
    public bool HasSudoAccess { get; set; }
    public string? RootPassword { get; set; }
    public string? SudoPassword { get; set; }
    
    // Deployment user (may be different from installation user)
    public string? DeploymentUser { get; set; }
    public bool CreateDeploymentUser { get; set; }
}
```

#### User Privilege Detection
```csharp
private async Task<UserPrivileges> DetectUserPrivileges(CancellationToken cancellationToken)
{
    var privileges = new UserPrivileges();
    
    // Check if current user is root
    var whoamiResult = await ExecuteSSHCommandWithOutput("whoami", cancellationToken);
    privileges.IsRoot = whoamiResult.Output.Trim() == "root";
    
    if (!privileges.IsRoot)
    {
        // Check sudo access without password
        var sudoNoPassResult = await ExecuteSSHCommandWithOutput("sudo -n true", cancellationToken);
        privileges.HasPasswordlessSudo = sudoNoPassResult.ExitCode == 0;
        
        if (!privileges.HasPasswordlessSudo)
        {
            // Check if sudo is available at all
            var sudoAvailableResult = await ExecuteSSHCommandWithOutput("which sudo", cancellationToken);
            privileges.HasSudo = sudoAvailableResult.ExitCode == 0;
        }
    }
    
    return privileges;
}

public class UserPrivileges
{
    public bool IsRoot { get; set; }
    public bool HasSudo { get; set; }
    public bool HasPasswordlessSudo { get; set; }
    public bool CanInstallDocker => IsRoot || HasPasswordlessSudo;
}
```

#### Enhanced User Prompting
```csharp
private async Task<InstallationStrategy> PromptForInstallationStrategy(
    UserPrivileges privileges, 
    IInteractionService interactionService, 
    CancellationToken cancellationToken)
{
    var options = new List<KeyValuePair<string, string>>();
    
    if (privileges.CanInstallDocker)
    {
        if (privileges.IsRoot)
        {
            options.Add(new("root-with-user", "Install as root, create deployment user (Recommended)"));
            options.Add(new("root-direct", "Install and deploy as root (Not recommended)"));
        }
        else
        {
            options.Add(new("sudo-current", "Install with sudo, deploy as current user"));
            options.Add(new("sudo-with-user", "Install with sudo, create deployment user"));
        }
    }
    
    options.Add(new("manual", "Skip installation, I'll set up Docker manually"));
    options.Add(new("cancel", "Cancel deployment"));

    var inputs = new InteractionInput[]
    {
        new() {
            InputType = InputType.Choice,
            Label = "Installation Strategy",
            Options = options,
            Value = options.FirstOrDefault().Key
        }
    };

    var privilegeInfo = privileges.IsRoot ? "root user" :
                       privileges.HasPasswordlessSudo ? "sudo user (passwordless)" :
                       privileges.HasSudo ? "sudo user (password required)" :
                       "regular user (no sudo)";

    var result = await interactionService.PromptInputsAsync(
        "Docker Installation Strategy",
        $"""
        Docker installation requires elevated privileges.
        
        Current SSH user: {_currentSshUsername}
        Privilege level: {privilegeInfo}
        
        Security recommendation: Install Docker with elevated privileges, 
        then deploy applications as a non-root user.
        
        Choose your installation strategy:
        """,
        inputs,
        cancellationToken: cancellationToken
    );

    if (result.Canceled)
    {
        throw new InvalidOperationException("Installation strategy selection was canceled");
    }

    return ParseInstallationStrategy(result.Data[0].Value!);
}
```

#### Deployment User Creation
```csharp
private async Task CreateDeploymentUser(string username, IPublishingStep step, CancellationToken cancellationToken)
{
    await using var userTask = await step.CreateTaskAsync($"Setting up deployment user: {username}", cancellationToken);
    
    try
    {
        // Check if user already exists (idempotency)
        var userExistsCheck = await ExecuteSSHCommandWithOutput($"id {username}", cancellationToken);
        if (userExistsCheck.ExitCode == 0)
        {
            await userTask.UpdateAsync($"User {username} already exists, checking configuration...", cancellationToken);
            
            // Verify user is in docker group
            var groupCheck = await ExecuteSSHCommandWithOutput($"groups {username}", cancellationToken);
            if (!groupCheck.Output.Contains("docker"))
            {
                await userTask.UpdateAsync($"Adding {username} to docker group...", cancellationToken);
                await ExecuteSSHCommand($"sudo usermod -aG docker {username}", cancellationToken);
            }
            
            await userTask.SucceedAsync($"Deployment user {username} configured");
            return;
        }

        // Confirm user creation (irreversible action)
        var shouldCreate = await ConfirmIrreversibleAction(
            $"Create new system user '{username}'",
            new List<string>
            {
                $"User '{username}' will be created with home directory",
                "User will be added to 'docker' group",
                "SSH access will be configured for this user",
                "User creation cannot be automatically undone"
            },
            _interactionService,
            cancellationToken);

        if (!shouldCreate)
        {
            await userTask.FailAsync("User creation canceled");
            throw new InvalidOperationException($"Deployment user creation was canceled");
        }

        // Create user with home directory
        await userTask.UpdateAsync($"Creating user {username}...", cancellationToken);
        var createUserResult = await ExecuteSSHCommandWithOutput(
            $"sudo useradd -m -s /bin/bash {username}", cancellationToken);
            
        if (createUserResult.ExitCode != 0)
        {
            await userTask.FailAsync($"Failed to create user: {createUserResult.Error}");
            throw new InvalidOperationException($"Failed to create user {username}: {createUserResult.Error}");
        }
        
        // Add user to docker group
        await userTask.UpdateAsync($"Adding {username} to docker group...", cancellationToken);
        await ExecuteSSHCommand($"sudo usermod -aG docker {username}", cancellationToken);
        
        // Set up SSH access for the new user
        await userTask.UpdateAsync($"Configuring SSH access for {username}...", cancellationToken);
        await SetupSSHKeyForUser(username, cancellationToken);
        
        // Verify user can log in and run Docker
        await userTask.UpdateAsync("Verifying user configuration...", cancellationToken);
        var canLogin = await VerifyUserCanLogin(username, cancellationToken);
        if (!canLogin)
        {
            await userTask.WarnAsync($"User {username} created but SSH access verification failed");
        }
        
        await userTask.SucceedAsync($"Deployment user {username} created and configured");
    }
    catch (Exception ex)
    {
        await userTask.FailAsync($"Failed to setup deployment user: {ex.Message}");
        
        // Attempt cleanup of partially created user
        await CleanupPartialUserCreation(username, cancellationToken);
        throw;
    }
}

private async Task CleanupPartialUserCreation(string username, CancellationToken cancellationToken)
{
    try
    {
        // Check if user was created
        var userCheck = await ExecuteSSHCommandWithOutput($"id {username}", cancellationToken);
        if (userCheck.ExitCode == 0)
        {
            // Confirm cleanup with user
            var shouldCleanup = await ConfirmIrreversibleAction(
                $"Remove partially created user '{username}'",
                new List<string>
                {
                    $"User '{username}' and home directory will be deleted",
                    "Any files created for this user will be lost"
                },
                _interactionService,
                CancellationToken.None);

            if (shouldCleanup)
            {
                await ExecuteSSHCommand($"sudo userdel -r {username}", CancellationToken.None);
            }
        }
    }
    catch
    {
        // Cleanup is best-effort, don't throw from cleanup
    }
}

private async Task<bool> VerifyUserCanLogin(string username, CancellationToken cancellationToken)
{
    try
    {
        // This would require establishing a new SSH connection as the new user
        // For now, we'll just verify the user exists and has proper group membership
        var groupCheck = await ExecuteSSHCommandWithOutput($"groups {username}", cancellationToken);
        return groupCheck.ExitCode == 0 && groupCheck.Output.Contains("docker");
    }
    catch
    {
        return false;
    }
}

private async Task SetupSSHKeyForUser(string username, CancellationToken cancellationToken)
{
    // Option 1: Copy current user's SSH setup
    if (!string.IsNullOrEmpty(_currentSshKeyPath))
    {
        var publicKeyPath = _currentSshKeyPath.Replace(".pem", ".pub").Replace("_rsa", "_rsa.pub");
        if (File.Exists(publicKeyPath))
        {
            var publicKey = await File.ReadAllTextAsync(publicKeyPath);
            await ExecuteSSHCommand($"sudo mkdir -p /home/{username}/.ssh", cancellationToken);
            await ExecuteSSHCommand($"echo '{publicKey}' | sudo tee /home/{username}/.ssh/authorized_keys", cancellationToken);
            await ExecuteSSHCommand($"sudo chown -R {username}:{username} /home/{username}/.ssh", cancellationToken);
            await ExecuteSSHCommand($"sudo chmod 700 /home/{username}/.ssh", cancellationToken);
            await ExecuteSSHCommand($"sudo chmod 600 /home/{username}/.ssh/authorized_keys", cancellationToken);
            return;
        }
    }
    
    // Option 2: Use password authentication (prompt user)
    // This would require additional prompting for password setup
}
```

#### Multi-Connection Strategy
```csharp
private async Task<SSHConnectionContext> EstablishDeploymentConnection(
    InstallationStrategy strategy, 
    CancellationToken cancellationToken)
{
    if (strategy.RequiresNewConnection)
    {
        // Close current connection
        await CleanupSSHConnection();
        
        // Establish new connection as deployment user
        var deploymentContext = new SSHConnectionContext
        {
            TargetHost = _currentContext.TargetHost,
            SshUsername = strategy.DeploymentUser!,
            SshKeyPath = _currentContext.SshKeyPath, // Reuse same key if possible
            SshPort = _currentContext.SshPort,
            RemoteDeployPath = _currentContext.RemoteDeployPath
        };
        
        await EstablishSSHConnection(deploymentContext, cancellationToken);
        return deploymentContext;
    }
    
    return _currentContext; // Use existing connection
}
```

### Configuration Options

Add to `DockerSSHConfiguration`:
```csharp
public class DockerSSHConfiguration
{
    // ...existing properties...
    
    // Installation strategy
    public string? InstallationStrategy { get; set; } // "root-with-user", "sudo-current", etc.
    public string? DeploymentUsername { get; set; } = "aspire-deploy";
    public bool CreateDeploymentUser { get; set; } = true;
    
    // Root/sudo credentials (if needed)
    public string? RootPassword { get; set; }
    public string? SudoPassword { get; set; }
}
```

## Idempotency and Safety Guarantees

### Core Principles

1. **Read Before Write**: Always check current state before making changes
2. **Fail Safe**: Prefer failing over making unexpected changes  
3. **User Confirmation**: Never make irreversible changes without explicit consent
4. **Atomic Operations**: Either complete fully or rollback cleanly
5. **Repeatable**: Safe to run multiple times with same result

### Idempotent Operations

#### Docker Installation
```csharp
// ✅ Safe to run multiple times
await InstallDocker(systemInfo, step, cancellationToken);

// Behavior:
// - If Docker already installed and operational → Skip installation
// - If Docker installed but not operational → Fix configuration only
// - If Docker not installed → Install and configure
// - If installation fails → Clean rollback with clear error message
```

#### User Management
```csharp
// ✅ Safe to run multiple times  
await AddUserToDockerGroup(username, task, cancellationToken);

// Behavior:
// - Check if user already in docker group → Skip if already member
// - Add user to group → Only if not already a member
// - Report status → Clear indication of action taken or skipped
```

#### Service Management
```csharp
// ✅ Safe to run multiple times
await EnsureDockerServiceRunning(task, cancellationToken);

// Behavior:
// - Check service status → Only start if not already running
// - Enable auto-start → Only if not already enabled
// - Verify operation → Confirm service is actually running
```

### Safety Checks

#### Pre-Installation Validation
```csharp
private async Task<ValidationResult> ValidateInstallationSafety(CancellationToken cancellationToken)
{
    var checks = new List<SafetyCheck>();
#### Docker Installation
See [Appendix A.1: Idempotent Docker Installation](#appendix-a1-idempotent-docker-installation)
    var connectivity = await ExecuteSSHCommandWithOutput("curl -sSf --connect-timeout 5 https://get.docker.com", cancellationToken);
#### User Management
See [Appendix A.2: Idempotent User Management](#appendix-a2-idempotent-user-management)
    {
#### Service Management
See [Appendix A.3: Idempotent Service Management](#appendix-a3-idempotent-service-management)
# Appendix: Implementation Code

---

## Appendix A.1: Idempotent Docker Installation

```csharp
// ✅ Safe to run multiple times
await InstallDocker(systemInfo, step, cancellationToken);

// Behavior:
// - If Docker already installed and operational → Skip installation
// - If Docker installed but not operational → Fix configuration only
// - If Docker not installed → Install and configure
// - If installation fails → Clean rollback with clear error message
```

## Appendix A.2: Idempotent User Management

```csharp
// ✅ Safe to run multiple times  
await AddUserToDockerGroup(username, task, cancellationToken);

// Behavior:
// - Check if user already in docker group → Skip if already member
// - Add user to group → Only if not already a member
// - Report status → Clear indication of action taken or skipped
```

## Appendix A.3: Idempotent Service Management

```csharp
// ✅ Safe to run multiple times
await EnsureDockerServiceRunning(task, cancellationToken);

// Behavior:
// - Check service status → Only start if not already running
// - Enable auto-start → Only if not already enabled
```
        Name = "System Status",
        Passed = maintenanceCheck.Output.Contains("running") || maintenanceCheck.Output.Contains("degraded"),
        Details = maintenanceCheck.Output.Trim()
    });
    
    return new ValidationResult { Checks = checks };
}
```

#### Irreversible Operation Confirmation
```csharp
private async Task<bool> ConfirmIrreversibleAction(
    string actionDescription,
    List<string> consequences,
    IInteractionService interactionService,
    CancellationToken cancellationToken)
{
    var inputs = new InteractionInput[]
    {
        new() {
            InputType = InputType.Choice,
            Label = "Confirm Action",
            Options = new List<KeyValuePair<string, string>>
            {
                new("yes", "Yes, I understand the consequences"),
                new("no", "No, cancel this action")
            },
            Value = "no" // Default to safe option
        }
    };

    var consequencesList = consequences.Count > 0 
        ? "\n\nConsequences:\n" + string.Join("\n", consequences.Select(c => $"• {c}"))
        : "";

    var result = await interactionService.PromptInputsAsync(
        "⚠️ Confirm Irreversible Action",
        $"""
        You are about to: {actionDescription}
        
        This action cannot be automatically undone.{consequencesList}
        
        Are you sure you want to proceed?
        """,
        inputs,
        cancellationToken: cancellationToken
    );

    return !result.Canceled && result.Data[0].Value == "yes";
}
```

### Rollback Strategy

#### Installation Failure Recovery
```csharp
private async Task HandleInstallationFailure(
    Exception installException, 
    IPublishingStep step,
    CancellationToken cancellationToken)
{
    await using var cleanupTask = await step.CreateTaskAsync("Cleaning up failed installation", cancellationToken);
    
    try
    {
        // Remove any partially installed Docker components
        var cleanupCommands = new[]
        {
            "sudo systemctl stop docker 2>/dev/null || true",
            "sudo systemctl disable docker 2>/dev/null || true", 
            "sudo apt-get remove -y docker-ce docker-ce-cli containerd.io 2>/dev/null || true",
            "sudo yum remove -y docker-ce docker-ce-cli containerd.io 2>/dev/null || true"
        };
        
        foreach (var command in cleanupCommands)
        {
            await ExecuteSSHCommandWithOutput(command, cancellationToken);
        }
        
        await cleanupTask.SucceedAsync("Cleanup completed");
    }
    catch (Exception cleanupException)
    {
        await cleanupTask.WarnAsync($"Cleanup partially failed: {cleanupException.Message}");
    }
    
    // Provide clear manual recovery instructions
    var instructions = GenerateManualInstallationInstructions();
    throw new InvalidOperationException(
        $"Docker installation failed: {installException.Message}\n\n" +
        $"Manual installation instructions:\n{instructions}");
}
```

### State Verification

#### Post-Operation Verification
```csharp
private async Task<bool> VerifyExpectedState(
    DockerInstallationStatus expectedStatus,
    IPublishingStep step,
    CancellationToken cancellationToken)
{
    await using var verifyTask = await step.CreateTaskAsync("Verifying system state", cancellationToken);
    
    var currentStatus = await CheckDockerInstallation(step, cancellationToken);
    
    var verifications = new[]
    {
        ("Docker Installed", currentStatus.IsInstalled == expectedStatus.IsInstalled),
        ("Docker Operational", currentStatus.IsOperational == expectedStatus.IsOperational),
        ("User Permissions", await VerifyUserCanRunDocker(cancellationToken))
    };
    
    var allPassed = verifications.All(v => v.Item2);
    var results = string.Join(", ", verifications.Select(v => $"{v.Item1}: {(v.Item2 ? "✅" : "❌")}"));
    
    if (allPassed)
    {
        await verifyTask.SucceedAsync($"State verified: {results}");
    }
    else
    {
        await verifyTask.FailAsync($"State verification failed: {results}");
    }
    
    return allPassed;
}

private async Task<bool> VerifyUserCanRunDocker(CancellationToken cancellationToken)
{
    // Test that current user can actually run Docker commands
    var testResult = await ExecuteSSHCommandWithOutput("docker version --format '{{.Client.Version}}'", cancellationToken);
    return testResult.ExitCode == 0;
}
```

### Configuration Changes

#### Tracked Configuration Changes
```csharp
public class ConfigurationChange
{
    public string Description { get; set; } = "";
    public string Command { get; set; } = "";
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public bool IsReversible { get; set; }
    public string? RollbackCommand { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

private readonly List<ConfigurationChange> _appliedChanges = new();

private async Task TrackConfigurationChange(
    string description,
    string command,
    string? rollbackCommand = null,
    CancellationToken cancellationToken = default)
{
    // Capture state before change
    var beforeState = await CaptureRelevantState(cancellationToken);
    
    // Apply change
    var result = await ExecuteSSHCommandWithOutput(command, cancellationToken);
    
    if (result.ExitCode == 0)
    {
        var change = new ConfigurationChange
        {
            Description = description,
            Command = command,
            PreviousValue = beforeState,
            IsReversible = rollbackCommand != null,
            RollbackCommand = rollbackCommand
        };
        
        _appliedChanges.Add(change);
    }
}
```

### Error Recovery

#### Graceful Degradation
```csharp
private async Task<DockerInstallationResult> InstallDockerWithGracefulFallback(
    SystemInfo systemInfo,
    IPublishingStep step,
    CancellationToken cancellationToken)
{
    try
    {
        // Attempt automatic installation
        await InstallDocker(systemInfo, step, cancellationToken);
        return new DockerInstallationResult { Success = true, Method = "Automatic" };
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Log the failure but don't fail the deployment immediately
        await step.WarnAsync($"Automatic Docker installation failed: {ex.Message}");
        
        // Offer alternative approaches
        var fallbackOptions = new[]
        {
            "Generate manual installation script",
            "Provide step-by-step instructions", 
            "Skip Docker installation (manual setup required)",
            "Cancel deployment"
        };
        
        // ... prompt user for fallback approach
        
        return new DockerInstallationResult 
        { 
            Success = false, 
            Method = "Manual", 
            RequiresManualSteps = true,
            Instructions = GenerateManualInstructions(systemInfo)
        };
    }
}
```

## Questions for Decision

1. **Default Strategy**: Should we default to creating a deployment user, or use the current user?
   
2. **User Naming**: Should we use a fixed name like `aspire-deploy` or prompt for the deployment username?

3. **SSH Key Handling**: Should we:
   - Copy the current user's SSH key to the deployment user?
   - Generate a new SSH key pair for the deployment user?
   - Prompt the user to provide a separate key for the deployment user?

4. **Root Password**: If connecting as non-root, should we:
   - Prompt for sudo password if needed?
   - Require passwordless sudo?
   - Allow connecting as root with separate credentials?

5. **Fallback Strategy**: If user declines auto-installation, should we:
   - Provide step-by-step manual instructions?
   - Generate a setup script they can run?
   - Just fail with a basic error message?

6. **Connection Persistence**: Should we:
   - Always reconnect as deployment user for consistency?
   - Keep the installation connection if it's suitable for deployment?
   - Offer both options?

### Recommended Defaults

Based on security best practices, I recommend:

- **Default to creating a deployment user** (`aspire-deploy`)
- **Copy SSH keys** from installation user to deployment user
- **Require passwordless sudo** for non-root installation users
- **Always reconnect** as deployment user for application deployment
- **Provide detailed manual instructions** if auto-installation is declined

This approach maximizes security while maintaining reasonable automation.

## Pre-Execution Summary and Confirmation

### Summary Phase Implementation

Before executing any Docker installation or system modifications, the system will present a comprehensive summary of all planned actions for user review and final confirmation.

```csharp
private async Task<ExecutionPlan> PlanDockerInstallation(
    DockerInstallationStatus dockerStatus,
    UserPrivileges privileges,
    InstallationStrategy strategy,
    CancellationToken cancellationToken)
{
    var plan = new ExecutionPlan();
    
    // Phase 1: Analysis Summary
    plan.SystemAnalysis = new SystemAnalysisSummary
    {
        OperatingSystem = dockerStatus.SystemInfo?.Description ?? "Unknown",
        CurrentDockerStatus = GetDockerStatusDescription(dockerStatus),
        UserPrivileges = GetPrivilegeDescription(privileges),
        InstallationMethod = strategy.Method.ToString()
    };
    
    // Phase 2: Planned Actions
    if (!dockerStatus.IsInstalled)
    {
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.InstallSoftware,
            Description = "Install Docker using get.docker.com script",
            Command = "curl -fsSL https://get.docker.com | sh",
            IsReversible = true,
            RollbackDescription = "Uninstall Docker packages and clean up configuration",
            EstimatedDuration = TimeSpan.FromMinutes(5),
            RequiresInternet = true,
            RequiresPrivileges = true
        });
    }
    
    if (strategy.CreateDeploymentUser)
    {
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.CreateUser,
            Description = $"Create deployment user '{strategy.DeploymentUsername}'",
            Command = $"sudo useradd -m -s /bin/bash {strategy.DeploymentUsername}",
            IsReversible = true,
            RollbackDescription = $"Remove user and home directory: sudo userdel -r {strategy.DeploymentUsername}",
            EstimatedDuration = TimeSpan.FromSeconds(30),
            RequiresPrivileges = true
        });
        
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.ConfigurePermissions,
            Description = $"Add {strategy.DeploymentUsername} to docker group",
            Command = $"sudo usermod -aG docker {strategy.DeploymentUsername}",
            IsReversible = true,
            RollbackDescription = $"Remove from docker group: sudo gpasswd -d {strategy.DeploymentUsername} docker",
            EstimatedDuration = TimeSpan.FromSeconds(10),
            RequiresPrivileges = true
        });
        
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.ConfigureSSH,
            Description = $"Configure SSH access for {strategy.DeploymentUsername}",
            Command = "Configure SSH keys and authorized_keys",
            IsReversible = true,
            RollbackDescription = "SSH configuration will be removed with user deletion",
            EstimatedDuration = TimeSpan.FromSeconds(20),
            RequiresPrivileges = true
        });
    }
    else if (dockerStatus.RequiresPermissionFix)
    {
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.ConfigurePermissions,
            Description = $"Add current user to docker group",
            Command = $"sudo usermod -aG docker {privileges.CurrentUsername}",
            IsReversible = true,
            RollbackDescription = $"Remove from docker group: sudo gpasswd -d {privileges.CurrentUsername} docker",
            EstimatedDuration = TimeSpan.FromSeconds(10),
            RequiresPrivileges = true
        });
    }
    
    if (!dockerStatus.IsOperational || plan.PlannedActions.Any(a => a.Type == ActionType.InstallSoftware))
    {
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.StartService,
            Description = "Start and enable Docker service",
            Command = "sudo systemctl start docker && sudo systemctl enable docker",
            IsReversible = true,
            RollbackDescription = "Stop and disable Docker service",
            EstimatedDuration = TimeSpan.FromSeconds(30),
            RequiresPrivileges = true
        });
    }
    
    if (strategy.RequiresReconnection)
    {
        plan.PlannedActions.Add(new PlannedAction
        {
            Type = ActionType.ReconnectSSH,
            Description = $"Reconnect as deployment user '{strategy.DeploymentUsername}'",
            Command = $"SSH reconnection to {strategy.DeploymentUsername}@{dockerStatus.SystemInfo?.Description}",
            IsReversible = false,
            RollbackDescription = "Can reconnect as original user if needed",
            EstimatedDuration = TimeSpan.FromSeconds(15),
            RequiresPrivileges = false
        });
    }
    
    // Phase 3: Risk Assessment
    plan.RiskAssessment = AssessRisks(plan.PlannedActions, dockerStatus, privileges);
    
    // Phase 4: Verification Steps
    plan.VerificationSteps = new List<VerificationStep>
    {
        new() { Description = "Verify Docker installation", Command = "docker --version" },
        new() { Description = "Verify Docker daemon accessibility", Command = "docker info" },
        new() { Description = "Test container execution", Command = "docker run --rm hello-world" }
    };
    
    if (strategy.CreateDeploymentUser)
    {
        plan.VerificationSteps.Add(new VerificationStep 
        { 
            Description = $"Verify {strategy.DeploymentUsername} can run Docker", 
            Command = $"sudo -u {strategy.DeploymentUsername} docker version" 
        });
    }
    
    return plan;
}

private async Task<bool> PresentExecutionSummary(
    ExecutionPlan plan,
    IInteractionService interactionService,
    CancellationToken cancellationToken)
{
    var summaryText = GenerateExecutionSummary(plan);
    
    var inputs = new InteractionInput[]
    {
        new() {
            InputType = InputType.Choice,
            Label = "Proceed with Installation",
            Options = new List<KeyValuePair<string, string>>
            {
                new("proceed", "✅ Yes, proceed with the installation plan"),
                new("modify", "🔧 Modify the plan (go back to options)"),
                new("cancel", "❌ Cancel installation")
            },
            Value = "proceed"
        }
    };

    var result = await interactionService.PromptInputsAsync(
        "🔍 Docker Installation Plan Summary",
        summaryText,
        inputs,
        cancellationToken: cancellationToken
    );

    if (result.Canceled || result.Data[0].Value == "cancel")
    {
        throw new InvalidOperationException("Docker installation canceled by user");
    }

    return result.Data[0].Value == "proceed";
}

private string GenerateExecutionSummary(ExecutionPlan plan)
{
    var sb = new StringBuilder();
    
    // Header
    sb.AppendLine("## 📋 Installation Plan Summary");
    sb.AppendLine();
    
    // System Analysis
    sb.AppendLine("### 🖥️ System Analysis");
    sb.AppendLine($"• **Operating System**: {plan.SystemAnalysis.OperatingSystem}");
    sb.AppendLine($"• **Current Docker Status**: {plan.SystemAnalysis.CurrentDockerStatus}");
    sb.AppendLine($"• **User Privileges**: {plan.SystemAnalysis.UserPrivileges}");
    sb.AppendLine($"• **Installation Method**: {plan.SystemAnalysis.InstallationMethod}");
    sb.AppendLine();
    
    // Planned Actions
    sb.AppendLine("### 🔧 Planned Actions");
    var totalDuration = TimeSpan.Zero;
    
    for (int i = 0; i < plan.PlannedActions.Count; i++)
    {
        var action = plan.PlannedActions[i];
        totalDuration = totalDuration.Add(action.EstimatedDuration);
        
        sb.AppendLine($"**{i + 1}. {action.Description}**");
        sb.AppendLine($"   • Command: `{action.Command}`");
        sb.AppendLine($"   • Duration: ~{FormatDuration(action.EstimatedDuration)}");
        sb.AppendLine($"   • Reversible: {(action.IsReversible ? "✅ Yes" : "❌ No")}");
        
        if (action.IsReversible)
        {
            sb.AppendLine($"   • Rollback: {action.RollbackDescription}");
        }
        
        var requirements = new List<string>();
        if (action.RequiresInternet) requirements.Add("Internet");
        if (action.RequiresPrivileges) requirements.Add("Root/Sudo");
        if (requirements.Count > 0)
        {
            sb.AppendLine($"   • Requires: {string.Join(", ", requirements)}");
        }
        sb.AppendLine();
    }
    
    sb.AppendLine($"**⏱️ Total Estimated Time**: ~{FormatDuration(totalDuration)}");
    sb.AppendLine();
    
    // Risk Assessment
    sb.AppendLine("### ⚠️ Risk Assessment");
    foreach (var risk in plan.RiskAssessment.Risks)
    {
        var riskIcon = risk.Level switch
        {
            RiskLevel.Low => "🟢",
            RiskLevel.Medium => "🟡", 
            RiskLevel.High => "🔴",
            _ => "⚪"
        };
        
        sb.AppendLine($"{riskIcon} **{risk.Level}**: {risk.Description}");
        if (!string.IsNullOrEmpty(risk.Mitigation))
        {
            sb.AppendLine($"   • Mitigation: {risk.Mitigation}");
        }
    }
    sb.AppendLine();
    
    // Verification Plan
    sb.AppendLine("### ✅ Post-Installation Verification");
    sb.AppendLine("After installation, the following checks will be performed:");
    
    for (int i = 0; i < plan.VerificationSteps.Count; i++)
    {
        var step = plan.VerificationSteps[i];
        sb.AppendLine($"{i + 1}. {step.Description} (`{step.Command}`)");
    }
    sb.AppendLine();
    
    // Rollback Plan
    var reversibleActions = plan.PlannedActions.Where(a => a.IsReversible).ToList();
    if (reversibleActions.Count > 0)
    {
        sb.AppendLine("### 🔄 Rollback Plan");
        sb.AppendLine("If installation fails, the following cleanup will be attempted:");
        
        for (int i = reversibleActions.Count - 1; i >= 0; i--)
        {
            var action = reversibleActions[i];
            sb.AppendLine($"• {action.RollbackDescription}");
        }
        sb.AppendLine();
    }
    
    // Final Confirmation
    sb.AppendLine("### 🤔 Ready to Proceed?");
    sb.AppendLine("Please review the plan above carefully. All irreversible actions have been identified.");
    sb.AppendLine("You can modify the plan by going back, or proceed with the installation.");
    
    return sb.ToString();
}

private RiskAssessment AssessRisks(List<PlannedAction> actions, DockerInstallationStatus dockerStatus, UserPrivileges privileges)
{
    var risks = new List<Risk>();
    
    // Assess privilege escalation risks
    if (actions.Any(a => a.RequiresPrivileges) && !privileges.IsRoot)
    {
        risks.Add(new Risk
        {
            Level = RiskLevel.Medium,
            Description = "Requires sudo privileges for system modifications",
            Mitigation = "Commands will be executed with sudo only when necessary"
        });
    }
    
    // Assess user creation risks
    if (actions.Any(a => a.Type == ActionType.CreateUser))
    {
        risks.Add(new Risk
        {
            Level = RiskLevel.Low,
            Description = "Creating new system user account",
            Mitigation = "User can be removed if deployment is canceled"
        });
    }
    
    // Assess internet dependency
    if (actions.Any(a => a.RequiresInternet))
    {
        risks.Add(new Risk
        {
            Level = RiskLevel.Low,
            Description = "Requires internet access for Docker installation",
            Mitigation = "Installation will fail gracefully if connection is lost"
        });
    }
    
    // Assess service modification risks
    if (actions.Any(a => a.Type == ActionType.StartService))
    {
        risks.Add(new Risk
        {
            Level = RiskLevel.Low,
            Description = "Will start and enable Docker system service",
            Mitigation = "Service can be stopped and disabled if needed"
        });
    }
    
    // Assess SSH reconnection risks
    if (actions.Any(a => a.Type == ActionType.ReconnectSSH))
    {
        risks.Add(new Risk
        {
            Level = RiskLevel.Medium,
            Description = "Will reconnect SSH as different user",
            Mitigation = "Original connection details are preserved for fallback"
        });
    }
    
    return new RiskAssessment
    {
        OverallRisk = risks.Any(r => r.Level == RiskLevel.High) ? RiskLevel.High :
                     risks.Any(r => r.Level == RiskLevel.Medium) ? RiskLevel.Medium : RiskLevel.Low,
        Risks = risks
    };
}

private string FormatDuration(TimeSpan duration)
{
    if (duration.TotalMinutes >= 1)
        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    else
        return $"{duration.Seconds}s";
}

private string GetDockerStatusDescription(DockerInstallationStatus status)
{
    if (!status.IsInstalled)
        return "Not installed";
    if (!status.IsOperational)
        return status.RequiresPermissionFix ? "Installed but permission denied" : "Installed but not running";
    return $"Fully operational ({status.Version})";
}

private string GetPrivilegeDescription(UserPrivileges privileges)
{
    if (privileges.IsRoot)
        return "Root user";
    if (privileges.HasPasswordlessSudo)
        return "Sudo user (passwordless)";
    if (privileges.HasSudo)
        return "Sudo user (password required)";
    return "Regular user (no sudo)";
}
```

### Supporting Data Structures for Summary

```csharp
public class ExecutionPlan
{
    public SystemAnalysisSummary SystemAnalysis { get; set; } = new();
    public List<PlannedAction> PlannedActions { get; set; } = new();
    public RiskAssessment RiskAssessment { get; set; } = new();
    public List<VerificationStep> VerificationSteps { get; set; } = new();
}

public class SystemAnalysisSummary
{
    public string OperatingSystem { get; set; } = "";
    public string CurrentDockerStatus { get; set; } = "";
    public string UserPrivileges { get; set; } = "";
    public string InstallationMethod { get; set; } = "";
}

public class PlannedAction
{
    public ActionType Type { get; set; }
    public string Description { get; set; } = "";
    public string Command { get; set; } = "";
    public bool IsReversible { get; set; }
    public string RollbackDescription { get; set; } = "";
    public TimeSpan EstimatedDuration { get; set; }
    public bool RequiresInternet { get; set; }
    public bool RequiresPrivileges { get; set; }
}

public enum ActionType
{
    InstallSoftware,
    CreateUser,
    ConfigurePermissions,
    ConfigureSSH,
    StartService,
    ReconnectSSH
}

public class RiskAssessment
{
    public RiskLevel OverallRisk { get; set; }
    public List<Risk> Risks { get; set; } = new();
}

public class Risk
{
    public RiskLevel Level { get; set; }
    public string Description { get; set; } = "";
    public string Mitigation { get; set; } = "";
}

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public class VerificationStep
{
    public string Description { get; set; } = "";
    public string Command { get; set; } = "";
}
```

### Enhanced Installation Flow with Summary

```csharp
private async Task ExecuteDockerInstallationWithSummary(
    DockerInstallationStatus dockerStatus,
    IPublishingStep step,
    CancellationToken cancellationToken)
{
    // Step 1: Gather all decisions and user preferences
    var privileges = await DetectUserPrivileges(cancellationToken);
    var strategy = await PromptForInstallationStrategy(privileges, dockerStatus, _interactionService, cancellationToken);
    
    // Step 2: Create comprehensive execution plan
    var plan = await PlanDockerInstallation(dockerStatus, privileges, strategy, cancellationToken);
    
    // Step 3: Present summary and get final confirmation
    var shouldProceed = await PresentExecutionSummary(plan, _interactionService, cancellationToken);
    
    if (!shouldProceed)
    {
        throw new InvalidOperationException("Installation canceled after plan review");
    }
    
    // Step 4: Execute the plan with progress tracking
    await ExecutePlanWithProgress(plan, step, cancellationToken);
    
    // Step 5: Verify all expected outcomes
    await VerifyInstallationSuccess(plan, step, cancellationToken);
}

private async Task ExecutePlanWithProgress(ExecutionPlan plan, IPublishingStep step, CancellationToken cancellationToken)
{
    await using var executionTask = await step.CreateTaskAsync("Executing Docker installation plan", cancellationToken);
    
    var completedActions = new List<PlannedAction>();
    
    try
    {
        for (int i = 0; i < plan.PlannedActions.Count; i++)
        {
            var action = plan.PlannedActions[i];
            var progress = $"[{i + 1}/{plan.PlannedActions.Count}]";
            
            await executionTask.UpdateAsync($"{progress} {action.Description}...", cancellationToken);
            
            // Execute the specific action based on type
            await ExecuteSpecificAction(action, executionTask, cancellationToken);
            
            completedActions.Add(action);
            
            await executionTask.UpdateAsync($"{progress} ✅ {action.Description} completed", cancellationToken);
        }
        
        await executionTask.SucceedAsync($"All {plan.PlannedActions.Count} actions completed successfully");
    }
    catch (Exception ex)
    {
        await executionTask.FailAsync($"Failed at action: {ex.Message}");
        
        // Attempt rollback of completed actions
        await RollbackCompletedActions(completedActions, step, cancellationToken);
        throw;
    }
}

private async Task RollbackCompletedActions(List<PlannedAction> completedActions, IPublishingStep step, CancellationToken cancellationToken)
{
    if (completedActions.Count == 0) return;
    
    await using var rollbackTask = await step.CreateTaskAsync("Rolling back completed actions", cancellationToken);
    
    // Execute rollback in reverse order
    for (int i = completedActions.Count - 1; i >= 0; i--)
    {
        var action = completedActions[i];
        if (action.IsReversible)
        {
            try
            {
                await rollbackTask.UpdateAsync($"Rolling back: {action.Description}", cancellationToken);
                await ExecuteRollbackAction(action, cancellationToken);
            }
            catch (Exception rollbackEx)
            {
                await rollbackTask.WarnAsync($"Rollback failed for {action.Description}: {rollbackEx.Message}", cancellationToken);
            }
        }
    }
    
    await rollbackTask.SucceedAsync("Rollback completed");
}
```

### Example Summary Output

When a user chooses to install Docker with a deployment user, they would see something like this:

```
🔍 Docker Installation Plan Summary

## 📋 Installation Plan Summary

### 🖥️ System Analysis
• Operating System: Ubuntu 22.04.3 LTS
• Current Docker Status: Not installed
• User Privileges: Sudo user (passwordless)
• Installation Method: RootWithUser

### 🔧 Planned Actions

1. Install Docker using get.docker.com script
   • Command: curl -fsSL https://get.docker.com | sh
   • Duration: ~5m 0s
   • Reversible: ✅ Yes
   • Rollback: Uninstall Docker packages and clean up configuration
   • Requires: Internet, Root/Sudo

2. Create deployment user 'aspire-deploy'
   • Command: sudo useradd -m -s /bin/bash aspire-deploy
   • Duration: ~30s
   • Reversible: ✅ Yes
   • Rollback: Remove user and home directory: sudo userdel -r aspire-deploy
   • Requires: Root/Sudo

3. Add aspire-deploy to docker group
   • Command: sudo usermod -aG docker aspire-deploy
   • Duration: ~10s
   • Reversible: ✅ Yes
   • Rollback: Remove from docker group: sudo gpasswd -d aspire-deploy docker
   • Requires: Root/Sudo

4. Configure SSH access for aspire-deploy
   • Command: Configure SSH keys and authorized_keys
   • Duration: ~20s
   • Reversible: ✅ Yes
   • Rollback: SSH configuration will be removed with user deletion
   • Requires: Root/Sudo

5. Start and enable Docker service
   • Command: sudo systemctl start docker && sudo systemctl enable docker
   • Duration: ~30s
   • Reversible: ✅ Yes
   • Rollback: Stop and disable Docker service
   • Requires: Root/Sudo

6. Reconnect as deployment user 'aspire-deploy'
   • Command: SSH reconnection to aspire-deploy@Ubuntu 22.04.3 LTS
   • Duration: ~15s
   • Reversible: ❌ No
   • Rollback: Can reconnect as original user if needed

⏱️ Total Estimated Time: ~6m 45s

### ⚠️ Risk Assessment
🟡 Medium: Requires sudo privileges for system modifications
   • Mitigation: Commands will be executed with sudo only when necessary
🟢 Low: Creating new system user account
   • Mitigation: User can be removed if deployment is canceled
🟢 Low: Requires internet access for Docker installation
   • Mitigation: Installation will fail gracefully if connection is lost
🟢 Low: Will start and enable Docker system service
   • Mitigation: Service can be stopped and disabled if needed
🟡 Medium: Will reconnect SSH as different user
   • Mitigation: Original connection details are preserved for fallback

### ✅ Post-Installation Verification
After installation, the following checks will be performed:
1. Verify Docker installation (docker --version)
2. Verify Docker daemon accessibility (docker info)
3. Test container execution (docker run --rm hello-world)
4. Verify aspire-deploy can run Docker (sudo -u aspire-deploy docker version)

### 🔄 Rollback Plan
If installation fails, the following cleanup will be attempted:
• SSH configuration will be removed with user deletion
• Remove from docker group: sudo gpasswd -d aspire-deploy docker
• Remove user and home directory: sudo userdel -r aspire-deploy
• Uninstall Docker packages and clean up configuration

### 🤔 Ready to Proceed?
Please review the plan above carefully. All irreversible actions have been identified.
You can modify the plan by going back, or proceed with the installation.

[✅ Yes, proceed with the installation plan]
[🔧 Modify the plan (go back to options)]
[❌ Cancel installation]
```

This comprehensive summary gives users complete visibility into:
- What will be done and in what order
- How long it will take
- What the risks are and how they're mitigated
- How to undo changes if something goes wrong
- What verification will be performed

Users can review everything before committing, greatly increasing confidence in the automation process.
