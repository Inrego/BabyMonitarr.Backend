using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public interface IUserService
{
    Task<bool> IsFirstRunAsync();
    Task<User> CreateUserAsync(string username, string password, bool isAdmin = false);
    Task<User?> ValidateCredentialsAsync(string username, string password);
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByUsernameAsync(string username);
    Task<User> EnsureUserFromExternalAsync(string username, string? displayName, string? email);
    Task ChangePasswordAsync(int userId, string newPassword);
}
