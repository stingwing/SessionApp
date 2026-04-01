using SessionApp.Data.Entities;
using SessionApp.Models;
using System;

namespace SessionApp.Data
{
    /// <summary>
    /// Extension methods for linking user accounts to sessions and participants
    /// </summary>
    public static class UserLinkingExtensions
    {
        /// <summary>
        /// Links a session to a user account (when host is a registered user)
        /// </summary>
        public static void LinkHostUser(this SessionEntity sessionEntity, Guid? userId)
        {
            sessionEntity.HostUserId = userId;
        }

        /// <summary>
        /// Links a participant to a user account (when player is a registered user)
        /// </summary>
        public static void LinkParticipantUser(this ParticipantEntity participantEntity, Guid? userId)
        {
            participantEntity.UserId = userId;
        }

        /// <summary>
        /// Checks if a session has a linked user account
        /// </summary>
        public static bool HasLinkedUser(this SessionEntity sessionEntity)
        {
            return sessionEntity.HostUserId.HasValue;
        }

        /// <summary>
        /// Checks if a participant has a linked user account
        /// </summary>
        public static bool HasLinkedUser(this ParticipantEntity participantEntity)
        {
            return participantEntity.UserId.HasValue;
        }
    }
}
