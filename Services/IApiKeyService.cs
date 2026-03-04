using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public interface IApiKeyService
{
    Task<(ApiKey apiKey, string plainTextKey)> GenerateKeyAsync(int userId, string name);
    Task<List<ApiKey>> ListKeysForUserAsync(int userId);
    Task<bool> DeleteKeyAsync(int keyId, int userId);
    Task<User?> ValidateKeyAsync(string plainTextKey);
}
