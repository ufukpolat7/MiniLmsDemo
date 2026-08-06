using MiniLms.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MiniLms.Services
{
    public class AzureSpeechService : IAzureSpeechService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<AzureSpeechService> _logger;

        public AzureSpeechService(
            HttpClient httpClient,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            ILogger<AzureSpeechService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _environment = environment;
            _logger = logger;
        }

        public async Task<string?> GenerateAudioSummaryAsync(string text, int documentId, string userId)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                string cleanText = StripMarkdownAndFormatForSpeech(text);
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    return null;
                }

                string folderPath = Path.Combine(_environment.WebRootPath, "audio_summaries");
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                string relativePath = $"/audio_summaries/summary_{documentId}_{userId}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3";
                string physicalPath = Path.Combine(_environment.WebRootPath, relativePath.TrimStart('/'));

                string voiceName = _configuration["AzureSpeech:VoiceName"] ?? "tr-TR-EmelNeural";

                // 🎯 1. ADIM (BİRİNCİL): Microsoft Edge Free Nöral TTS (%100 Ücretsiz tr-TR-EmelNeural)
                _logger.LogInformation("[Edge Free Neural TTS]: Microsoft Edge Ücretsiz Nöral TTS (tr-TR-EmelNeural) ile MP3 üretiliyor...");
                string? edgeMp3 = await GenerateEdgeNeuralMp3Async(cleanText, physicalPath, relativePath, voiceName);
                if (!string.IsNullOrEmpty(edgeMp3))
                {
                    _logger.LogInformation($"[Edge Free Neural TTS Success]: Ücretsiz Edge Nöral MP3 üretildi: {relativePath}");
                    return relativePath;
                }

                // 🎯 2. ADIM: Azure Key tanımlıysa yedek olarak Azure REST API kullan
                string apiKey = _configuration["AzureSpeech:SubscriptionKey"] ?? "";
                string region = _configuration["AzureSpeech:Region"] ?? "westeurope";
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    _logger.LogInformation("[Azure Speech Backup]: Azure REST API deneniyor...");
                    string ttsUrl = $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1";
                    string ssml = $@"<speak version='1.0' xml:lang='tr-TR'>
    <voice xml:lang='tr-TR' name='{voiceName}'>
        <prosody rate='-3%' pitch='0%'>
            {System.Security.SecurityElement.Escape(cleanText)}
        </prosody>
    </voice>
