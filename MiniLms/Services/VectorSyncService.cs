using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniLms.Interfaces;
using MiniLms.Models; // Modelleri kesin olarak tanıması için bu using şarttır
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiniLms.Services
{
    public class VectorSyncService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IVectorDbService _vectorDbService;
        private readonly IAiService _aiService;

        public VectorSyncService(IServiceProvider serviceProvider, IVectorDbService vectorDbService, IAiService aiService)
        {
            _serviceProvider = serviceProvider;
            _vectorDbService = vectorDbService;
            _aiService = aiService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _vectorDbService.EnsureCollectionExistsAsync("lesson_contents");
            }
            catch (Exception)
            {
                // Qdrant sunucusu henüz hazır değilse arka plan görevi patlamasın
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var repo = scope.ServiceProvider.GetRequiredService<ILessonContentRepository>();

                        // Metot çıktısını kesin olarak LessonContent listesi olarak alıyoruz
                        IEnumerable<LessonContent> unIndexedContents = await repo.GetUnIndexedAsync();

                        foreach (LessonContent content in unIndexedContents)
                        {
                            // Modelinizdeki 'Body' veya 'Text' alanlarından hangisini Qdrant'a göndermek isterseniz seçebilirsiniz.
                            // Biz ikisinin de boş olmama ihtimaline karşı dolu olan metni seçiyoruz:
                            string textToEmbed = !string.IsNullOrEmpty(content.Body) ? content.Body : content.Text;

                            if (string.IsNullOrEmpty(textToEmbed)) continue;

                            // Gemini'dan vektör koordinatlarını al
                            var vector = await _aiService.GetEmbeddingAsync(textToEmbed);

                            if (vector != null)
                            {
                                // Qdrant Vector DB'ye kaydet
                                bool saved = await _vectorDbService.SaveVectorAsync(
                                    collectionName: "lesson_contents",
                                    contentId: content.Id,
                                    lessonId: content.LessonId,
                                    vector: vector,
                                    originalText: textToEmbed
                                );

                                if (saved)
                                {
                                    await repo.MarkAsIndexedAsync(content.Id);
                                }
                            }

                            // Google Gemini API Rate Limit (429) engeline takılmamak için her istek arası 1.5 sn bekle
                            await Task.Delay(1500, stoppingToken);
                        }
                    }
                }
                catch (Exception)
                {
                    // Döngünün devamlılığı için hataları yutuyoruz
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}