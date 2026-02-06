using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
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
                            Name = card.Name ?? string.Empty,
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

            // Clear existing commanders and insert new ones
            
            _dbContext.Commanders.RemoveRange(_dbContext.Commanders);
            await _dbContext.Commanders.AddRangeAsync(commanders);
            await _dbContext.SaveChangesAsync();

            return commanders.Count;
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