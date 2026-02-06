using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SessionApp.Data;
using SessionApp.Data.Entities;

namespace SessionApp.Services
{
    public class ScryfallService
    {
        private readonly HttpClient _httpClient;
        private readonly SessionDbContext _dbContext;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Use the same pattern as SafeStringAttribute for normalization
        private static readonly Regex SafePattern = new Regex(@"^[a-zA-Z0-9\s\-_\.,'!@#&()\[\]:]+$", RegexOptions.Compiled);

        public ScryfallService(HttpClient httpClient, SessionDbContext dbContext)
        {
            _httpClient = httpClient;
            _dbContext = dbContext;
        }

        public async Task<int> FetchAndStoreCommandersAsync()
        {
            var commanders = new List<CommanderEntity>();
            var url = "https://api.scryfall.com/cards/search?q=is:commander";
           
            while (!string.IsNullOrEmpty(url))
            {
                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"Scryfall API returned {response.StatusCode}. Response: {errorContent}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ScryfallResponse>(json, JsonOptions);

                if (result?.Data != null)
                {
                    foreach (var card in result.Data)
                    {
                        commanders.Add(new CommanderEntity
                        {
                            Id = card.Id,
                            Name = NormalizeCommanderName(card.Name ?? string.Empty),
                            ScryfallUri = card.ScryfallUri ?? string.Empty,
                            LegalitiesJson = JsonSerializer.Serialize(card.Legalities ?? new Dictionary<string, string>()),
                            LastUpdatedUtc = DateTime.UtcNow
                        });
                    }
                }

                url = result?.NextPage;

                // Respect Scryfall's rate limiting (50-100ms between requests)
                await Task.Delay(100);
            }

            // Load existing commanders into a dictionary for quick lookup
            var existingCommanders = await _dbContext.Commanders
                .ToDictionaryAsync(c => c.Id, c => c);

            int addedCount = 0;
            int updatedCount = 0;
            int skippedCount = 0;

            foreach (var commander in commanders)
            {
                if (existingCommanders.TryGetValue(commander.Id, out var existing))
                {
                    // Commander exists - check if update is needed
                    bool needsUpdate = existing.Name != commander.Name ||
                                     existing.ScryfallUri != commander.ScryfallUri ||
                                     existing.LegalitiesJson != commander.LegalitiesJson;

                    if (needsUpdate)
                    {
                        existing.Name = commander.Name;
                        existing.ScryfallUri = commander.ScryfallUri;
                        existing.LegalitiesJson = commander.LegalitiesJson;
                        existing.LastUpdatedUtc = DateTime.UtcNow;
                        updatedCount++;
                    }
                    else
                    {
                        // Update timestamp even if no changes to track sync
                        existing.LastUpdatedUtc = DateTime.UtcNow;
                        skippedCount++;
                    }
                }
                else
                {
                    // New commander - add it
                    _dbContext.Commanders.Add(commander);
                    addedCount++;
                }
            }

            await _dbContext.SaveChangesAsync();

            return commanders.Count;
        }

        /// <summary>
        /// Normalizes commander names using the SafeString validation pattern.
        /// Removes invalid characters and trims whitespace.
        /// </summary>
        private string NormalizeCommanderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            // Trim whitespace
            var normalized = name.Trim();

            // If the name already matches the safe pattern, return it
            if (SafePattern.IsMatch(normalized))
                return normalized;

            // Remove any characters that don't match the safe pattern
            // Keep only alphanumeric, spaces, and safe punctuation
            var sanitized = Regex.Replace(normalized, @"[^a-zA-Z0-9\s\-_\.,'!@#&()\[\]:]", string.Empty);

            // Remove multiple consecutive spaces
            sanitized = Regex.Replace(sanitized, @"\s+", " ");

            return sanitized.Trim();
        }

        private class ScryfallResponse
        {
            [JsonPropertyName("data")]
            public List<ScryfallCard>? Data { get; set; }

            [JsonPropertyName("has_more")]
            public bool HasMore { get; set; }

            [JsonPropertyName("next_page")]
            public string? NextPage { get; set; }
        }

        private class ScryfallCard
        {
            [JsonPropertyName("id")]
            public Guid Id { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("scryfall_uri")]
            public string? ScryfallUri { get; set; }

            [JsonPropertyName("legalities")]
            public Dictionary<string, string>? Legalities { get; set; }
        }
    }
}