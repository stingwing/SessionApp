# Security Setup Guide

This guide explains the security improvements implemented and how to configure them.

## 🔐 What Was Fixed

### 1. **JWT Authentication Added**
- Login and registration now return JWT tokens
- Protected endpoints require authentication
- Tokens expire after 7 days

### 2. **Password Change Secured**
- **BEFORE:** Anyone could change anyone's password if they knew the current password
- **AFTER:** Only authenticated users can change their OWN password
- Uses JWT token to verify identity

### 3. **Email Privacy Protected**
- **BEFORE:** Email addresses were exposed in public profile endpoints
- **AFTER:** Email only shown to the authenticated user viewing their own profile
- Public profiles hide email addresses

### 4. **User Enumeration Fixed**
- **BEFORE:** Registration revealed if username/email already existed
- **AFTER:** Generic error message prevents user enumeration

## 🚀 Setup Instructions

### Development Setup

1. **Install Required NuGet Packages:**
   ```bash
   dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
   dotnet add package System.IdentityModel.Tokens.Jwt
   ```

2. **Generate a Secure JWT Secret:**
   ```bash
   # On Windows PowerShell:
   -join ((33..126) | Get-Random -Count 64 | % {[char]$_})
   
   # On Linux/Mac:
   openssl rand -base64 64
   ```

3. **Configure User Secrets (Development):**
   ```bash
   # Initialize user secrets for your project
   dotnet user-secrets init
   
   # Set JWT Secret (replace with your generated secret)
   dotnet user-secrets set "Jwt:Secret" "your-generated-secret-key-min-32-chars"
   
   # Set other secrets (if not already set)
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "your-connection-string"
   dotnet user-secrets set "Email:Password" "your-email-password"
   ```

### Production Setup

1. **Set Environment Variables (Azure App Service, Docker, etc.):**
   ```bash
   # JWT Configuration
   Jwt__Secret=your-production-secret-key-min-32-chars-CHANGE-THIS
   Jwt__Issuer=SessionApp
   Jwt__Audience=SessionApp
   
   # Database
   ConnectionStrings__DefaultConnection=your-production-connection-string
   
   # Email
   Email__Password=your-production-email-password
   ```

2. **For Azure App Service:**
   - Go to Configuration → Application settings
   - Add the environment variables above
   - The double underscore (`__`) is converted to `:` in configuration

3. **For Docker:**
   ```yaml
   environment:
     - Jwt__Secret=${JWT_SECRET}
     - Jwt__Issuer=SessionApp
     - Jwt__Audience=SessionApp
     - ConnectionStrings__DefaultConnection=${DB_CONNECTION}
     - Email__Password=${EMAIL_PASSWORD}
   ```

## 📋 API Changes

### **NEW: Login Response Includes Token**

**Before:**
```json
{
  "userId": "...",
  "username": "john_doe",
  "email": "john@example.com",
  "displayName": "John Doe"
}
```

**After:**
```json
{
  "userId": "...",
  "username": "john_doe",
  "email": "john@example.com",
  "displayName": "John Doe",
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2024-04-07T10:30:00Z"
}
```

### **NEW: Authentication Required for Password Change**

**Before:**
```http
POST /api/auth/change-password?userId={guid}
Authorization: (none)
Content-Type: application/json

{
  "currentPassword": "old",
  "newPassword": "new",
  "confirmNewPassword": "new"
}
```

**After:**
```http
POST /api/auth/change-password
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
Content-Type: application/json

{
  "currentPassword": "old",
  "newPassword": "new",
  "confirmNewPassword": "new"
}
```

### **NEW: Public Profiles Hide Email**

**Before:**
```json
GET /api/auth/profile/{userId}
{
  "userId": "...",
  "username": "john_doe",
  "email": "john@example.com",  // ❌ Exposed to everyone
  "displayName": "John Doe"
}
```

