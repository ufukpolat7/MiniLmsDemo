using MiniLms.Interfaces;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiniLms.Services
{
    public class VectorDbService : IVectorDbService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<VectorDbService> _logger;
        private readonly string _baseUrl;
        private readonly string? _apiKey;

        public VectorDbService(HttpClient httpClient, ILogger<VectorDbService> logger, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            
            _baseUrl = (configuration["Qdrant:Url"] ?? "http://localhost:6333").TrimEnd('/');
            _apiKey = configuration["Qdrant:ApiKey"];

            if (!string.IsNullOrWhiteSpace(_apiKey) && !_httpClient.DefaultRequestHeaders.Contains("api-key"))
            {
                _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
            }
        }

        public async Task<bool> DeleteVectorAsync(string pointId)
        {
            try
            {
                // Qdrant API'sine ilgili pointId için DELETE isteği atılır
                string url = $"{_baseUrl}/collections/MiniLmsCollection/points/delete";
                var payload = new { points = new[] { pointId } };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Qdrant silme işlemi sırasında hata oluştu.");
                return false;
            }
        }

        public async Task EnsureCollectionExistsAsync(string collectionName)
        {
            var checkResponse = await _httpClient.GetAsync($"{_baseUrl}/collections/{collectionName}");
            if (checkResponse.IsSuccessStatusCode) return; // Koleksiyon zaten var

            // Gemini embedding modeli 768 boyutludur ve mesafe ölçümü için Cosine en idealidir
            var createPayload = new
            {
                vectors = new { size = 768, distance = "Cosine" }
            };

            var content = new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json");
            await _httpClient.PutAsync($"{_baseUrl}/collections/{collectionName}", content);
        }

        public async Task SaveVectorAsync(string collectionName, int contentId, int lessonId, List<float> vector, string originalText)
        {
            await EnsureCollectionExistsAsync(collectionName);

            var uploadPayload = new
            {
                points = new[]
                {
                    new
                    {
                        id = Guid.NewGuid().ToString(), // Benzersiz nokta ID'si
                        vector = vector,
                        payload = new
                        {
                            contentId = contentId,
                            lessonId = lessonId,
                            text = originalText
                        }
                    }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(uploadPayload), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync($"{_baseUrl}/collections/{collectionName}/points?wait=true", content);
        }

        public async Task<List<string>> SearchSimilarTextsAsync(string collectionName, List<float> vectorData, int limit = 3)
        {
            try
            {
                // Qdrant arama endpoint'i
                string url = $"{_baseUrl}/collections/{collectionName}/points/search";

                var requestBody = new
                {
                    vector = vectorData, // Çakışmayı önlemek için 'vectorData' kullandık
                    limit = limit,
                    with_payload = true
                };

                string jsonPayload = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError(
                    "Qdrant arama hatası. StatusCode: {StatusCode}, Error: {Error}",
                    response.StatusCode,
                    errorContent);
                    return new List<string>();
                }

                string jsonResponse = await response.Content.ReadAsStringAsync();
                var resultList = new List<string>();

                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var point in resultProp.EnumerateArray())
                        {
                            if (point.TryGetProperty("payload", out var payloadProp) &&
                                payloadProp.TryGetProperty("text", out var textProp))
                            {
                                var text = textProp.GetString();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    resultList.Add(text);
                                }
                            }
                        }
                    }
                }

                return resultList;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Qdrant arama sırasında beklenmeyen hata oluştu.");
                return new List<string>();
            }
        }

        public Task<List<string>> SearchSimilarTextsAsync(string collectionName, List<float> queryVector, int lessonId, int limit = 3, List<float>? vectorData = null)
        {
            return SearchSimilarTextsAsync(collectionName, queryVector, limit);
        }

        public async Task<bool> DeleteVectorAsync(string collectionName, List<long> pointIds)
        {
            try
            {
                if (pointIds == null || pointIds.Count == 0) return true;

                string url = $"{_baseUrl}/collections/{collectionName}/points/delete";
                var payload = new { points = pointIds };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Qdrant vektör silme işlemi sırasında hata oluştu.");
                return true; // Qdrant kapalı olsa dahi veritabanı doküman silmesini engelleme
            }
        }

        public async Task<bool> DeleteVectorAsync(List<long> pointIds)
        {
            return await DeleteVectorAsync("lesson_contents", pointIds);
        }
    }
}
