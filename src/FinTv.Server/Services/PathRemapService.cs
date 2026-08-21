using System.Security.Cryptography;
using System.Text;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public sealed class PathRemapService
{
    private readonly FinTvDbContext _db;

    public PathRemapService(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PathMapping>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _db.PathMappings.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(cancellationToken);

    public async Task ReplaceAllAsync(IReadOnlyList<PathMapping> mappings, CancellationToken cancellationToken = default)
    {
        var existing = await _db.PathMappings.ToListAsync(cancellationToken);
        _db.PathMappings.RemoveRange(existing);
        var order = 0;
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.JellyfinPrefix) || string.IsNullOrWhiteSpace(mapping.LocalPrefix))
            {
                continue;
            }

            _db.PathMappings.Add(new PathMapping
            {
                JellyfinPrefix = NormalizePrefix(mapping.JellyfinPrefix),
                LocalPrefix = NormalizePrefix(mapping.LocalPrefix),
                IgnoreCase = mapping.IgnoreCase,
                SortOrder = order++
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public string? Remap(string? jellyfinPath, IReadOnlyList<PathMapping>? mappings = null)
    {
        if (string.IsNullOrWhiteSpace(jellyfinPath))
        {
            return jellyfinPath;
        }

        var source = jellyfinPath.Replace('\\', '/');
        mappings ??= _db.PathMappings.AsNoTracking().OrderBy(m => m.SortOrder).ToList();
        PathMapping? best = null;
        foreach (var mapping in mappings)
        {
            var prefix = mapping.JellyfinPrefix.Replace('\\', '/').TrimEnd('/');
            var comparison = mapping.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (source.StartsWith(prefix, comparison)
                && (best is null || prefix.Length > best.JellyfinPrefix.Replace('\\', '/').TrimEnd('/').Length))
            {
                best = mapping;
            }
        }

        if (best is null)
        {
            return source;
        }

        var from = best.JellyfinPrefix.Replace('\\', '/').TrimEnd('/');
        var to = best.LocalPrefix.Replace('\\', '/').TrimEnd('/');
        var rest = source[from.Length..];
        if (!rest.StartsWith('/') && rest.Length > 0)
        {
            rest = "/" + rest;
        }

        return to + rest;
    }

    public string? ResolveExistingPath(string? jellyfinPath)
    {
        var remapped = Remap(jellyfinPath);
        if (!string.IsNullOrWhiteSpace(remapped) && File.Exists(remapped))
        {
            return remapped;
        }

        if (!string.IsNullOrWhiteSpace(jellyfinPath) && File.Exists(jellyfinPath))
        {
            return jellyfinPath;
        }

        return remapped;
    }

    public async Task<object> TestAsync(int sampleSize, CancellationToken cancellationToken = default)
    {
        var mappings = await GetAllAsync(cancellationToken);
        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => i.Path != null && i.Path != "")
            .OrderBy(i => i.Name)
            .Take(Math.Clamp(sampleSize, 1, 500))
            .Select(i => new { i.Id, i.Name, i.Path })
            .ToListAsync(cancellationToken);

        var exists = 0;
        var missing = 0;
        var samples = new List<object>();
        foreach (var item in items)
        {
            var local = Remap(item.Path, mappings);
            var found = !string.IsNullOrWhiteSpace(local) && File.Exists(local);
            if (found)
            {
                exists++;
            }
            else
            {
                missing++;
            }

            if (samples.Count < 15)
            {
                samples.Add(new { item.Id, item.Name, jellyfinPath = item.Path, localPath = local, exists = found });
            }
        }

        return new { total = items.Count, exists, missing, mappings = mappings.Count, samples };
    }

    private static string NormalizePrefix(string prefix) => prefix.Trim().Replace('\\', '/').TrimEnd('/');
}

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2")
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public sealed class FfmpegLocator : IFfmpegLocator
{
    public string EncoderPath { get; }

    public FfmpegLocator(IConfiguration configuration)
    {
        EncoderPath = configuration["FFMPEG_PATH"]
            ?? Environment.GetEnvironmentVariable("FFMPEG_PATH")
            ?? FindOnPath("ffmpeg")
            ?? "ffmpeg";
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }

        return null;
    }
}

public sealed class PublicBaseUrl : IPublicBaseUrl
{
    public string GetLoopbackHttpAddress()
        => Environment.GetEnvironmentVariable("FINTV_PUBLIC_URL")
           ?? "http://127.0.0.1:8097";

    public string GetSmartApiUrl(HttpRequest request)
    {
        var configured = FinTvRuntime.Current?.Configuration.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host}";
    }
}

public sealed class ApiKeyOptions
{
    public string ApiKey { get; set; } = string.Empty;
}
