# AGENTS.md - Proje Kuralları

## 🔒 Özetleme Sistemi Kesin Koruma Kuralı

1. **`Services/AiService.cs`** içerisindeki **`CleanPdfText`** ve özetleme sistemine **ASLA VE KESİNLİKLE DOKUNMA**.
2. **`Services/CourseDocumentService.cs`** içerisindeki **`GetDocumentTextChunksAsync`** metoduna **ASLA VE KESİNLİKLE DOKUNMA**.
3. Gelecekteki hiçbir istemde (prompt) kullanıcı açıkça "özet sistemini değiştir" demediği sürece bu kodlar dondurulmuştur ve korunacaktır.

## 🎯 Modül İzolasyonu ve Sınırlı Çalışma Kuralı

1. **Kullanıcı açıkça belirtmedikçe ilişkisiz modüllerin kodlarına ASLA DOKUNMA.**
2. Örnek: Kullanıcı **Quiz** ile ilgili bir geliştirme/hata düzeltme istediğinde; **Sesli Özet** (`AzureSpeechService.cs`, `GetAudioSummary`), **Metin Özetleme** (`CleanPdfText`, `DocumentSummaries`) veya diğer modüllerin kodları **ASLA değiştirilmeyecektir**.
3. Yapılacak tüm değişiklikler yalnızca kullanıcının talep ettiği hedef modül ile sınırlandırılacaktır.