</speak>";

                    using var request = new HttpRequestMessage(HttpMethod.Post, ttsUrl);
                    request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
                    request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-160kbitrate-mono-mp3");
                    request.Headers.Add("User-Agent", "MiniLmsApp");
                    request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(physicalPath, audioBytes);
                        return relativePath;
                    }
                }

                // 🎯 3. ADIM: Google Türkçe TTS (Yedek)
                _logger.LogInformation("[Google TTS Fallback]: Google Türkçe TTS servisi deneniyor...");
                string? googleMp3Path = await GenerateGoogleTurkishMp3Async(cleanText, physicalPath, relativePath);
                if (!string.IsNullOrEmpty(googleMp3Path))
                {
                    return googleMp3Path;
                }

                // 🎯 4. ADIM: Yerel SAPI (Ağ tamamen kopuksa)
                _logger.LogWarning("[SAPI Fallback]: Yerel Türkçe Filtreli Sistem SAPI kullanılıyor...");
                return GenerateLocalTurkishSapiWav(cleanText, documentId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Speech Exception]: Sesli özet üretilirken hata.");
                return null;
            }
        }

        private async Task<string?> GenerateEdgeNeuralMp3Async(string text, string physicalPath, string relativePath, string voiceName)
        {
            try
            {
                string selectedVoice = string.IsNullOrWhiteSpace(voiceName) ? "tr-TR-EmelNeural" : voiceName;
                
                string tempTxtPath = Path.Combine(Path.GetTempPath(), $"edge_tts_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(tempTxtPath, text, Encoding.UTF8);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c edge-tts --file \"{tempTxtPath}\" --voice {selectedVoice} --write-media \"{physicalPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await proc.WaitForExitAsync(cts.Token);
                }

                if (File.Exists(tempTxtPath))
                {
                    try { File.Delete(tempTxtPath); } catch { }
                }

                if (File.Exists(physicalPath) && new FileInfo(physicalPath).Length > 0)
                {
                    _logger.LogInformation($"[Edge Free Neural TTS Success]: Microsoft Edge '{selectedVoice}' MP3 üretildi ({new FileInfo(physicalPath).Length} bytes): {relativePath}");
                    return relativePath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Edge TTS Error]: Edge Nöral TTS CLI ile ses üretilirken hata oluştu.");
            }
            return null;
        }

        private async Task<string?> GenerateGoogleTurkishMp3Async(string text, string physicalPath, string relativePath)
        {
            try
            {
                var textChunks = SplitIntoSentenceChunks(text, 180);
                using var memoryStream = new MemoryStream();

                foreach (var chunk in textChunks)
                {
                    if (string.IsNullOrWhiteSpace(chunk)) continue;

                    string url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={Uri.EscapeDataString(chunk)}&tl=tr&client=tw-ob";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    var resp = await _httpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        byte[] chunkBytes = await resp.Content.ReadAsByteArrayAsync();
                        await memoryStream.WriteAsync(chunkBytes, 0, chunkBytes.Length);
                    }
                    await Task.Delay(100);
                }

                if (memoryStream.Length > 0)
                {
                    await File.WriteAllBytesAsync(physicalPath, memoryStream.ToArray());
                    _logger.LogInformation($"[Google Turkish TTS Success]: Doğal Türkçe MP3 ses dosyası üretildi: {relativePath}");
                    return relativePath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Google TTS Error]: Google Türkçe TTS ses akışı alınamadı.");
            }
            return null;
        }

        private string? GenerateLocalTurkishSapiWav(string cleanText, int documentId, string userId)
        {
            try
            {
                string relativePath = $"/audio_summaries/summary_{documentId}_{userId}_{Guid.NewGuid().ToString().Substring(0, 8)}.wav";
                string physicalPath = Path.Combine(_environment.WebRootPath, relativePath.TrimStart('/'));

                string escapedText = cleanText.Replace("'", "''").Replace("\"", "`\"");
                string psScript = $@"
Add-Type -AssemblyName System.Speech;
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer;
$v = $s.GetInstalledVoices() | Where-Object {{ 
    $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'tr' -or 
    $_.VoiceInfo.Culture.Name -like 'tr*' -or 
    $_.VoiceInfo.Name -like '*Turkish*' -or 
    $_.VoiceInfo.Name -like '*Tolga*' -or 
    $_.VoiceInfo.Name -like '*Seda*' -or 
    $_.VoiceInfo.Name -like '*Yelda*' 
}} | Select-Object -First 1;
if ($v) {{ 
    $s.SelectVoice($v.VoiceInfo.Name); 
}}
$s.SetOutputToWaveFile('{physicalPath}');
$s.Speak('{escapedText}');
$s.Dispose();
";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(12000);

                if (File.Exists(physicalPath) && new FileInfo(physicalPath).Length > 0)
                {
                    _logger.LogInformation($"[SAPI Speech Success]: Yerel Türkçe WAV üretildi: {relativePath}");
                    return relativePath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SAPI Speech Exception]: Yerel WAV üretilirken hata.");
            }
            return null;
        }

        private static List<string> SplitIntoSentenceChunks(string text, int maxLength)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            string[] sentences = Regex.Split(text, @"(?<=[.!?])\s+");
            StringBuilder currentChunk = new StringBuilder();

            foreach (var sentence in sentences)
            {
                if (currentChunk.Length + sentence.Length + 1 > maxLength)
                {
                    if (currentChunk.Length > 0)
                    {
                        result.Add(currentChunk.ToString().Trim());
                        currentChunk.Clear();
                    }

                    if (sentence.Length > maxLength)
                    {
                        for (int i = 0; i < sentence.Length; i += maxLength)
                        {
                            result.Add(sentence.Substring(i, Math.Min(maxLength, sentence.Length - i)).Trim());
                        }
                    }
                    else
                    {
                        currentChunk.Append(sentence).Append(" ");
                    }
                }
                else
                {
                    currentChunk.Append(sentence).Append(" ");
                }
            }

            if (currentChunk.Length > 0)
            {
                result.Add(currentChunk.ToString().Trim());
            }

            return result;
        }

        private static string StripMarkdownAndFormatForSpeech(string markdownText)
        {
            if (string.IsNullOrWhiteSpace(markdownText)) return string.Empty;

            string clean = markdownText;

            clean = Regex.Replace(clean, @"(?m)^#+\s*", "");
            clean = Regex.Replace(clean, @"\*\*([^*]+)\*\*", "$1");
            clean = Regex.Replace(clean, @"\*([^*]+)\*", "$1");
            clean = Regex.Replace(clean, @"(?m)^\s*[-•*]\s*", "");
            clean = Regex.Replace(clean, @"<[^>]+>", "");

            // 🎯 Akademik Terimleri & Kısaltmaları Türkçe Okunuşlarına Çevirme
            clean = Regex.Replace(clean, @"\bP2P\b", "Pii to Pii", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bTCP/IP\b", "Tee Cee Pee ıı Pee", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bTCP\b", "Tee Cee Pee", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bUDP\b", "Yuu Dee Pee", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bHTTP\b", "Ha Teee Teee Pee", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bHTTPS\b", "Ha Teee Teee Pee Es", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bIP\b", "Ay Pee", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bDNS\b", "Dee En Es", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bCPU\b", "Cee Pee Yuu", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bRAM\b", "Rem", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bPDF\b", "Pee Dee Ef", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bIEEE\b", "I Üçlü E", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bvb\.\b", "ve benzeri", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bvs\.\b", "ve saire", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bms\b", "milisaniye", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bsn\b", "saniye", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\bdk\b", "dakika", RegexOptions.IgnoreCase);

            clean = Regex.Replace(clean, @"DERS REHBERİ\s*", "");
            clean = Regex.Replace(clean, @"Bu rehber, ilgili derse ait[^\n]*\n?", "");

            clean = Regex.Replace(clean, @"\s+", " ").Trim();

            if (clean.Length > 2000)
            {
                int lastSpace = clean.Substring(0, 2000).LastIndexOf(' ');
                clean = (lastSpace > 100 ? clean.Substring(0, lastSpace) : clean.Substring(0, 2000)) + ". Özetin devamını okuyarak inceleyebilirsiniz.";
            }

            return clean;
        }
    }
}
