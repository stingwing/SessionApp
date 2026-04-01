using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SessionApp.Data;
using SessionApp.Data.Entities;
using SessionApp.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SessionApp.Services
{
    public class UserService
    {
        private readonly SessionDbContext _context;
        private readonly IPasswordHasher<UserEntity> _passwordHasher;
        private readonly ILogger<UserService> _logger;

        // Security configuration constants
        private const int MaxFailedLoginAttempts = 5;
        private const int LockoutDurationMinutes = 15;

        public UserService(
            SessionDbContext context,
            IPasswordHasher<UserEntity> passwordHasher,
            ILogger<UserService> logger)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _logger = logger;
        }

        /// <summary>
        /// Registers a new user with hashed password
        /// </summary>
        public async Task<(bool Success, string? ErrorMessage, UserEntity? User)> RegisterUserAsync(RegisterRequest request)
        {
            // Check if username already exists
            var existingUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Username.ToLower() == request.Username.ToLower());

            if (existingUser != null)
            {
                return (false, "Username already exists", null);
            }

            // Check if email already exists
            existingUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower());

            if (existingUser != null)
            {
                return (false, "Email already exists", null);
            }

            // Create new user entity
            var user = new UserEntity
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                Email = request.Email,
                DisplayName = request.DisplayName ?? request.Username,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
                EmailConfirmed = false
            };

            // Hash the password with salt
            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

            try
            {
                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                _logger.LogInformation("User registered successfully: {Username}", user.Username);
                return (true, null, user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering user: {Username}", request.Username);
                return (false, "An error occurred during registration", null);
            }
        }

        /// <summary>
        /// Authenticates a user and verifies password
        /// </summary>
        public async Task<(bool Success, string? ErrorMessage, UserEntity? User)> LoginAsync(LoginRequest request)
        {
            // Find user by username or email
            var user = await _context.Users
                .FirstOrDefaultAsync(u =>
                    u.Username.ToLower() == request.UsernameOrEmail.ToLower() ||
                    u.Email.ToLower() == request.UsernameOrEmail.ToLower());

            // Use generic error message to prevent user enumeration (timing attack protection)
            const string genericErrorMessage = "Invalid username/email or password";

            if (user == null)
            {
                // Perform a dummy hash verification to prevent timing attacks
                // This ensures failed login attempts take similar time whether user exists or not
                _passwordHasher.HashPassword(new UserEntity(), request.Password);
                _logger.LogWarning("Login attempt with non-existent user: {UsernameOrEmail}", request.UsernameOrEmail);
                return (false, genericErrorMessage, null);
            }

            // Check if account is locked out
            if (user.IsLockedOut)
            {
                var remainingLockoutTime = user.LockoutEndUtc!.Value - DateTime.UtcNow;
                _logger.LogWarning("Login attempt on locked account: {Username}. Lockout remaining: {RemainingTime}",
                    user.Username, remainingLockoutTime);
                return (false, $"Account is temporarily locked. Please try again in {Math.Ceiling(remainingLockoutTime.TotalMinutes)} minutes.", null);
            }

            if (!user.IsActive)
            {
                _logger.LogWarning("Login attempt on disabled account: {Username}", user.Username);
                return (false, "Account is disabled. Please contact support.", null);
            }

            // Verify password
            var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

            if (verificationResult == PasswordVerificationResult.Failed)
            {
                // Increment failed login attempts
                user.FailedLoginAttempts++;

                _logger.LogWarning("Failed login attempt for user: {Username}. Attempt count: {FailedAttempts}",
                    user.Username, user.FailedLoginAttempts);

                // Lock account if max attempts exceeded
                if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
                {
                    user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(LockoutDurationMinutes);
                    _logger.LogWarning("Account locked due to failed login attempts: {Username}. Locked until: {LockoutEnd}",
                        user.Username, user.LockoutEndUtc);
                    await _context.SaveChangesAsync();
                    return (false, $"Account has been locked for {LockoutDurationMinutes} minutes due to multiple failed login attempts.", null);
                }

                await _context.SaveChangesAsync();
                return (false, genericErrorMessage, null);
            }

            // Successful login - reset failed attempts and lockout
            user.FailedLoginAttempts = 0;
            user.LockoutEndUtc = null;
            user.LastLoginUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("User logged in successfully: {Username}", user.Username);

            // If password needs rehashing (old algorithm), update it
            if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Password rehashed for user: {Username}", user.Username);
            }

            return (true, null, user);
        }

        /// <summary>
        /// Changes user password
        /// </summary>
        public async Task<(bool Success, string? ErrorMessage)> ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
            {
                return (false, "User not found");
            }

            // Verify current password
            var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);

            if (verificationResult == PasswordVerificationResult.Failed)
            {
                return (false, "Current password is incorrect");
            }

            // Hash new password
            user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);

            try
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Password changed for user: {Username}", user.Username);
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing password for user: {Username}", user.Username);
                return (false, "An error occurred while changing password");
            }
        }

        /// <summary>
        /// Gets user by ID
        /// </summary>
        public async Task<UserEntity?> GetUserByIdAsync(Guid userId)
        {
            return await _context.Users.FindAsync(userId);
        }

        /// <summary>
        /// Gets user by username
        /// </summary>
        public async Task<UserEntity?> GetUserByUsernameAsync(string username)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        }

        /// <summary>
        /// Checks if a user ID corresponds to a valid user
        /// </summary>
        public async Task<bool> IsValidUserAsync(Guid userId)
        {
            return await _context.Users.AnyAsync(u => u.Id == userId && u.IsActive);
        }
    }
}
