using System.Collections.Generic;
using System.Threading.Tasks;

namespace MiniLms.Interfaces
{
    public interface IVectorDbService
    {
        // Qdrant'ta "lesson_contents" adında bir alan yoksa veya boyutu farklıysa otomatik oluşturur
        Task EnsureCollectionExistsAsync(string collectionName, int vectorSize = 768);

        // Vektörleştirilmiş içeriği Qdrant'a kaydeder
        Task<bool> SaveVectorAsync(string collectionName, int contentId, int lessonId, List<float> vector, string originalText);

        // Kullanıcının sorusuna anlamsal olarak en yakın metinleri getirir
        Task<List<string>> SearchSimilarTextsAsync(string collectionName, List<float> queryVector, int lessonId, int limit = 3, List<float>? vectorData = null);
        Task<List<string>> SearchSimilarTextsAsync(string collectionName, List<float> vectorData, int limit);
        Task<bool> DeleteVectorAsync(List<long>pointIds);
    }
}

