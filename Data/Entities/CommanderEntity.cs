using System;
using System.ComponentModel.DataAnnotations;

namespace SessionApp.Data.Entities
{
    public class CommanderEntity
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(300)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(500)]
        public string ScryfallUri { get; set; } = string.Empty;

        [Required]
        public string LegalitiesJson { get; set; } = "{}";

        public DateTime LastUpdatedUtc { get; set; }
    }
}