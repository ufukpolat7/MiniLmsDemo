# 🎓 MiniLMS AI - Akıllı Öğrenme Yönetim Sistemi

MiniLMS AI; **ASP.NET Core 8.0 MVC**, **Google Gemini LLM**, **Qdrant Vektör Veritabanı (RAG)**, **Azure Neural TTS** ve açık kaynaklı **MediaCMS Video Sunucusu** entegrasyonuyla geliştirilmiş yeni nesil akıllı bir Öğrenme Yönetim Sistemidir (LMS).

---

## 🌟 Temel Özellikler

### 🤖 1. Yapay Zekâ Destekli Doküman Analitiği & Özetleme
- **Çoklu Format Desteği:** PDF (PdfPig) ve PowerPoint (.pptx/.ppt - yerel XML ayrıştırıcı) ders notlarını otomatik okuma.
- **Akıllı Özetleme:** Google Gemini LLM ile pedagojik seviyelere göre yapılandırılmış Markdown formatında ders özetleri çıkarma.
- **LLM Fallback Motoru:** API kotaları veya kesintilerine karşı yedekli model geçişi (`gemini-3.6-flash` ➔ `gemini-3.5-flash-lite`).

### 🎙️ 2. Sesli Özet & Doğal Anlatım (Azure Speech TTS)
- **Doğal Türkçe Ses:** Microsoft Azure Speech SDK (`tr-TR-EmelNeural`) ile özetleri bir öğretmenin anlatım dilinde seslendirme.
- **Akıllı Önbellekleme (Caching):** Üretilen ses kayıtlarının diskte önbelleğe alınması ile tekrarlı dinlemelerde sıfır API maliyeti.

### 🧠 3. RAG Mimarisi & Akıllı Ders Asistanı (Qdrant)
- **Vektörel Embedding:** Doküman paragraflarının 768 boyutlu embedding vektörlerine dönüştürülmesi.
- **Semantik Arama:** Qdrant Cloud / Local Vector DB üzerinde kosinüs benzerliği ile arama.
- **Halüsinasyonsuz Asistan:** Öğrenci sorularının yalnızca yüklenen ders dokümanlarına sadık kalarak yanıtlanması.

### 🎯 4. Bloom Taksonomili Sınav Motoru & Eksiklerim Analitiği (Knowledge Gap Engine)
- **Bloom Seviyeli Quizler:** Hatırlama, Kavrama ve Analiz düzeylerinde çoktan seçmeli test üretimi.
- **Öğretmen Onay ve Not Akışı:** Eğitmenin soruları inceleyip pedagojik not ekleyerek sınıfa yayınlaması (`Publish`).
- **Hata Takibi & Semantik Konu Kümeleme:** Öğrencinin yanlış cevaplarının kaydedilmesi ve konulara göre gruplanması.
- **Kişiye Özel Telafi Sınavları:** *"🎯 Bu Konudan Quiz Çöz"* ve *"🔀 Derse Özel Karışık Quiz"* ile doğrudan zayıf olunan konulara odaklanma.

### 🎬 5. MediaCMS Video Streaming & Playlist Yönetimi
- **Açık Kaynak Medya Sunucusu:** Video barındırma ve HLS/MP4 akışının bağımsız MediaCMS sunucusu üzerinden sağlanması.
- **Otomatik Kategori & Playlist Eşleme:** Her ders için MediaCMS üzerinde otomatik kategori ve oynatma listesi senkronizasyonu.
- **Entegre Video Yükleme:** Eğitmenlerin doğrudan LMS arayüzünden seçtikleri videoları MediaCMS'e yükleyebilmesi.

---

## 🏗️ Mimari & Teknoloji Yığını

| Katman | Teknoloji / Kütüphane |
| :--- | :--- |
| **Backend** | .NET 8.0 / ASP.NET Core MVC |
| **ORM & Veritabanı** | Entity Framework Core (Code-First) / MS SQL Server |
| **Kimlik Doğrulama** | ASP.NET Core Identity & Policy-Based Authorization |
| **Yapay Zekâ (LLM)** | Google Gemini API (gemini-3.6-flash, gemini-3.5-flash-lite) |
| **Vektör Veritabanı** | Qdrant Cloud / Local Vector Database |
| **Ses Sentezleme** | Azure Cognitive Services Speech SDK |
| **Video & Medya CMS** | MediaCMS REST API & Streaming Server |
| **Frontend** | Bootstrap 5, jQuery, CSS3 Glassmorphism UI |

---

## 🚀 Kurulum ve Çalıştırma

### 1. Projeyi Klonlayın
```bash
git clone https://github.com/KULLANICI_ADINIZ/MiniLms.git
cd MiniLms
```

### 2. Konfigürasyon Ayarları
`MiniLms/appsettings.example.json` dosyasını kopyalayarak `MiniLms/appsettings.json` adıyla kaydedin ve gerekli API anahtarlarınızı girin:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=MiniLMSDb;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Gemini": {
    "ApiKey": "YOUR_GEMINI_API_KEY"
  },
  "AzureSpeech": {
    "SubscriptionKey": "YOUR_AZURE_SPEECH_KEY",
    "Region": "westeurope"
  },
  "Qdrant": {
    "Url": "https://YOUR_QDRANT_CLUSTER_URL.aws.cloud.qdrant.io",
    "ApiKey": "YOUR_QDRANT_API_KEY"
  },
  "MediaCms": {
    "BaseUrl": "http://localhost",
    "ApiUrl": "http://localhost/api/v1",
    "ApiToken": "YOUR_MEDIACMS_API_TOKEN"
  }
}
```

### 3. Veritabanını Güncelleyin
```bash
cd MiniLms
dotnet ef database update
```

### 4. (Opsiyonel) MediaCMS Video Sunucusunu Başlatın
Video akış özelliklerini yerel ortamda çalıştırmak için:
```bash
docker compose -f docker-compose.mediacms.yml up -d
```
> 📌 **Not:** MediaCMS çalıştırılmasa dahi sistemin tüm AI, Quiz, Doküman ve Özetleme modülleri hata toleranslı biçimde kesintisiz çalışır.

### 5. Uygulamayı Başlatın
```bash
dotnet run
```
Tarayıcınızdan `http://localhost:5276` (veya belirtilen port) adresine gidin.

---

## 🔒 Lisans
Bu proje eğitim ve araştırma amacıyla geliştirilmiştir.
