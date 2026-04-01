# User Authentication System

This document describes the user authentication system implemented in SessionApp, including password hashing, user registration, login, and linking user accounts to game sessions.

## Overview

The authentication system provides:
- **Secure password storage** using ASP.NET Core Identity's `PasswordHasher` with automatic salting
- **User registration** with email and username validation
- **User login** with password verification
- **User account linking** to sessions (when creating games) and participants (when playing)
- **Password management** including password changes
- **User session history** to track games created and participated in

## Database Schema

### Users Table
- `Id` (Guid) - Primary key
- `Username` (string, unique) - User's login name
- `Email` (string, unique) - User's email address
- `PasswordHash` (string) - Hashed password with salt
- `DisplayName` (string, nullable) - Display name (defaults to username)
- `CreatedAtUtc` (DateTime) - Account creation timestamp
- `LastLoginUtc` (DateTime, nullable) - Last login timestamp
- `IsActive` (bool) - Account active status
- `EmailConfirmed` (bool) - Email verification status

### Updated Tables

#### Sessions Table
- Added `HostUserId` (Guid?, nullable) - Foreign key to Users table
- Links sessions to registered user accounts when created by logged-in users

#### Participants Table
- Added `UserId` (Guid?, nullable) - Foreign key to Users table
- Links participants to registered user accounts when joining as logged-in users

## Password Security

### Hashing Algorithm
Passwords are hashed using **ASP.NET Core Identity's PasswordHasher**, which:
- Uses **PBKDF2** (Password-Based Key Derivation Function 2) with HMAC-SHA256
- Generates a unique **random salt** for each password
- Performs **10,000 iterations** by default (configurable)
- Stores the algorithm version, iteration count, salt, and hash in a single string

### Password Format
The `PasswordHash` field stores a base64-encoded string containing:
```
[Algorithm Version (1 byte)][Iteration Count (4 bytes)][Salt Size (4 bytes)][Salt][Hash]
```

### Password Verification
During login:
1. The stored hash is parsed to extract the salt and parameters
2. The provided password is hashed using the same salt and parameters
3. The hashes are compared in constant time to prevent timing attacks
4. If the algorithm is outdated, the password is automatically rehashed

## API Endpoints

### Authentication Endpoints

#### 1. Register New User
```http
POST /api/auth/register
Content-Type: application/json

{
  "username": "john_doe",
  "email": "john@example.com",
  "password": "SecurePassword123!",
  "confirmPassword": "SecurePassword123!",
  "displayName": "John Doe"  // optional
}
```

**Response (201 Created):**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "username": "john_doe",
  "email": "john@example.com",
  "displayName": "John Doe",
  "createdAtUtc": "2024-03-31T10:30:00Z",
  "lastLoginUtc": null,
  "emailConfirmed": false
}
```

#### 2. Login
```http
POST /api/auth/login
Content-Type: application/json

{
  "usernameOrEmail": "john_doe",  // Can be username or email
  "password": "SecurePassword123!"
}
```

**Response (200 OK):**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "username": "john_doe",
  "email": "john@example.com",
  "displayName": "John Doe",
  "createdAtUtc": "2024-03-31T10:30:00Z",
  "lastLoginUtc": "2024-03-31T15:45:00Z",
  "emailConfirmed": false
}
```

#### 3. Change Password
```http
POST /api/auth/change-password?userId=3fa85f64-5717-4562-b3fc-2c963f66afa6
Content-Type: application/json

{
  "currentPassword": "SecurePassword123!",
  "newPassword": "NewSecurePassword456!",
  "confirmNewPassword": "NewSecurePassword456!"
}
```

**Response (200 OK):**
```json
{
  "message": "Password changed successfully"
}
```

#### 4. Get User Profile
```http
GET /api/auth/profile/{userId}
```

