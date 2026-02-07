using System;

namespace SessionApp.Models
{
    public class SessionSummary
    {
        public string Code { get; set; } = string.Empty;
        public string EventName { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public bool IsGameStarted { get; set; }
        public bool IsGameEnded { get; set; }
        public bool Archived { get; set; }
        public int ParticipantCount { get; set; }
    }
}