using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public class ApiKeyService : IApiKeyService
{
    private const int KeyLength = 48;
    private const int PrefixLength = 8;

    private readonly BabyMonitarrDbContext _db;

    public ApiKeyService(BabyMonitarrDbContext db)
    {
        _db = db;
    }

    public async Task<(ApiKey apiKey, string plainTextKey)> GenerateKeyAsync(int userId, string name)
    {
        var plainText = GenerateRandomKey();
        var hash = HashKey(plainText);

        var apiKey = new ApiKey
        {
            UserId = userId,
            KeyHash = hash,
            KeyPrefix = plainText[..PrefixLength],
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        _db.ApiKeys.Add(apiKey);
        await _db.SaveChangesAsync();

        return (apiKey, plainText);
    }

    public async Task<List<ApiKey>> ListKeysForUserAsync(int userId)
    {
        return await _db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> DeleteKeyAsync(int keyId, int userId)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(
            k => k.Id == keyId && k.UserId == userId);

        if (key == null)
            return false;

        _db.ApiKeys.Remove(key);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<User?> ValidateKeyAsync(string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey) || plainTextKey.Length < PrefixLength)
            return null;

        var prefix = plainTextKey[..PrefixLength];
        var candidateHash = HashKey(plainTextKey);

        // Find keys with matching prefix to narrow the search
        var candidates = await _db.ApiKeys
            .Include(k => k.User)
            .Where(k => k.KeyPrefix == prefix)
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(candidateHash),
                System.Text.Encoding.UTF8.GetBytes(candidate.KeyHash)))
            {
                candidate.LastUsedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return candidate.User;
            }
        }

        return null;
    }

    private static string GenerateRandomKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyLength);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string HashKey(string plainTextKey)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plainTextKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
