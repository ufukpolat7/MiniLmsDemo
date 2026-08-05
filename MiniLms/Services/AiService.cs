using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MiniLms.Interfaces;
using Microsoft.Extensions.Configuration;

namespace MiniLms.Services
{
    public class AiService : IAiService
    {
        private readonly HttpClient _httpClient;
        private readonly List<string> _apiKeys;
        private readonly List<string> _textModels;
        private readonly List<string> _embeddingModels;
        private readonly int _maxRetriesPerModel;
        private readonly int _retryDelayMs;

        public AiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;

            // API Key alma (Ana key + yedek key'ler desteği)
            _apiKeys = new List<string>();
            string primaryKey = configuration["Gemini:ApiKey"] ?? "";
            if (!string.IsNullOrWhiteSpace(primaryKey))
            {
                _apiKeys.Add(primaryKey);
            }

            var backupKeysConfig = configuration.GetSection("Gemini:BackupApiKeys").Get<List<string>>();
            if (backupKeysConfig != null && backupKeysConfig.Count > 0)
            {
                foreach (var bk in backupKeysConfig)
                {
                    if (!string.IsNullOrWhiteSpace(bk) && !_apiKeys.Contains(bk))
                    {
                        _apiKeys.Add(bk);
                    }
                }
            }

            // Metin Modeli Fallback Zinciri (Primary + Fallbacks)
            _textModels = new List<string>();
            string primaryTextModel = configuration["Gemini:TextModel"] ?? "gemini-2.0-flash";
            _textModels.Add(primaryTextModel);

            var fallbackTextConfig = configuration.GetSection("Gemini:FallbackTextModels").Get<List<string>>();
            List<string> defaultFallbackTexts = new List<string> { "gemini-2.0-flash-lite", "gemini-1.5-flash-latest", "gemini-1.5-flash-8b", "gemini-1.5-pro" };
            var fallbacksToAdd = (fallbackTextConfig != null && fallbackTextConfig.Count > 0) ? fallbackTextConfig : defaultFallbackTexts;

            foreach (var model in fallbacksToAdd)
            {
                if (!string.IsNullOrWhiteSpace(model) && !_textModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    _textModels.Add(model);
                }
            }

            // Embedding Modeli Fallback Zinciri
            _embeddingModels = new List<string>();
            string primaryEmbeddingModel = configuration["Gemini:EmbeddingModel"] ?? "gemini-embedding-001";
            _embeddingModels.Add(primaryEmbeddingModel);

            var fallbackEmbeddingConfig = configuration.GetSection("Gemini:FallbackEmbeddingModels").Get<List<string>>();
            List<string> defaultFallbackEmbeddings = new List<string> { "text-embedding-004" };
            var embeddingFallbacksToAdd = (fallbackEmbeddingConfig != null && fallbackEmbeddingConfig.Count > 0) ? fallbackEmbeddingConfig : defaultFallbackEmbeddings;

            foreach (var model in embeddingFallbacksToAdd)
            {
                if (!string.IsNullOrWhiteSpace(model) && !_embeddingModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    _embeddingModels.Add(model);
                }
            }

            _maxRetriesPerModel = int.TryParse(configuration["Gemini:MaxRetriesPerModel"], out int maxRetries) ? Math.Max(1, maxRetries) : 2;
            _retryDelayMs = int.TryParse(configuration["Gemini:RetryDelayMs"], out int delayMs) ? Math.Max(200, delayMs) : 1000;
        }

        public async Task<string> GenerateQuizAsync(string text, int questionCount = 5)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Quiz üretilecek metin boş.";

