using Renci.SshNet;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public static class SSHUtility
{
    public static async Task<SshClient> CreateSSHClient(string host, string username, string? password, string? keyPath, string port, CancellationToken cancellationToken)
    {
        var connectionInfo = CreateConnectionInfo(host, username, password, keyPath, port);
        var client = new SshClient(connectionInfo);

        await client.ConnectAsync(cancellationToken);

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new InvalidOperationException("Failed to establish SSH connection");
        }

        return client;
    }

    public static async Task<ScpClient> CreateSCPClient(string host, string username, string? password, string? keyPath, string port, CancellationToken cancellationToken)
    {
        var connectionInfo = CreateConnectionInfo(host, username, password, keyPath, port);
        var client = new ScpClient(connectionInfo);

        await client.ConnectAsync(cancellationToken);

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new InvalidOperationException("Failed to establish SCP connection");
        }

        return client;
    }

    public static ConnectionInfo CreateConnectionInfo(string host, string username, string? password, string? keyPath, string port)
    {
        var portInt = int.Parse(port);

        if (!string.IsNullOrEmpty(keyPath))
        {
            // Use key-based authentication
            var keyFile = new PrivateKeyFile(keyPath, password ?? "");
            return new ConnectionInfo(host, portInt, username, new PrivateKeyAuthenticationMethod(username, keyFile));
        }
        else if (!string.IsNullOrEmpty(password))
        {
            // Use password authentication
            return new ConnectionInfo(host, portInt, username, new PasswordAuthenticationMethod(username, password));
        }
        else
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }
    }

    public static bool IsLikelySSHKey(string filePath)
    {
        try
        {
            // Check file size (SSH keys are typically between 100 bytes and 10KB)
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 100 || fileInfo.Length > 10240)
                return false;

            // Read first few lines to check for SSH key headers
            var firstLines = File.ReadLines(filePath).Take(3).ToArray();
            if (firstLines.Length == 0)
                return false;

            var firstLine = firstLines[0].Trim();

            // Check for common SSH key headers
            var sshKeyHeaders = new[]
            {
                "-----BEGIN OPENSSH PRIVATE KEY-----",
                "-----BEGIN RSA PRIVATE KEY-----",
                "-----BEGIN DSA PRIVATE KEY-----",
                "-----BEGIN EC PRIVATE KEY-----",
                "-----BEGIN PRIVATE KEY-----"
            };

            return sshKeyHeaders.Any(header => firstLine.StartsWith(header, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