**Response (200 OK):**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "username": "john_doe",
  "email": "john@example.com",
  "displayName": "John Doe",
  "createdAtUtc": "2024-03-31T10:30:00Z",
  "lastLoginUtc": "2024-03-31T15:45:00Z",
  "emailConfirmed": false
}
```

### User Session Management Endpoints

#### 5. Link Session to User (Host)
```http
POST /api/user-sessions/{sessionCode}/link-host?userId={userId}
```

Links a session to the user who created it.

#### 6. Link Participant to User
```http
POST /api/user-sessions/{sessionCode}/link-participant?participantId={participantId}&userId={userId}
```

Links a participant to a registered user account.

#### 7. Get Sessions by Host
```http
GET /api/user-sessions/by-host/{userId}
```

Returns all sessions created by the user.

#### 8. Get Sessions by Participant
```http
GET /api/user-sessions/by-participant/{userId}
```

Returns all sessions where the user is a participant.

#### 9. Get All User Sessions
```http
GET /api/user-sessions/by-user/{userId}
```

Returns both hosted and participated sessions for a user.

**Response:**
```json
{
  "hostedSessions": [...],
  "participatedSessions": [...],
  "totalSessions": 15
}
```

## Usage Flow

### 1. Anonymous User Flow (No Account)
```
Create Room → HostId is generated GUID
Join Room → ParticipantId is generated GUID
```

### 2. Registered User Flow (With Account)
```
Register → Get UserId
Login → Verify credentials
Create Room → Link session to UserId via HostUserId
Join Room → Link participant to UserId via UserId field
View History → Get all games created or joined
```

### 3. Hybrid Flow
Users can create and join sessions without an account, and optionally link them later:
```
Create Room (anonymous) → Room created
Register Account → Get UserId
Link Session → Connect existing session to user account
```

## Security Considerations

1. **Password Requirements**: Minimum 8 characters (enforced by validation)
2. **Password Storage**: Never stored in plain text, always hashed with salt
3. **Username/Email Uniqueness**: Enforced at database level with unique indexes
4. **Password Verification**: Uses constant-time comparison to prevent timing attacks
5. **Algorithm Upgrades**: Automatic rehashing when password algorithm is updated
6. **Optional Linking**: User accounts are optional; anonymous sessions still work

## Implementation Details

### Services

#### UserService
- `RegisterUserAsync()` - Creates new user with hashed password
- `LoginAsync()` - Verifies credentials and updates last login
- `ChangePasswordAsync()` - Changes password after verifying current password
- `GetUserByIdAsync()` - Retrieves user by ID
- `GetUserByUsernameAsync()` - Retrieves user by username
- `IsValidUserAsync()` - Checks if user ID is valid and active

#### SessionRepository (Extended)
- `LinkSessionToUserAsync()` - Links session to user (HostUserId)
- `LinkParticipantToUserAsync()` - Links participant to user (UserId)
- `GetSessionsByUserAsync()` - Gets sessions created by user
- `GetSessionsWhereUserIsParticipantAsync()` - Gets sessions where user participated

### Models

#### RegisterRequest
- Username, Email, Password, ConfirmPassword, DisplayName (optional)

#### LoginRequest
- UsernameOrEmail (accepts both), Password

#### ChangePasswordRequest
- CurrentPassword, NewPassword, ConfirmNewPassword

#### UserProfile
- UserId, Username, Email, DisplayName, CreatedAtUtc, LastLoginUtc, EmailConfirmed

## Configuration

The authentication system requires no additional configuration beyond what's already in `Program.cs`:

```csharp
// Register User Authentication Services
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<IPasswordHasher<UserEntity>, PasswordHasher<UserEntity>>();
```

## Migration

The database migration `AddUserAccountsAndAuthentication` includes:
1. Creating the `Users` table
2. Adding `HostUserId` to `Sessions` table
3. Adding `UserId` to `Participants` table
4. Creating indexes for performance
5. Setting up foreign keys with `SET NULL` on delete (preserves anonymous sessions)

To apply the migration:
```bash
dotnet ef database update
```

## Testing

Example test cases to implement:
1. Register with valid credentials
2. Register with duplicate username/email
3. Login with correct credentials
4. Login with incorrect password
5. Change password successfully
6. Link session to user after creation
7. Retrieve user's session history
8. Delete user (sessions remain with HostUserId set to null)

## Future Enhancements

Potential improvements:
1. **JWT Token Authentication** - Add bearer token support
2. **Email Verification** - Send confirmation emails
3. **Password Reset** - Forgot password functionality
4. **Two-Factor Authentication** - Add 2FA support
5. **OAuth Integration** - Support Google, Microsoft, etc.
6. **Role-Based Access Control** - Add admin/user roles
7. **Session Timeout** - Automatic logout after inactivity
8. **Audit Log** - Track authentication events
