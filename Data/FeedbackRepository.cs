using Microsoft.EntityFrameworkCore;
using SessionApp.Data.Entities;
using SessionApp.Models;

namespace SessionApp.Data
{
    public class FeedbackRepository
    {
        private readonly SessionDbContext _context;

        public FeedbackRepository(SessionDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Create new feedback
        /// </summary>
        public async Task<FeedbackEntity> CreateFeedbackAsync(FeedbackEntity feedback)
        {
            _context.Feedbacks.Add(feedback);
            await _context.SaveChangesAsync();
            return feedback;
        }

        /// <summary>
        /// Get feedback by ID
        /// </summary>
        public async Task<FeedbackEntity?> GetFeedbackByIdAsync(Guid id)
        {
            return await _context.Feedbacks
                .Include(f => f.User)
                .FirstOrDefaultAsync(f => f.Id == id);
        }

        /// <summary>
        /// Get all feedback by user ID
        /// </summary>
        public async Task<List<FeedbackSummary>> GetFeedbackByUserIdAsync(Guid userId)
        {
            return await _context.Feedbacks
                .Where(f => f.UserId == userId)
                .OrderByDescending(f => f.CreatedAtUtc)
                .Select(f => new FeedbackSummary
                {
                    Id = f.Id,
                    Subject = f.Subject,
                    Category = f.Category,
                    Status = f.Status,
                    CreatedAtUtc = f.CreatedAtUtc,
                    HasResponse = !string.IsNullOrEmpty(f.Response)
                })
                .ToListAsync();
        }

        /// <summary>
        /// Get all feedback (admin only)
        /// </summary>
        public async Task<List<FeedbackEntity>> GetAllFeedbackAsync(int skip = 0, int take = 50)
        {
            return await _context.Feedbacks
                .Include(f => f.User)
                .OrderByDescending(f => f.CreatedAtUtc)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }

        /// <summary>
        /// Get feedback by status (admin only)
        /// </summary>
        public async Task<List<FeedbackEntity>> GetFeedbackByStatusAsync(string status, int skip = 0, int take = 50)
        {
            return await _context.Feedbacks
                .Include(f => f.User)
                .Where(f => f.Status == status)
                .OrderByDescending(f => f.CreatedAtUtc)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }

        /// <summary>
        /// Update feedback status and response
        /// </summary>
        public async Task<FeedbackEntity?> UpdateFeedbackAsync(Guid id, string status, string? adminNotes, string? response)
        {
            var feedback = await _context.Feedbacks.FindAsync(id);
            if (feedback == null)
            {
                return null;
            }

            feedback.Status = status;
            feedback.AdminNotes = adminNotes;
            feedback.UpdatedAtUtc = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(response))
            {
                feedback.Response = response;
                feedback.RespondedAtUtc = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return feedback;
        }

        /// <summary>
        /// Delete feedback (admin only)
        /// </summary>
        public async Task<bool> DeleteFeedbackAsync(Guid id)
        {
            var feedback = await _context.Feedbacks.FindAsync(id);
            if (feedback == null)
            {
                return false;
            }

            _context.Feedbacks.Remove(feedback);
            await _context.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Get feedback count by status (for dashboard)
        /// </summary>
        public async Task<Dictionary<string, int>> GetFeedbackCountByStatusAsync()
        {
            return await _context.Feedbacks
                .GroupBy(f => f.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Status, x => x.Count);
        }
    }
}
