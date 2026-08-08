using System.Runtime.CompilerServices;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Mvcc;

/// <summary>
/// Shares one <see cref="MvStore"/> (and its durable log handle) per file-system
/// database identity so concurrent connections observe the same version store.
/// </summary>
internal static class EmbeddedMvStoreRegistry
{
    private sealed class Scope
    {
        private readonly Dictionary<string, MvStore> _stores = new(StringComparer.Ordinal);

        internal MvStore GetOrCreate(string key, Func<MvStore> factory)
        {
            lock (_stores)
            {
                if (_stores.TryGetValue(key, out var existing))
                    return existing;
                var created = factory();
                _stores.Add(key, created);
                return created;
            }
        }

        internal bool TryGet(string key, out MvStore? store)
        {
            lock (_stores)
                return _stores.TryGetValue(key, out store);
        }

        internal void Remove(string key)
        {
            lock (_stores)
                _stores.Remove(key);
        }
    }

    private static readonly ConditionalWeakTable<IFileSystem, Scope> FileSystemScopes = new();
    private static readonly Scope PhysicalFileSystemScope = new();

    internal static MvStore GetOrCreate(
        IFileSystem fileSystem,
        string databasePath,
        Func<MvStore> factory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentNullException.ThrowIfNull(factory);

        return ResolveScope(fileSystem, databasePath, out var key).GetOrCreate(key, factory);
    }

    internal static bool TryGet(IFileSystem fileSystem, string databasePath, out MvStore? store)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        return ResolveScope(fileSystem, databasePath, out var key).TryGet(key, out store);
    }

    internal static void Remove(IFileSystem fileSystem, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        ResolveScope(fileSystem, databasePath, out var key).Remove(key);
    }

    private static Scope ResolveScope(IFileSystem fileSystem, string databasePath, out string key)
    {
        var unwrapped = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        if (unwrapped is not PhysicalFileSystem)
        {
            key = databasePath;
            return FileSystemScopes.GetValue(unwrapped, static _ => new Scope());
        }

        key = Path.GetFullPath(databasePath);
        if (OperatingSystem.IsWindows())
            key = key.ToUpperInvariant();
        return PhysicalFileSystemScope;
    }
}
