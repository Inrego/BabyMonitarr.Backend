using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Auth;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Services;

public class UserService : IUserService
{
    private readonly BabyMonitarrDbContext _db;

    public UserService(BabyMonitarrDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsFirstRunAsync()
    {
        return !await _db.Users.AnyAsync();
    }

    public async Task<User> CreateUserAsync(string username, string password, bool isAdmin = false)
    {
        var user = new User
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<User?> ValidateCredentialsAsync(string username, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == username);

        if (user?.PasswordHash == null)
            return null;

        return PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        return await _db.Users.FindAsync(id);
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
    }

    public async Task<User> EnsureUserFromExternalAsync(string username, string? displayName, string? email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user != null)
        {
            var changed = false;
            if (displayName != null && user.DisplayName != displayName)
            {
                user.DisplayName = displayName;
                changed = true;
            }
            if (email != null && user.Email != email)
            {
                user.Email = email;
                changed = true;
            }
            if (changed)
                await _db.SaveChangesAsync();

            return user;
        }

        var isFirstUser = !await _db.Users.AnyAsync();
        user = new User
        {
            Username = username,
            DisplayName = displayName,
            Email = email,
            IsAdmin = isFirstUser,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task ChangePasswordAsync(int userId, string newPassword)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new InvalidOperationException("User not found");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await _db.SaveChangesAsync();
    }
}
