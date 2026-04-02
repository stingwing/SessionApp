using System.Net;
using System.Net.Mail;

namespace SessionApp.Services
{
    public class EmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlBody)
        {
            try
            {
                var smtpHost = _configuration["Email:SmtpHost"];
                var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
                var fromEmail = _configuration["Email:FromEmail"];
                var fromName = _configuration["Email:FromName"];
                var username = _configuration["Email:Username"];
                var password = _configuration["Email:Password"];
                var enableSsl = bool.Parse(_configuration["Email:EnableSsl"] ?? "true");

                if (string.IsNullOrEmpty(smtpHost) || string.IsNullOrEmpty(fromEmail) || 
                    string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    _logger.LogWarning("Email configuration is incomplete. Email not sent.");
                    return false;
                }

                using var smtpClient = new SmtpClient(smtpHost, smtpPort)
                {
                    EnableSsl = enableSsl,
                    Credentials = new NetworkCredential(username, password),
                    Timeout = 30000
                };

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(fromEmail, fromName ?? "SessionApp"),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true
                };

                mailMessage.To.Add(toEmail);

                await smtpClient.SendMailAsync(mailMessage);
                _logger.LogInformation("Email sent successfully to {Email}", toEmail);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
                return false;
            }
        }

        public string GenerateEmailVerificationBody(string username, string verificationUrl)
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #4CAF50; color: white; padding: 20px; text-align: center; }}
        .content {{ padding: 20px; background-color: #f9f9f9; }}
        .button {{ display: inline-block; padding: 12px 24px; background-color: #4CAF50; color: white; text-decoration: none; border-radius: 5px; margin: 20px 0; }}
        .footer {{ text-align: center; padding: 20px; font-size: 12px; color: #666; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>Welcome to Commander Pod Creator!</h1>
        </div>
        <div class='content'>
            <h2>Hello {username},</h2>
            <p>Thank you for creating an account with Commander Pod Creator. To complete your registration, please verify your email address by clicking the button below:</p>
            <p style='text-align: center;'>
                <a href='{verificationUrl}' class='button'>Verify Email Address</a>
            </p>
            <p>If the button doesn't work, you can copy and paste this link into your browser:</p>
            <p style='word-break: break-all; color: #666;'>{verificationUrl}</p>
            <p>This link will expire in 24 hours for security reasons.</p>
            <p>If you didn't create this account, you can safely ignore this email.</p>
        </div>
        <div class='footer'>
            <p>&copy; {DateTime.UtcNow.Year} Commander Pod Creator. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";
        }

        public string GeneratePasswordResetBody(string username, string resetUrl)
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #FF9800; color: white; padding: 20px; text-align: center; }}
        .content {{ padding: 20px; background-color: #f9f9f9; }}
        .button {{ display: inline-block; padding: 12px 24px; background-color: #FF9800; color: white; text-decoration: none; border-radius: 5px; margin: 20px 0; }}
        .footer {{ text-align: center; padding: 20px; font-size: 12px; color: #666; }}
        .warning {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 10px; margin: 10px 0; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>Password Reset Request</h1>
        </div>
        <div class='content'>
            <h2>Hello {username},</h2>
            <p>We received a request to reset your password. If you made this request, click the button below to reset your password:</p>
            <p style='text-align: center;'>
                <a href='{resetUrl}' class='button'>Reset Password</a>
            </p>
            <p>If the button doesn't work, you can copy and paste this link into your browser:</p>
            <p style='word-break: break-all; color: #666;'>{resetUrl}</p>
            <div class='warning'>
                <p><strong>Security Notice:</strong></p>
                <ul>
                    <li>This link will expire in 1 hour for security reasons.</li>
                    <li>If you didn't request a password reset, please ignore this email.</li>
                    <li>Your password will not be changed unless you click the link above.</li>
                </ul>
            </div>
        </div>
        <div class='footer'>
            <p>&copy; {DateTime.UtcNow.Year} Commander Pod Creator. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";
        }
    }
}
