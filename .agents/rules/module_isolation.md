# Modül İzolasyonu ve Bağımsız Çalışma Kuralı

- **Kullanıcı açıkça belirtmedikçe çalışma yapılan modül dışındaki diğer modüllerin kodlarına KESİNLİKLE DOKUNMA.**
- **Quiz Modülü Örneği:** Kullanıcı Quiz modülüyle ilgili bir istem verdiğinde; Sesli Özetleme (`AzureSpeechService.cs`, `GetAudioSummary`), Metin Özetleme (`CleanPdfText`, `GetDocumentTextChunksAsync`, `DocumentSummaries`) veya diğer ilişkisiz sistemlerin kodları değiştirilmeyecek, silinmeyecek ve refactor edilmeyecektir.
- Yapılan değişiklikler ve kod düzenlemeleri sadece kullanıcının belirttiği ilgili modül ile sınırlandırılacaktır.