**After:**
```json
GET /api/auth/profile/{userId}
{
  "userId": "...",
  "username": "john_doe",
  "displayName": "John Doe"  // ✅ Email hidden
}
```

**To get your own profile with email:**
```json
GET /api/auth/profile/me
Authorization: Bearer {token}

{
  "userId": "...",
  "username": "john_doe",
  "email": "john@example.com",  // ✅ Only shown to owner
  "displayName": "John Doe",
  "emailConfirmed": true
}
```

## 🔨 Frontend Integration

### Storing the JWT Token

```javascript
// After login/register
const response = await fetch('/api/auth/login', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ usernameOrEmail, password })
});

const data = await response.json();

// Store token (choose one method)
localStorage.setItem('authToken', data.token);  // Persists across sessions
// OR
sessionStorage.setItem('authToken', data.token); // Cleared on tab close
```

### Using the Token for Authenticated Requests

```javascript
// Change password (requires authentication)
const token = localStorage.getItem('authToken');

const response = await fetch('/api/auth/change-password', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`
  },
  body: JSON.stringify({
    currentPassword: 'old',
    newPassword: 'new',
    confirmNewPassword: 'new'
  })
});
```

### Token Expiration Handling

```javascript
// Check if token is expired
function isTokenExpired(token) {
  const payload = JSON.parse(atob(token.split('.')[1]));
  return payload.exp * 1000 < Date.now();
}

// Refresh or redirect to login
if (!token || isTokenExpired(token)) {
  // Redirect to login page
  window.location.href = '/login';
}
```

## 🔍 Testing the Security

### Test Password Change Security

**This should FAIL (no token):**
```bash
curl -X POST http://localhost:5000/api/auth/change-password \
  -H "Content-Type: application/json" \
  -d '{
    "currentPassword": "old",
    "newPassword": "new",
    "confirmNewPassword": "new"
  }'
```
Expected: `401 Unauthorized`

**This should SUCCEED (with valid token):**
```bash
# First, login to get token
TOKEN=$(curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"usernameOrEmail":"testuser","password":"Test123!"}' \
  | jq -r '.token')

# Then change password with token
curl -X POST http://localhost:5000/api/auth/change-password \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{
    "currentPassword": "Test123!",
    "newPassword": "NewTest456!",
    "confirmNewPassword": "NewTest456!"
  }'
```
Expected: `200 OK`

### Test Email Privacy

```bash
# Public profile - should NOT include email
curl http://localhost:5000/api/auth/profile/{userId}

# My profile - should include email (requires token)
curl http://localhost:5000/api/auth/profile/me \
  -H "Authorization: Bearer $TOKEN"
```

## 📊 Security Checklist

- [x] JWT authentication implemented
- [x] Password change requires authentication
- [x] Email addresses hidden from public profiles
- [x] User enumeration prevented in registration
- [x] Rate limiting on auth endpoints (10/min)
- [x] Account lockout after 5 failed logins
- [x] Password hashing with PBKDF2
- [ ] **TODO:** Move secrets to User Secrets/Environment Variables
- [ ] **TODO:** Add security headers (HSTS, CSP, etc.)
- [ ] **TODO:** Configure CORS for production
- [ ] **TODO:** Implement email confirmation enforcement
- [ ] **TODO:** Add 2FA (optional enhancement)

## 🔒 Remaining Security Improvements

See the full security evaluation report for additional improvements like:
- Security headers (HSTS, CSP, X-Frame-Options)
- CORS improvements for production
- Email confirmation enforcement
- Password strength improvements
- PII logging sanitization

## 📝 Notes

- **Token Expiration:** Tokens expire after 7 days. Adjust in `JwtTokenService.cs` if needed.
- **Secret Length:** JWT secret must be at least 32 characters for security.
- **HTTPS:** Always use HTTPS in production to protect tokens in transit.
- **Token Storage:** Never store tokens in cookies without `HttpOnly` and `Secure` flags.
