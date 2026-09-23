# Özetleme Sistemi Kesin Koruma Kuralı

- **`Services/AiService.cs`** içerisindeki **`CleanPdfText`** ve özetleme sistemine **KESİNLİKLE DOKUNMA**.
- **`Services/CourseDocumentService.cs`** içerisindeki **`GetDocumentTextChunksAsync`** metoduna **KESİNLİKLE DOKUNMA**.
- Kullanıcı sonraki hiçbir istemde (prompt) özel olarak talep etmedikçe bu dosyalardaki özet mekanizmasını ve ilgili metodları değiştirme, silme veya refactor etme.
