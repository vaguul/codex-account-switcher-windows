using System.Security.AccessControl;
using System.Security.Principal;

namespace Vaguul.CodexAccountSwitcher.Services;

public static class SecureFileSystem
{
    private static readonly SecurityIdentifier CurrentUser = WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("The current Windows identity is unavailable.");

    public static bool IsInside(string candidate, string root)
    {
        var candidatePath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Account data cannot use symbolic links or directory junctions.");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }
    }

    public static void CreatePrivateDirectory(string path)
    {
        RejectReparsePoints(path);
        var security = new DirectorySecurity();
        security.SetOwner(CurrentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            directory.Create(security);
        }
        else
        {
            directory.SetAccessControl(security);
        }
    }

    public static async Task AtomicWriteAsync(
        string destination,
        ReadOnlyMemory<byte> bytes,
        string? backupPath = null,
        CancellationToken cancellationToken = default)
    {
        RejectReparsePoints(destination);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        var security = new FileSecurity();
        security.SetOwner(CurrentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));

        try
        {
            await using (var stream = FileSystemAclExtensions.Create(
                new FileInfo(temporary),
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough,
                security))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                if (backupPath is not null && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                File.Replace(temporary, destination, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        RejectReparsePoints(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The required file was not found.", path);
        }

        if (info.Length is <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException("The file has an invalid size.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public static void DeleteTreeInside(string path, string root)
    {
        if (!IsInside(path, root))
        {
            throw new IOException("Cleanup was blocked because the path is outside the expected directory.");
        }

        RejectReparsePoints(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            RejectReparsePoints(entry);
            if (Directory.Exists(entry))
            {
                DeleteTreeInside(entry, root);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path);
    }
}
