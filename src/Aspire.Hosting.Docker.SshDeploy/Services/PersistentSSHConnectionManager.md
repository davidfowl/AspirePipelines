# PersistentSSHConnectionManager

## Overview

The `PersistentSSHConnectionManager` is an SSH connection manager designed specifically for Windows, where the SSH client does not support ControlMaster (a Unix-only feature for connection multiplexing).

## Problem Statement

On Unix systems, SSH supports ControlMaster, which allows multiple SSH sessions to share a single network connection through a control socket. This is efficient and avoids connection exhaustion.

On Windows, each SSH command opens a new TCP connection to the remote server. When deploying with Aspire, many SSH commands are executed in sequence, which can lead to:

- Connection exhaustion (too many simultaneous connections)
- Rate limiting by the SSH server
- "Connection timed out" errors (exit code 255)

## Solution

The `PersistentSSHConnectionManager` maintains a single, persistent SSH connection using stdin/stdout communication:

1. **Single Connection**: Opens one SSH connection that runs a persistent bash shell on the remote server
2. **Command Wrapping**: Each command is wrapped with delimiters so we can parse the output
3. **Output Parsing via stdout**: Reads command output using delimiters to separate results and capture exit codes
4. **Session Reuse**: The same connection is reused for all commands during the deployment

## How It Works

### Connection Establishment

1. SSH connects to the remote server running `bash`
2. A ready-check command (`echo ___ASPIRE_READY___`) is sent to verify the connection
3. Upon receiving the ready marker, the connection is considered established

### Command Execution

Each command sent through the connection is wrapped with markers:

```bash
echo ___ASPIRE_CMD_START___; <command>; __ec=$?; echo ___ASPIRE_EXIT_CODE___$__ec; echo ___ASPIRE_CMD_END___
```

This ensures:
- We know where command output starts and ends
- We can capture the exit code of the executed command

### Protocol

The implementation uses a simple text-based protocol with delimiters:

| Marker | Purpose |
|--------|---------|
| `___ASPIRE_READY___` | Signals the connection is ready |
| `___ASPIRE_CMD_START___` | Marks the beginning of command output |
| `___ASPIRE_CMD_END___` | Marks the end of command output |
| `___ASPIRE_EXIT_CODE___N` | Contains the exit code of the executed command |

### Communication Flow

```
Client                              Server (bash shell)
  |                                      |
  |  echo ___ASPIRE_READY___ -->         |
  |                                      |
  |  <-- ___ASPIRE_READY___             |  (connection ready)
  |                                      |
  |  echo START; whoami && pwd;... -->   |  (wrapped command)
  |                                      |
  |  <-- ___ASPIRE_CMD_START___         |
  |  <-- root                            |
  |  <-- /root                           |
  |  <-- ___ASPIRE_EXIT_CODE___0        |
  |  <-- ___ASPIRE_CMD_END___           |
  |                                      |
  |  exit -->                            |  (close session)
```

## Platform Selection

The `NativeSSHConnectionFactory` automatically selects the appropriate manager based on the operating system:

- **Windows**: Uses `PersistentSSHConnectionManager` (stdin/stdout session)
- **Unix/macOS**: Uses `NativeSSHConnectionManager` (ControlMaster)

```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    manager = new PersistentSSHConnectionManager(...);
}
else
{
    manager = new NativeSSHConnectionManager(...);
}
```

## File Transfers

File transfers (SCP) still use separate connections since they require binary data transfer that doesn't work well over the text-based stdin/stdout protocol. However, file transfers are typically less frequent than command execution, so this is acceptable.

## Key Features

- **Thread Safety**: Uses `SemaphoreSlim` to ensure only one command executes at a time
- **Timeout Handling**: Commands have configurable timeouts with proper cancellation
- **Path Expansion**: Remote paths containing `$HOME` or `~` are expanded via the SSH session
- **Keepalive**: SSH connection uses `ServerAliveInterval` to maintain the connection
- **Graceful Shutdown**: Sends "exit" command before closing the connection

## Why This Approach?

The simpler approach of wrapping each command with markers (instead of running a complex command loop script) was chosen because:

1. **No escaping issues**: Avoids complex bash script escaping that differs between Windows and Unix
2. **Simpler parsing**: Each command's output is clearly delimited
3. **Reliable exit codes**: Exit codes are captured immediately after each command
4. **Easy debugging**: The wrapped command format is straightforward to debug

## Related Files

- `NativeSSHConnectionManager.cs` - Unix implementation using ControlMaster
- `NativeSSHConnectionFactory.cs` - Factory that selects the appropriate manager
- `ISSHConnectionManager.cs` - Interface implemented by both managers