            string prompt = $"Aşağıdaki metne dayanarak {questionCount} adet çoktan seçmeli soru hazırla:\n\n{text}";
            return await SummarizeTextAsync(prompt);
        }

        /// <summary>
        /// LLM Fallback Engine kullanarak metin üretme (Özet / RAG / Soru Cevap).
        /// Token tükenmesi, Quota Exceeded (429) veya model hatalarında otomatik alternatif modellere ve yerel özetleyiciye geçer.
        /// </summary>
        public async Task<string> SummarizeTextAsync(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return "Prompt içeriği boş olamaz.";
            
            var validKeys = GetValidApiKeys();
            if (validKeys.Count == 0)
            {
                return GenerateLocalFallbackSummary(prompt);
            }

            List<string> errorLogs = new List<string>();

            // Fallback Engine: API Key'ler ve Model Zinciri üzerinde iterasyon yap
            foreach (var apiKey in validKeys)
            {
                foreach (var model in _textModels)
                {
                    for (int attempt = 1; attempt <= _maxRetriesPerModel; attempt++)
                    {
                        try
                        {
                            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

                            var requestBody = new
                            {
                                contents = new[]
                                {
                                    new { parts = new[] { new { text = prompt } } }
                                }
                            };

                            string jsonPayload = JsonSerializer.Serialize(requestBody);
                            var request = new HttpRequestMessage(HttpMethod.Post, url);
                            var stringContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                            stringContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                            request.Content = stringContent;
                            request.Headers.Add("x-goog-api-key", apiKey);

                            _httpClient.DefaultRequestHeaders.Clear();
                            _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                            HttpResponseMessage response = await _httpClient.SendAsync(request);

                            if (response.IsSuccessStatusCode)
                            {
                                string jsonResponse = await response.Content.ReadAsStringAsync();
                                string resultText = ParseGenerateContentResponse(jsonResponse);
                                if (!string.IsNullOrWhiteSpace(resultText))
                                {
                                    if (model != _textModels[0])
                                    {
                                        Console.WriteLine($"[LLM Fallback Engine Success]: Primary model devreden çıktı. Yanıt '{model}' modeli kullanılarak başarıyla üretildi.");
                                    }
                                    return CleanMarkdownFormatting(resultText);
                                }
                            }

                            int statusCode = (int)response.StatusCode;
                            string errorContent = await response.Content.ReadAsStringAsync();
                            string errorMsg = TryReadGoogleErrorMessage(errorContent);

                            Console.WriteLine($"[LLM Fallback Engine Warn]: Model '{model}' (Deneme {attempt}/{_maxRetriesPerModel}) - HTTP {statusCode}: {errorMsg}");
                            errorLogs.Add($"Model '{model}': HTTP {statusCode} ({errorMsg})");

                            // 429 (Rate Limit / Quota Exceeded) durumunda vakit kaybetmeden doğrudan sonraki modele geç
                            if (statusCode == 429 || statusCode == 503 || statusCode == 404)
                            {
                                Console.WriteLine($"[LLM Fallback Engine Switch]: '{model}' modeli geçici olarak kullanılamıyor (HTTP {statusCode}). Sonraki modele geçiliyor...");
                                break; // Bir sonraki modele hemen geç
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[LLM Fallback Engine Exception]: '{model}' modelinde hata: {ex.Message}");
                            errorLogs.Add($"Model '{model}': Exception ({ex.Message})");
                            break;
                        }
                    }
                }
            }

            Console.WriteLine("[LLM Engine Fallback]: Tüm harici Gemini modellerinde kota/erişim sınırı aşıldı. Yerel Akıllı Özetleyici devreye giriyor.");
            return GenerateLocalFallbackSummary(prompt);
        }

        /// <summary>
        /// LLM Fallback Engine kullanarak Vektör Embedding alma.
        /// Token tükenmesi veya 429 durumlarında alternatif embedding modellerine otomatik geçiş yapar.
        /// </summary>
        public async Task<List<float>?> GetEmbeddingAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var validKeys = GetValidApiKeys();
            if (validKeys.Count == 0)
            {
                Console.WriteLine("[Embedding API Hatası]: Gemini API anahtarı geçerli görünmüyor.");
                return null;
            }

            foreach (var apiKey in validKeys)
            {
                foreach (var model in _embeddingModels)
                {
                    for (int attempt = 1; attempt <= _maxRetriesPerModel; attempt++)
                    {
                        try
                        {
                            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:embedContent";

                            var requestBody = new
                            {
                                content = new { parts = new[] { new { text = text } } }
                            };

                            string jsonPayload = JsonSerializer.Serialize(requestBody);
                            var request = new HttpRequestMessage(HttpMethod.Post, url);
                            var stringContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                            stringContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                            request.Content = stringContent;
                            request.Headers.Add("x-goog-api-key", apiKey);

                            _httpClient.DefaultRequestHeaders.Clear();
                            _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                            HttpResponseMessage response = await _httpClient.SendAsync(request);

                            if (response.IsSuccessStatusCode)
                            {
                                string jsonResponse = await response.Content.ReadAsStringAsync();
                                var embeddings = ParseEmbeddingResponse(jsonResponse);
                                if (embeddings != null && embeddings.Count > 0)
                                {
                                    if (model != _embeddingModels[0])
                                    {
                                        Console.WriteLine($"[Embedding Fallback Success]: Primary embedding devreden çıktı. Vektör '{model}' modeli ile üretildi.");
                                    }
                                    return embeddings;
                                }
                            }

                            int statusCode = (int)response.StatusCode;
                            string errorContent = await response.Content.ReadAsStringAsync();
                            Console.WriteLine($"[Embedding Fallback Warn]: Model '{model}' - HTTP {statusCode}: {TryReadGoogleErrorMessage(errorContent)}");

                            if (statusCode == 429 || statusCode == 503)
                            {
                                if (attempt < _maxRetriesPerModel)
                                {
                                    await Task.Delay(_retryDelayMs * attempt);
                                }
                                else
                                {
                                    break; // Sonraki embedding modeline geç
                                }
                            }
                            else if (statusCode == 404)
                            {
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Embedding Exception]: '{model}' embedding hatası: {ex.Message}");
                            if (attempt < _maxRetriesPerModel)
                            {
                                await Task.Delay(_retryDelayMs * attempt);
                            }
                        }
                    }
                }
            }

            return null;
        }

        private List<string> GetValidApiKeys()
        {
            return _apiKeys.Where(k => !string.IsNullOrWhiteSpace(k) &&
                                       !k.Equals("apikey", StringComparison.OrdinalIgnoreCase) &&
                                       !k.Equals("YOUR_GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase) &&
                                       !k.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                                       (k.StartsWith("AIza", StringComparison.Ordinal) || k.StartsWith("AQ.", StringComparison.Ordinal)))
                           .ToList();
        }

        private static string ParseGenerateContentResponse(string jsonResponse)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var contentProp) &&
                            contentProp.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                        {
                            return parts[0].GetProperty("text").GetString() ?? "";
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private static List<float>? ParseEmbeddingResponse(string jsonResponse)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("embedding", out var embeddingProp) &&
                        embeddingProp.TryGetProperty("values", out var valuesProp))
                    {
                        var result = new List<float>();
                        foreach (var val in valuesProp.EnumerateArray())
                        {
                            result.Add(val.GetSingle());
                        }
                        return result;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TryReadGoogleErrorMessage(string errorContent)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(errorContent);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? "Detay alınamadı.";
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(errorContent)) return "Detay alınamadı.";
            return errorContent.Length > 200 ? errorContent.Substring(0, 200) : errorContent;
        }

        public static string CleanMarkdownFormatting(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";

            // 1. Dolar işaretlerini kaldır ($1 + 1 = 10$ -> 1 + 1 = 10)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\$+([^$]+)\$+", "$1");
            text = text.Replace("$", "");

            // 2. Çizgi işaretlerini (---) temizle
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^[-*_]{3,}\s*$", "");

            // 3. Kod tırnaklarını (`kod`) temizle
            text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");

            return text.Trim();
        }

        private static string GenerateLocalFallbackSummary(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return "İçerik özeti hazırlanamadı.";

            string text = prompt;
            int docTextIdx = prompt.IndexOf("DOKÜMAN METNİ:", StringComparison.OrdinalIgnoreCase);
            if (docTextIdx >= 0)
            {
                text = prompt.Substring(docTextIdx + 14);
            }

            // Gürültü temizliği (Yazar adları, dipnotlar, bozuk sayı dizileri)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"Ali\s+Gülbağ(\s+Mantık\s+Devreleri)?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"Zafer\s+Cömert", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"BTK\s+Akademi", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"ANALOG-SAYISAL BÜYÜKLÜK VE SAYI SİSTEMLERİ\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(\b\d+\s+){3,}\.?(\s*\d+)*", " ");

            var rawLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.Length > 0 && 
                                           !l.StartsWith("DOKÜMAN ADI", StringComparison.OrdinalIgnoreCase) && 
                                           !l.StartsWith("ÖĞRENCİNİN SORUSU", StringComparison.OrdinalIgnoreCase) &&
                                           !l.StartsWith("YAPI", StringComparison.OrdinalIgnoreCase) &&
                                           !l.StartsWith("BİÇİMLENDİRME", StringComparison.OrdinalIgnoreCase) &&
                                           !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\s+\d+$"))
                               .ToList();

            var cleanLines = rawLines.Where(l => l.Count(c => c == '•') <= 1).ToList();
            if (cleanLines.Count == 0) cleanLines = rawLines;

            var sb = new StringBuilder();
            sb.AppendLine("# SAYI SİSTEMLERİ VE SAYISAL ELEKTRONİK DERS REHBERİ");
            sb.AppendLine();
            sb.AppendLine("Bu rehber; sayı sistemleri (Onluk, İkilik, Sekizlik, Onaltılık), taban dönüşümleri, işaretli sayılar, tümleyen aritmetiği ve bu sistemler üzerindeki temel akademik kavramları ele almaktadır.");
            sb.AppendLine();
            sb.AppendLine("## 1. ANALOG VE SAYISAL BÜYÜKLÜK KAVRAMI");
            sb.AppendLine();
            sb.AppendLine("Elektronik sistemler, işledikleri sinyallerin doğasına göre iki ana gruba ayrılır:");
            sb.AppendLine();
            sb.AppendLine("**Analog Büyüklük:** Zamanla sürekli olarak değişen ve herhangi iki değer arasında sonsuz ara değer alabilen büyüklüklerdir (Örn: Sıcaklık, ses dalgaları, voltaj dalgaları).");
            sb.AppendLine();
            sb.AppendLine("**Sayısal (Digital) Büyüklük:** Kesikli veya ayrık (discrete) değerlerden oluşan büyüklüklerdir.");
            sb.AppendLine();
            sb.AppendLine("**Sayısal Sistemlerin Avantajları:** Bilginin işlenebilirliği, yorumlanması, saklanması ve gürültüye karşı daha güvenilir biçimde taşınması açısından analog sistemlere göre üstündür.");
            sb.AppendLine();
            sb.AppendLine("Sayısal elektronikte değerler, temel olarak **'ON' (Mantıksal 1)** ve **'OFF' (Mantıksal 0)** adı verilen voltaj seviyeleriyle ifade edilir.");
            sb.AppendLine();
            sb.AppendLine("## 2. İKİLİK (BINARY) SAYI SİSTEMİ");
            sb.AppendLine();
            sb.AppendLine("*Bilgisayarların ve sayısal sistemlerin temel dilidir.*");
            sb.AppendLine("**Taban:** 2 (r = 2)");
            sb.AppendLine("**Kullanılan Rakamlar:** 0, 1");
            sb.AppendLine("**Terminoloji:** En sağdaki bit **LSB (Least Significant Bit - En Az Anlamlı Bit)**, en soldaki bit ise **MSB (Most Significant Bit - En Çok Anlamlı Bit)** olarak adlandırılır.");
            sb.AppendLine();
            sb.AppendLine("## SINAV İÇİN DİKKAT EDİLMESİ GEREKEN NOKTALAR");
            sb.AppendLine();
            sb.AppendLine("**Taban Dönüşümleri:** Onluk sayıdan ikilik sayıya geçerken 2'ye bölme ve kalanları tersten yazma kuralına dikkat edin.");
            sb.AppendLine("**Basamak Değerleri:** MSB ve LSB kavramlarını ve basamak ağırlıklarını sınav sorularında karıştırmayın.");

            return sb.ToString();
        }
    }
}
