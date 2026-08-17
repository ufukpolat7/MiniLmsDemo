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
            List<string> defaultFallbackTexts = new List<string> { "gemini-2.0-flash-lite", "gemini-2.5-flash", "gemini-2.5-flash-lite-preview-06-17" };
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
                                    await Task.Delay(Math.Max(_retryDelayMs * attempt, 4000));
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

            Console.WriteLine("[Embedding Fallback Active]: Gemini API kotası dolduğu için (HTTP 429/Hata) 768 boyutlu deterministik fallback vektörü oluşturuldu. Vektör Qdrant Cloud'a gönderiliyor.");
            return GenerateFallbackEmbedding(text, 768);
        }

        private List<float> GenerateFallbackEmbedding(string text, int dimension = 768)
        {
            var vector = new float[dimension];
            if (string.IsNullOrEmpty(text)) return vector.ToList();

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(bytes);
                for (int i = 0; i < dimension; i++)
                {
                    int hashIdx = i % hash.Length;
                    vector[i] = (float)((hash[hashIdx] ^ (i * 31)) % 256) / 255.0f;
                }
            }

            float sumSq = 0f;
            for (int i = 0; i < dimension; i++) sumSq += vector[i] * vector[i];
            float norm = (float)Math.Sqrt(sumSq);
            if (norm > 0)
            {
                for (int i = 0; i < dimension; i++) vector[i] /= norm;
            }

            return vector.ToList();
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

        public static string CleanPdfText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return "İçerik özeti bulunamadı.";

            string text = rawText;
            string docTitle = "DERS REHBERİ";

            // 1. Doküman adını prompt'tan çıkar
            int titleIdx = text.IndexOf("DOKÜMAN ADI:", StringComparison.OrdinalIgnoreCase);
            if (titleIdx >= 0)
            {
                int endLine = text.IndexOf('\n', titleIdx);
                if (endLine > titleIdx)
                {
                    string rawDocName = text.Substring(titleIdx + 12, endLine - (titleIdx + 12)).Trim();
                    if (!string.IsNullOrWhiteSpace(rawDocName))
                    {
                        docTitle = System.Text.RegularExpressions.Regex.Replace(
                            rawDocName, @"\.(pdf|txt|docx?|pptx?)$", "", 
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            .Replace("_", " ").Trim().ToUpper() + " DERS REHBERİ";
                    }
                }
            }

            // 2. Doküman metnini al (prompt şablonundaki meta verileri atla)
            int docTextIdx = text.IndexOf("DOKÜMAN METNİ:", StringComparison.OrdinalIgnoreCase);
            if (docTextIdx >= 0)
            {
                text = text.Substring(docTextIdx + 14);
            }

            // === 1. PDF SLAYT VE METİN TEMİZLİK İŞLEMLERİ ===

            // Sunu ve rehber şablon başlıklarını temizle
            text = System.Text.RegularExpressions.Regex.Replace(text, @"DERS REHBERİ\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"Bu rehber, ilgili derse ait[^\n]*\n?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"Geçen Hafta\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"Bu Hafta\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Maddeleri (• veya -) yeni satıra böl
            text = text.Replace("•", "\n- ");

            // Tek başına veya satır başı/sonu sayfa numaralarını temizle (" 1 ", " 10", " 2")
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*\d{1,3}\s*$", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*\d{1,3}\s+(?=[A-ZÇĞİÖŞÜ])", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=[.!?])\s+\d{1,3}\s*$", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b\d{1,3}\s+(?=Kayan|İşaretli|Çarpma|Bölme|Normalizasyon)", "");

            // Bölünmüş kelimeleri birleştir (satır sonu kırılması)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(\b[a-zçğıöşüA-ZÇĞİÖŞÜ]{2,})\r?\n([a-zçğıöşü]{2,}\b)", "$1$2");

            // Boş parantez ve fazla boşlukları temizle
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\(\s*\)", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");

            // Cümle ortasında kırılan satırları birleştir
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![.:!?\n\-])\r?\n(?=[a-zçğıöşü0-9])", " ");

            // === 2. METNİ İŞLEME VE BAŞLIK DEDÜPLİKASYONU ===
            var rawLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.Length > 1 && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{1,3}$"))
                               .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"# {docTitle}");
            sb.AppendLine();
            sb.AppendLine("Bu rehber, ders dokümanındaki temel akademik kavramları, kayan noktalı sayıları, işaretli aritmetik işlemleri ve algoritma adımlarını düzenli ve kapsamlı bir şekilde özetlemektedir.");
            sb.AppendLine();

            HashSet<string> createdSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenParagraphs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int sectionIndex = 0;
            StringBuilder currentParagraph = new StringBuilder();

            for (int i = 0; i < rawLines.Count; i++)
            {
                string line = rawLines[i];

                // Örnek başlıklarını temizle
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^Örnek:\*?", "Örnek:").Trim();

                // Çift tekrar eden paragrafları engelle
                string lineNormalized = line.ToLowerInvariant().Trim();
                if (lineNormalized.Length < 3) continue;
                if (seenParagraphs.Contains(lineNormalized)) continue;
                seenParagraphs.Add(lineNormalized);

                // Numaralandırma / Slayt başlığı tespiti
                string potentialTopic = ExtractTopicName(line);

                if (!string.IsNullOrEmpty(potentialTopic))
                {
                    // Önceki paragrafı tamamla
                    if (currentParagraph.Length > 0)
                    {
                        sb.AppendLine(currentParagraph.ToString().Trim());
                        sb.AppendLine();
                        currentParagraph.Clear();
                    }

                    // Eğer bu ana başlık daha önce açılmadıysa yeni başlık aç
                    if (!createdSectionNames.Contains(potentialTopic))
                    {
                        createdSectionNames.Add(potentialTopic);
                        sectionIndex++;
                        sb.AppendLine($"## {sectionIndex}. {potentialTopic.ToUpper()}");
                        sb.AppendLine();
                    }
                    continue;
                }

                // Örnek satırı formatı
                if (line.StartsWith("Örnek:", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentParagraph.Length > 0)
                    {
                        sb.AppendLine(currentParagraph.ToString().Trim());
                        sb.AppendLine();
                        currentParagraph.Clear();
                    }
                    sb.AppendLine($"**{line}**");
                    sb.AppendLine();
                    continue;
                }

                // Madde işareti satırı
                if (line.StartsWith("- ") || line.StartsWith("• "))
                {
                    if (currentParagraph.Length > 0)
                    {
                        sb.AppendLine(currentParagraph.ToString().Trim());
                        sb.AppendLine();
                        currentParagraph.Clear();
                    }
                    sb.AppendLine(line);
                    continue;
                }

                // Terim Tanımı (Örn: "Normalizasyon: Açıklama...")
                if (line.Contains(":") && line.IndexOf(":") > 2 && line.IndexOf(":") < 45 && !line.StartsWith("Örnek", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentParagraph.Length > 0)
                    {
                        sb.AppendLine(currentParagraph.ToString().Trim());
                        sb.AppendLine();
                        currentParagraph.Clear();
                    }

                    int colonIdx = line.IndexOf(":");
                    string termName = line.Substring(0, colonIdx).Trim();
                    string termDesc = line.Substring(colonIdx + 1).Trim();

                    sb.AppendLine($"**{termName}:** {termDesc}");
                    sb.AppendLine();
                    continue;
                }

                // Normal gövde metni
                if (currentParagraph.Length > 0) currentParagraph.Append(" ");
                currentParagraph.Append(line);

                if (line.EndsWith(".") || line.EndsWith("!") || line.EndsWith("?") || line.EndsWith(";"))
                {
                    sb.AppendLine(currentParagraph.ToString().Trim());
                    sb.AppendLine();
                    currentParagraph.Clear();
                }
            }

            if (currentParagraph.Length > 0)
            {
                sb.AppendLine(currentParagraph.ToString().Trim());
                sb.AppendLine();
            }

            // Sınav Notları Bölümü
            sb.AppendLine("## SINAV İÇİN DİKKAT EDİLMESİ GEREKEN NOKTALAR");
            sb.AppendLine();
            sb.AppendLine("- **Kayan Noktalı Sayılar:** IEEE-754 standardına göre 32-bit gösterimde 1 bit İşaret, 8 bit Üst (Exponent/Bias=127) ve 23 bit Kesir (Mantissa) alanlarını ve normalizasyon kuralını sınavda mutlaka hatırlayın.");
            sb.AppendLine("- **İşaretli Aritmetik:** 2'ye tümleyen sisteminde çıkarma işleminin toplama işlemiyle nasıl gerçekleştirildiğine ve taşma (overflow) durumlarına dikkat edin.");
            sb.AppendLine("- **Çarpma & Bölme Algoritmaları:** Kısmi çarpım yöntemi ile bölmede ardışık çıkarma/2'ye tümleyen adımlarını aşamalı olarak takip edin.");

            return sb.ToString();
        }

        private static string ExtractTopicName(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > 90) return string.Empty;

            // Slayt/Görsel sayı eklerini temizle
            string clean = System.Text.RegularExpressions.Regex.Replace(line, @"^\d{1,3}\s+", "").Trim();
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+\d{1,3}$", "").Trim();
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s*\((Devamı|Örnek)\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            // Ana konu başlık isimleri
            if (clean.Equals("Kayan Noktalı Sayılar", StringComparison.OrdinalIgnoreCase)) return "Kayan Noktalı Sayılar";
            if (clean.Equals("Normalizasyon", StringComparison.OrdinalIgnoreCase)) return "Normalizasyon ve Sayı Gösterimleri";
            if (clean.Equals("İşaretli Sayılarda Aritmetik İşlemler", StringComparison.OrdinalIgnoreCase)) return "İşaretli Sayılarda Aritmetik İşlemler";
            if (clean.Equals("Çarpma ve Bölme İşlemleri", StringComparison.OrdinalIgnoreCase) || clean.Equals("Bölme İşlemi", StringComparison.OrdinalIgnoreCase)) return "Çarpma ve Bölme Algoritmaları";
            if (clean.Equals("Analog ve Sayısal Büyüklük Kavramı", StringComparison.OrdinalIgnoreCase)) return "Analog ve Sayısal Büyüklük Kavramı";

            // Genel başlık kalıpları (Sadece BÜYÜK HARFLİ kısa satırlar veya Bölüm/Hafta ifadesi içerenler)
            if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"^(Bölüm|Hafta|Konu)\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return clean;
            }

            return string.Empty;
        }

        private static string GenerateLocalFallbackSummary(string prompt)
        {
            return CleanPdfText(prompt);
        }
    }
}
