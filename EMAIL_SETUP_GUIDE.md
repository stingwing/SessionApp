# Email Verification and Password Reset Setup Guide

This guide explains how to configure and use the email verification and password reset features.

## Features Added

### 1. Email Verification
- When a user registers, they receive a verification email with a link
- The link is valid for 24 hours
- Users can resend verification emails if needed
- Emails are verified by clicking the link in their inbox

### 2. Password Reset
- Users can request a password reset by providing their email
- They receive an email with a reset link valid for 1 hour
- The reset link allows them to set a new password
- Tokens are securely hashed in the database

## Configuration

### Email Settings (appsettings.json)

You need to configure the email settings in `appsettings.json` or use User Secrets for security:

```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "FromEmail": "your-email@gmail.com",
    "FromName": "SessionApp",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password",
    "EnableSsl": "true"
  },
  "App": {
    "BaseUrl": "https://yourdomain.com"
  }
}
```

### Gmail Setup (Recommended for Development)

1. Go to your Google Account settings
2. Enable 2-Factor Authentication
3. Go to Security > 2-Step Verification > App passwords
4. Generate a new app password for "Mail"
5. Use that app password in the configuration

### Using User Secrets (Recommended for Development)

Instead of storing sensitive data in appsettings.json, use User Secrets:

```bash
dotnet user-secrets init
dotnet user-secrets set "Email:SmtpHost" "smtp.gmail.com"
dotnet user-secrets set "Email:SmtpPort" "587"
dotnet user-secrets set "Email:FromEmail" "your-email@gmail.com"
dotnet user-secrets set "Email:FromName" "SessionApp"
dotnet user-secrets set "Email:Username" "your-email@gmail.com"
dotnet user-secrets set "Email:Password" "your-app-password"
dotnet user-secrets set "Email:EnableSsl" "true"
dotnet user-secrets set "App:BaseUrl" "https://localhost:7086"
```

### Environment Variables (Recommended for Production)

For production, use environment variables:

```bash
Email__SmtpHost=smtp.gmail.com
Email__SmtpPort=587
Email__FromEmail=your-email@gmail.com
Email__FromName=SessionApp
Email__Username=your-email@gmail.com
Email__Password=your-app-password
Email__EnableSsl=true
App__BaseUrl=https://yourdomain.com
```

## API Endpoints

### 1. Register (Modified)
**POST** `/api/auth/register`

Now automatically sends a verification email after successful registration.

### 2. Verify Email
**GET** `/api/auth/verify-email?token={token}`

Verifies the user's email address. Usually called by clicking the link in the verification email.

**Response:**
```json
{
  "message": "Email verified successfully"
}
```

### 3. Resend Verification Email
**POST** `/api/auth/resend-verification`

Resends the verification email to the user.

**Request Body:**
```json
{
  "email": "user@example.com"
}
```

**Response:**
```json
{
  "message": "Verification email sent successfully"
}
```

### 4. Forgot Password
**POST** `/api/auth/forgot-password`

Sends a password reset email to the user.

**Request Body:**
```json
{
  "email": "user@example.com"
}
```

**Response:**
```json
{
  "message": "If the email exists, a password reset link has been sent"
}
```

### 5. Reset Password
**POST** `/api/auth/reset-password`

Resets the user's password using the token from the email.

**Request Body:**
```json
{
  "token": "the-reset-token-from-email",
  "newPassword": "NewPassword123!",
  "confirmNewPassword": "NewPassword123!"
}
```

**Response:**
```json
{
  "message": "Password reset successfully"
}
```

## Database Migration

The following fields were added to the `UserEntity`:

- `EmailVerificationToken` (string, nullable) - Hashed token for email verification
- `EmailVerificationTokenExpiresUtc` (DateTime, nullable) - Expiration time for verification token
- `PasswordResetToken` (string, nullable) - Hashed token for password reset
- `PasswordResetTokenExpiresUtc` (DateTime, nullable) - Expiration time for reset token

To apply the migration:

```bash
dotnet ef database update
```

## Security Features

1. **Token Hashing**: All tokens are hashed using SHA256 before storage
2. **Token Expiration**: 
   - Email verification tokens expire after 24 hours
   - Password reset tokens expire after 1 hour
3. **Rate Limiting**: All auth endpoints are rate-limited to prevent abuse
4. **Email Enumeration Prevention**: The forgot password endpoint always returns success, even if the email doesn't exist
5. **Secure Token Generation**: Tokens use cryptographically secure random generation

## Testing

### Test Email Verification Flow

1. Register a new user: `POST /api/auth/register`
2. Check your email inbox for the verification link
3. Click the link or call: `GET /api/auth/verify-email?token={token}`
4. Verify the email is confirmed: `GET /api/auth/profile/{userId}`

### Test Password Reset Flow

1. Request password reset: `POST /api/auth/forgot-password` with email
2. Check your email inbox for the reset link
3. Use the token to reset password: `POST /api/auth/reset-password`
4. Login with the new password: `POST /api/auth/login`

## Frontend Integration

For a complete user experience, you'll want to create frontend pages for:

1. **Email Verification Page** - Receives the token from URL and calls the verify endpoint
2. **Resend Verification Page** - Form to request a new verification email
3. **Forgot Password Page** - Form to request password reset
4. **Reset Password Page** - Form with token and new password fields

Example frontend URL structure:
- `https://yourdomain.com/verify-email?token={token}` → calls `/api/auth/verify-email`
- `https://yourdomain.com/reset-password?token={token}` → shows form that calls `/api/auth/reset-password`

## Troubleshooting

### Emails Not Sending

1. Check that email configuration is correct
2. Verify the SMTP credentials are valid
3. Check application logs for error messages
4. Ensure firewall/network allows outbound SMTP connections
5. For Gmail, make sure you're using an App Password, not your regular password

### Token Errors

1. Verify tokens haven't expired
2. Check that the token is URL-encoded properly
3. Ensure the token matches exactly (no extra spaces)

### Production Considerations

1. Use a dedicated email service like SendGrid, AWS SES, or Mailgun for better deliverability
2. Implement email templates with your branding
3. Add email queue for handling failures and retries
4. Monitor email delivery rates
5. Consider adding CAPTCHA to prevent abuse
